﻿module Fez.Compiler

open System
open System.Reflection
open System.IO
open Microsoft.FSharp.Compiler.SourceCodeServices
open Microsoft.FSharp.Compiler.SourceCodeServices.BasicPatterns

module B = BasicPatterns

module Map =
    let merge m1 m2 =
        Map.fold (fun s k v -> Map.add k v s) m1 m2

[<AutoOpen>]
module Util =
    let (|FullPath|) file = Path.GetFullPath file

    let (++) = Array.append
    let run = Async.RunSynchronously
    let (|Item|_|) = Map.tryFind
    let (|ToList|) = Seq.toList

    let (|FileExists|_|) f path =
        let path = f path
        if File.Exists path then Some path else None
    // compiler stuff
    (* let localPath = Path.GetDirectoryName(typeof<TypeInThisAssembly>.GetTypeInfo().Assembly.Location) *)
    let compilerArgs () =
        let fsharpCoreLib = typeof<Microsoft.FSharp.Core.MeasureAttribute>.GetTypeInfo().Assembly.Location
        let fezCoreLib = typeof<Fez.Core.Pid>.GetTypeInfo().Assembly.Location
        let systemCoreLib = typeof<System.Object>.GetTypeInfo().Assembly.Location
        let systemLinqLib = typeof<System.Linq.IQueryable>.GetTypeInfo().Assembly.Location
        let sysPath = Path.GetDirectoryName(systemCoreLib)
        let sysLib name = Path.Combine(sysPath, name + ".dll")
        (* let localLib name = Path.Combine(localPath, name + ".dll") *)
        let resolve ref =
            match ref with
            | FileExists sysLib path -> path
            (* | FileExists localLib path -> path *)
            | ref -> failwithf "Cannot locate reference %s" ref
        [|
            "--define:FEZ"
            "--noframework"
            "--nologo"
            "--simpleresolution"
            "--nocopyfsharpcore"
            "--warn:3"
            "--fullpaths"
            "--flaterrors"
            "--target:library"
            "--targetprofile:netcore"
            "-r:" + systemCoreLib
            "-r:" + resolve "mscorlib"
            "-r:" + resolve "System.Collections"
            "-r:" + resolve "System.Diagnostics.Debug"
            "-r:" + resolve "System.IO"
            "-r:" + resolve "System.Linq"
            "-r:" + resolve "System.Linq.Expressions"
            "-r:" + resolve "System.Reflection"
            "-r:" + resolve "System.Runtime"
            "-r:" + resolve "System.Runtime.Numerics"
            "-r:" + resolve "System.Runtime.Extensions"
            "-r:" + resolve "System.Threading"
            "-r:" + resolve "System.Threading.Tasks"
            "-r:" + resolve "System.Text.RegularExpressions"
            "-r:" + fsharpCoreLib
            "-r:" + fezCoreLib
        |]

    let projectOptions (checker: FSharpChecker) files =
        {FSharpProjectOptions.ProjectFileName = "Test"
         SourceFiles = List.toArray files
         Stamp = Some 0L
         (* ProjectFileNames = [||] *)
         OtherOptions =
             [|"-o:Test.dll"; "-a"|]
             ++ compilerArgs ()
         ReferencedProjects = [||]
         IsIncompleteTypeCheckEnvironment = false
         UseScriptResolutionRules = false
         LoadTime = DateTime.Now
         UnresolvedReferences = None
         OriginalLoadReferences = []
         ExtraProjectInfo = None}


[<AutoOpen>]
module Compiler =

    let rec nonAbbreviatedType (t: FSharpType) =
        if t.IsAbbreviation then
            nonAbbreviatedType t.AbbreviatedType
        else t

    let inline (|NonAbbreviatedType|) (t: FSharpType) =
        nonAbbreviatedType t

    let (|TypeDefinition|_|) (NonAbbreviatedType t) =
        if t.HasTypeDefinition then
            Some t.TypeDefinition
        else None

    let (|IsFSharpOption|_|) =
        function
        | TypeDefinition tdef as t
            when tdef.TryFullName =
                Some "Microsoft.FSharp.Core.FSharpOption`1" ->
                    Some t
        | _ -> None

    let (|IsFSharpResult|_|) =
        function
        | TypeDefinition tdef as t
            when tdef.TryFullName =
                Some "Microsoft.FSharp.Core.FSharpResult`2" ->
                    Some t
        | _ -> None

    let (|IsFSharpList|_|) =
        function
        | TypeDefinition tdef as t
            when tdef.TryFullName =
                Some "Microsoft.FSharp.Collections.FSharpList`1" ->
                    Some t
        | _ -> None

    let (|IsCase|_|) caseName (c : FSharpUnionCase) =
        if c.CompiledName = caseName then
            Some caseName
        else None

    let (|IsCtor|_|) (c : FSharpMemberOrFunctionOrValue) =
        if c.CompiledName = ".ctor" then
            Some c
        else None


    let (|HasModCallAttribute|_|) (m :  FSharpMemberOrFunctionOrValue) =
        // TODO check all items in list
        match Seq.toList m.Attributes with
        | [a] when a.AttributeType.FullName = "Fez.Core.ModCall" ->
            Some (Seq.toList a.ConstructorArguments, m)
        | _ -> None
    (* let (|CaseName|) (c : FSharpUnionCase) = *)
    (*     c.CompiledName |> cerl.Atom *)

    let (|IsField|_|) fieldName (c : FSharpField) =
        if c.Name = fieldName then
            Some fieldName
        else None

    let check (checker : FSharpChecker) options (FullPath file) fileContents =
        let res = checker.ParseAndCheckProject options |> run
        if not (Array.isEmpty res.Errors) || res.HasCriticalErrors then
            failwithf "Errs %A" res.Errors
        res

    let lowerFirst s =
        if String.exists Char.IsLower s then
            String.mapi (fun i c -> if i = 0 then Char.ToLower c else c) s
        else s

    let fezUnit =
        cerl.Exp (cerl.Constr (cerl.Lit (cerl.LAtom (cerl.Atom "unit"))))

    let (|IsFezUnit|_|) e =
        match e with
        | cerl.Exp (cerl.Constr (cerl.Lit (cerl.LAtom (cerl.Atom "unit")))) ->
            Some e
        | _ -> None

    let rec stripFezUnit args =
        match args with
        | [IsFezUnit _] -> []
        | args -> args

    let rec stripFezUnits args = [
        match args with
        | IsFezUnit _ :: tail ->
            yield! stripFezUnit tail
        | a :: tail ->
            yield a
            yield! stripFezUnit tail
        | [] -> ()
    ]

    let filterUnitVars =
        List.choose (fun (x : FSharpMemberOrFunctionOrValue) ->
            if x.FullName.StartsWith("unitVar") then None
            else Some x.FullName)

    type Ctx =
        { Module: string
          Names : Map<cerl.Var, int>
          Functions : Map<cerl.Var, cerl.Function>
          // names that evaluate to a literal unit
          UnitNames : Set<string>
          Members : Set<string> } // module member FullNames
       with static member init m =
               {Module = m
                Names = Map.empty
                Functions = Map.empty
                UnitNames = Set.empty
                Members = Set.empty}

    let mkName (name : string) num =
        // TODO do replacements better
        let name = name
                    .Replace(''', '_')
        if Char.IsUpper name.[0] then
            sprintf "%s%i" name num
        else
            sprintf "_%s%i" name num

    let safeVar incr ({Names = nameMap} as ctx) (x : cerl.Var) : cerl.Var * Ctx =
        match incr, nameMap with
        | false, Item x num ->
            mkName x num, ctx
        | true, Item x num ->
            let num' = num + 1
            mkName x num', {ctx with Names = Map.add x num' nameMap}
        | _ ->
            mkName x 0, {ctx with  Names = Map.add x 0 nameMap}

    let uniqueName ctx =
        // TODO: append some random stuff to reduce the chance of name collisions
        safeVar true ctx "fez"

    let foldNames (ctx : Ctx) f xs =
        let xs, ctx = List.fold (fun (xs, nm) x ->
                                    let x, nm = f nm x
                                    x :: xs, nm) ([], ctx) xs
        List.rev xs, ctx

    let altExpr = cerl.altExpr
    let boolPat tf = (cerl.Pat (cerl.PLit (cerl.LAtom (cerl.Atom tf))))

    type ArgType =
        | Tupled of string list
        | Singled of string

    let (|Gumpf|_|) (m: FSharpMemberOrFunctionOrValue) =
        let gumpf = ["Equals"; "CompareTo"; "GetHashCode"]
        if List.exists ((=) m.CompiledName) gumpf then
            Some ()
        else None

    let (|FlatParameters|) (pgs : FSharpMemberOrFunctionOrValue list list) =
            let ps = pgs |> List.fold List.append []
            match ps with
            | [x] when x.FullName.StartsWith("unitVar") ->
                []
            | xs ->
                xs |> List.map (fun x -> x.FullName)
    let (|Parameters|) (pgs : FSharpMemberOrFunctionOrValue list list) =
        (* let ps = pgs |> List.fold List.append [] *)
        match pgs with
        | [[x]] when x.FullName.StartsWith("unitVar") ->
            []
        | xs ->
            xs
            |> List.map (function
                         | [x] -> Singled x.FullName
                         | args ->
                            Tupled (args |> List.map (fun x -> x.FullName)))


    let inspectT (t: FSharpType) =
        t.TypeDefinition.Namespace,
        t.TypeDefinition.CompiledName,
        t.TypeDefinition.LogicalName,
        t.TypeDefinition.FullName



    let varExps name =
        cerl.Constr (cerl.Var name) |> cerl.Exp

    let mapArgs (ctx : Ctx) f xs =
        List.map (f ctx >> fst) xs

    let constr x =
        cerl.Exp (cerl.Constr x)

    let unconstr =
        function
        | cerl.Exp (cerl.Constr c) -> c
        | _ -> failwith "not a constr"

    let litAtom = cerl.litAtom

    let litInt i =
        cerl.Lit (cerl.LInt i)

    let litFlt i =
        cerl.Lit (cerl.LFloat i)

    let litChar s =
        cerl.Lit (cerl.LChar s)

    let litString s =
        cerl.Lit (cerl.LString s)

    let modCall left right exps =
        cerl.ModCall ((left, right), exps)

    let apply f args =
        cerl.App (f, args)

    let lambda args expr =
        cerl.Lambda (args, expr)

    let rec flattenLambda parms l =
        match parms, l with
        | _, cerl.Exp (cerl.Constr (cerl.Lambda ([v] , exps))) ->
            flattenLambda (v :: parms) exps
        | [], _ -> l
        | _, _ ->
            cerl.Exp (cerl.Constr (cerl.Lambda (List.rev parms, l)))

    let mkLet v a expr =
        cerl.Let (([v], a), flattenLambda [] expr)

    let mkFunction ({Functions = funs} as ctx) name arity =
        let f = cerl.Function (cerl.Atom name, arity)
        f, {ctx with Functions = Map.add name f funs}

    let funDef f (expr) =
        cerl.FunDef (cerl.Constr f, cerl.Constr expr)

    let lAtomPat name =
        cerl.PLit (cerl.LAtom (cerl.Atom name))

    let mkType (t:FSharpType) wrap =
        t.TypeDefinition.FullName |> wrap

    let mkTypeTag (t: FSharpType) =
        mkType t (litAtom >> constr)

    let mkUnionTag (uc : FSharpUnionCase) =
        uc.Name |> litAtom |> constr

    let mkTypePat (t: FSharpType) =
        mkType t lAtomPat

    let mkUnionPat (uc : FSharpUnionCase) =
        uc.Name |> lAtomPat

    let trueExps = litAtom "true" |> constr
    let falseExps = litAtom "false" |> constr
    let mkAlt p g e =
        cerl.Constr (cerl.Alt (cerl.Pat p, g, e))

    let annLAtom n = litAtom n |> constr
    let erlang = litAtom "erlang" |> constr
    let notEquals = litAtom "/=" |> constr
    let equals = litAtom "=:=" |> constr
    let fez = litAtom "fez" |> constr

    let mkStructuralUnionCasePat (t: FSharpType) (uc: FSharpUnionCase) =
        let typeTag = mkTypePat t
        let caseTag = mkUnionPat uc
        let fields =
            uc.UnionCaseFields
            |> Seq.map (fun cf -> cerl.PVar "_")
            |> Seq.toList
        cerl.PTuple (typeTag :: caseTag :: fields)

    let wrapArgs (args: ArgType list) expsFun ctx =
        let wrap ctx caseExps pat =
            let e, ctx = expsFun ctx
            let a1 = altExpr (pat, cerl.defaultGuard, e)
            let a2 = altExpr (pat, cerl.defaultGuard,
                              constr (cerl.matchFail [litAtom "function_clause" |> constr]))
            // OTP 20 seems to require this third case. why?
            cerl.Case(caseExps, [a1;a2]), ctx
        // the args being passed into the function - just generate a random name here
        let inArgs, ctx = foldNames ctx (fun nm _ -> uniqueName nm) args
        let inter = inArgs |> List.map (cerl.Var >> cerl.Constr)
        let caseExpr = cerl.Exps (cerl.Ann (inter, []))
        if List.exists (function | Tupled _ -> true | _ -> false) args then
            // there are tupled args we need to unpack them
            let ms, ctx = args
                          |> List.fold (fun (pats, ctx) ->
                              function
                              | Singled a ->
                                  let a, ctx = safeVar false ctx a
                                  cerl.PVar a :: pats, ctx
                              | Tupled args ->
                                  let x, ctx = foldNames ctx (safeVar false) args
                                  let x = List.map cerl.PVar x
                                  cerl.PTuple x :: pats, ctx) ([], ctx)

            let ms = List.rev ms
            let p = cerl.Pats ms
            let e, ctx = wrap ctx caseExpr p
            inArgs, constr e, ctx
        else
            let args = List.map (function | Singled a -> a | _ -> failwith "boo") args
            let args, ctx = foldNames ctx (safeVar false) args
            let e, ctx = expsFun ctx
            args, e, ctx

    let mkTypeCheck (t: FSharpType) name =
        let ln = t.TypeDefinition.LogicalName
        match ln with
        | "int" ->
            cerl.ModCall((erlang, litAtom "is_integer" |> constr), [name]) |> constr
        | "string" ->
            cerl.ModCall((erlang, litAtom "is_list" |> constr), [name]) |> constr
        | x -> failwithf "mkTypeCheck not impl %A" x

    let mkErlangTermCasePat ctx (t: FSharpType) inclTag (uc: FSharpUnionCase) =
        match Seq.toList uc.UnionCaseFields with
        | [] -> //no args match on atom
            lowerFirst uc.Name |> lAtomPat, cerl.defaultGuard
        | [a] when not inclTag ->
            let name, ctx = uniqueName ctx
            let guard = cerl.Guard (mkTypeCheck (a.FieldType) (cerl.Var name |> constr))
            let field = cerl.PVar name
            field, guard
        | fields ->
            // turn field into nested guard type check expression
            let nameChecks, ctx =
                fields
                |> foldNames ctx (fun ctx f ->
                    let name, ctx = uniqueName ctx
                    let check = mkTypeCheck f.FieldType (cerl.Var name |> constr)
                    (name, check), ctx)
            let checks = nameChecks |> List.map snd
            let patterns = nameChecks |> List.map (fst >> cerl.PVar)
            let patterns =
                if inclTag then
                    let tag = lowerFirst uc.Name |> lAtomPat
                    tag :: patterns
                else patterns

            let falseExp = litAtom "false"
            let trueExp = litAtom "true"
            let third, ctx = uniqueName ctx
            let wrap ifExps thenExps =
                let a1 = altExpr (boolPat "true", cerl.defaultGuard, constr thenExps)
                let a2 = altExpr (boolPat "false", cerl.defaultGuard, constr falseExp)
                // OTP 20 seems to require this third case. why?
                let a3 = altExpr (cerl.Pat (cerl.PVar third), cerl.defaultGuard,
                                  constr (cerl.Var third))
                cerl.Case(ifExps, [a1;a2; a3])

            let state = wrap (List.head checks) trueExp
            let res =
                List.tail checks
                |> List.fold (fun s c -> wrap c s) state

            cerl.PTuple patterns, cerl.Guard (constr res)

    let put k v =
        let put = litAtom "put" |> constr
        modCall erlang put [k; v]

    let mkTryAfter ctx e after =
        let sucVal, ctx = uniqueName ctx
        let sucExps = cerl.Seq (after, constr (cerl.Var sucVal)) |> constr
        let ofs = [sucVal], sucExps
        let n1, ctx = uniqueName ctx
        let n2, ctx = uniqueName ctx
        let n3, ctx = uniqueName ctx
        let reRaise = cerl.Op (cerl.Atom "raise",
                               [constr (cerl.Var n3); constr (cerl.Var n2)])
        let afterExps = cerl.Seq (after, constr reRaise) |> constr
        let catch = [n1;n2;n3], afterExps
        cerl.Try (e, ofs, catch) |> constr, ctx


    let (|Intr2Erl|_|) (f: FSharpMemberOrFunctionOrValue) =
        match f.LogicalName with
        | "op_Multiply" -> Some "*"
        | "op_Addition" -> Some "+"
        | "op_Subtraction" -> Some "-"
        | "op_LessThan" -> Some "<"
        | "op_GreaterThan" -> Some ">"
        | "op_Equality" -> Some "=:="
        | _ -> None

    let castLiteral (o: obj) =
        match o with
        | :? int as i -> cerl.LInt (int64 i) |> Some
        | :? float as f -> cerl.LFloat f |> Some
        | :? float32 as f -> cerl.LFloat (float f) |> Some
        | :? char as c -> cerl.LChar c |> Some
        | :? string as s -> cerl.LString s |> Some
        | x -> None

    let (|Lit|_|) = castLiteral

    let tupleGet idx e =
        let element = litAtom "element" |> constr
        let idx = idx+1L |> cerl.LInt
        modCall erlang element [cerl.Lit idx |> constr; e]

    let tupleSet idx e v =
        let setelement = litAtom "setelement" |> constr
        let idx = idx+1L |> cerl.LInt
        modCall erlang setelement [cerl.Lit idx |> constr; e; v]

    let toLowerString (o:obj) =
        o.ToString().ToLower()

    type BS<'t> = cerl.BitString<'t>

    let mapConst (o : obj) (t: FSharpType) =
        let td = t.TypeDefinition
        match o with
        | :? int as i -> litInt (int64 i)
        | :? int64 as i -> litInt i
        | :? float as i -> litFlt i
        | :? float32 as i -> litFlt (float i)
        | :? char as c -> litChar c
        | :? string as s -> litString s
        | :? bool as b -> litAtom (toLowerString b)
        | :? array<byte> as bytes ->
            let bin = bytes
                      |> Array.map (BS<_>.fromByte)
                      |> Array.toList
                      |> cerl.Binary
            bin
        | null -> //unit
            litAtom "unit" // Special casing a value here for unit for now
        | x -> failwithf "mapConst: not impl %A" x

    let ioLibFormat nm str t args =
        let io = annLAtom "io_lib"
        let format = annLAtom "format"
        let arg1 = mapConst str t |> constr
        let args = [arg1; cerl.List (cerl.L args) |> constr]
        modCall io format args |> constr, nm
    // flatten nested single parameter lambdas
    // this will reverse the arguments but that is typically ok for
    // a first class fun in erlang
    let (|ExprType|_|) ts (e: FSharpExpr) =
        if e.Type.TypeDefinition.LogicalName = ts then Some e
        else None

    let (|IsMemberOn|_|) t (f: FSharpMemberOrFunctionOrValue) =
        if f.IsMember && f.LogicalEnclosingEntity.LogicalName = t then
            Some f
        else None

    let (|IsModuleMemberOn|_|) t (f: FSharpMemberOrFunctionOrValue) =
        if f.IsModuleValueOrMember && f.LogicalEnclosingEntity.LogicalName = t then
            Some f
        else None

    let (|LogicalName|_|) t (f: FSharpMemberOrFunctionOrValue) =
        if f.LogicalName = t then Some ()
        else None

    let (|IsUnitArg|_|) (f : FSharpMemberOrFunctionOrValue) =
        if f.FullType.ToString() = "type Microsoft.FSharp.Core.unit" then Some f
        else None

    let (|IsUnit|_|) (f : FSharpType) =
        if f.ToString() = "type Microsoft.FSharp.Core.unit" then Some f
        else None

    // mungle strings
    let safe (s: string) =
        s.Replace("`", "_")

    let safeAtom (n: string) =
        safe n |> litAtom |> constr

    /// used for member
    let qualifiedMember (f : FSharpMemberOrFunctionOrValue) =
        let fe = f.EnclosingEntity.Value
        let name = f.LogicalName
        let eeFullName = fe.FullName
        let eeName = fe.CompiledName
        let idx = eeFullName.IndexOf(eeName)
        let mn = eeFullName.Substring(0, idx-1)
        (* let tn = eeFullName.Substring(idx, eeFullName.Length-1) *)
        let m = safeAtom mn
        let f = eeName + "." + name |> safeAtom
        m, f

    let fezCore = litAtom "Fez.Core" |> constr

    let fastIntegerLoop fe te e =
        let fil = litAtom "fast_integer_loop" |> constr
        modCall fezCore fil [fe;te;e]

    let traitCall name (args : cerl.Exps list) =
        let traitCall = litAtom "trait_call" |> constr
        let instance = args.[0]
        let listArgs = cerl.List(cerl.L args) |> constr
        modCall fezCore traitCall [instance; litAtom name |> constr; listArgs]

    let multiDispatch name (args : cerl.Exps list) =
        let multi = litAtom "multi_dispatch" |> constr
        let instance = args.[0]
        let listArgs = cerl.List (cerl.L args) |> constr
        modCall fezCore multi [instance; litAtom name |> constr; listArgs]

    let eraseUnit (exprs : FSharpExpr list) =
        match exprs with
        | [e] when e.Type.HasTypeDefinition &&
                   e.Type.TypeDefinition.CompiledName = "unit" ->
            []
        | _ ->
            exprs

    let rec allBaseTypes (t: FSharpType) = [
        match t.TypeDefinition.BaseType with
        | Some bt when bt.HasTypeDefinition ->
            let bt = nonAbbreviatedType bt
            match bt.TypeDefinition.TryFullName with
            | Some fn -> yield fn
            | None ->
                yield bt.TypeDefinition.QualifiedName
            yield! allBaseTypes bt
        | Some bt ->
            yield bt.ToString()
            yield! allBaseTypes bt
        | None -> () ]

    let memberFunctionName (f:  FSharpMemberOrFunctionOrValue) =
        sprintf "%s.%s" f.LogicalEnclosingEntity.LogicalName f.LogicalName
        |> safeAtom

    //hacky way to check if a type is a library type and thus not implemented
    //in the same way as custom types
    let isStandardLibraryType (fe: FSharpEntity) =
        fe.FullName.StartsWith("Microsoft") ||
        fe.FullName.StartsWith("System.")

    let rec translateCall nm callee
                          (f : FSharpMemberOrFunctionOrValue)
                          (argTypes: FSharpType list)
                          (exprs : FSharpExpr list) : (cerl.Exps * Ctx) =
        let fe = f.EnclosingEntity.Value
        let typeHasMfv (e: FSharpExpr) =
            if e.Type.HasTypeDefinition then
                e.Type.TypeDefinition.MembersFunctionsAndValues
                |> Seq.exists (fun s -> s.LogicalName = f.LogicalName)
            else false

        let rec makeTupleArgs (args : cerl.Exps list) parms acc =
            match args, parms with
            | [], _ ->
                List.rev acc
            | arg :: remArgs, parm :: parms ->
                // single arg
                match parm with
                | [_] ->
                    makeTupleArgs remArgs parms (arg :: acc)
                | _ ->
                    let arg =
                        List.take (List.length parm) args
                        |> cerl.Tuple |> constr
                    makeTupleArgs (args.[List.length parm ..]) parms (arg :: acc)
            | arg :: remArgs, [] ->
                makeTupleArgs remArgs [] (arg :: acc)

        let makeArgs nm (f : FSharpMemberOrFunctionOrValue) exprs =
            let p = f.CurriedParameterGroups
                    |> Seq.map Seq.toList |> Seq.toList
            // each args expression needs its own context
            let args =
                mapArgs nm processExpr exprs
            makeTupleArgs args p []

        let makeFlatArgs ctx (_ : FSharpMemberOrFunctionOrValue) exprs =
            // each args expression needs its own context
            mapArgs ctx processExpr exprs

        match callee, f, exprs with
        //special case mapping + on a string to ++
        | _, Intr2Erl "+", ExprType "string" _ :: _ ->
            let stringAppend = litAtom "++" |> constr
            let args, nm = foldNames nm processExpr exprs
            modCall erlang stringAppend args |> constr, nm
        // TODO: special case op_Equality to check for 'Equals' override
        | _, _, e :: _ when f.LogicalName.StartsWith("op_") && typeHasMfv e ->
            // type has overriden operator
            let args = makeFlatArgs nm f (eraseUnit exprs)
            let m = safeAtom e.Type.TypeDefinition.FullName
            let f = safeAtom f.LogicalName
            let args = args |> stripFezUnit |> List.map (flattenLambda [])
            modCall m f args |> constr, nm
        | _, Intr2Erl x, e :: _ ->
            let op = litAtom x |> constr
            let args, nm = foldNames nm processExpr exprs
            modCall erlang op args |> constr, nm
        | Some callee, LogicalName "get_Length"
                       & (IsMemberOn "String" _ | IsMemberOn "List`1" _), _ ->
            let length = litAtom "length" |> constr
            let arg, nm = processExpr nm callee
            // string length wont have any args
            // List.length has one arg - unit - ignoring it here
            modCall erlang length [arg] |> constr, nm
        | None, f, [e] when f.FullName = "Microsoft.FSharp.Core.Operators.string"
                            && e.Type.TypeDefinition.LogicalName = "string" ->
            //erase ToString on strings
            processExpr nm e
        | None, f, e when Set.exists ((=) f.FullName) nm.Members ->
            // local module call
            let name =
                if fe.IsFSharpModule then
                    f.LogicalName
                else
                    //method on type rather than nested module
                    fe.LogicalName + "." + f.LogicalName
            //add callee as first arg if method dispatch
            let args, nm =
                let args = makeFlatArgs nm f (eraseUnit exprs)
                let args = args |> stripFezUnit |> List.map (flattenLambda [])
                args, nm
            let numArgs = List.length args
            let func, nm = mkFunction nm name numArgs
            let func = func |> cerl.Fun |> constr
            let app = apply func args
            constr app,nm
        | None, f, _  when f.IsExtensionMember ->
            // extension member
            let args = makeFlatArgs nm f (eraseUnit exprs)
            // remove unit
            // flatten any lambda args
            let args = args |> stripFezUnit |> List.map (flattenLambda [])
            let m = fe.FullName |> safeAtom
            //member call
            let f = memberFunctionName f
            modCall m f args |> constr, nm
        | None, IsCtor f, _  -> //
            // ctors always use tupled args so we translate to
            // arity version for efficiency
            let args, nm =
                eraseUnit exprs
                |> foldNames nm processExpr
            // module call
            let m = safeAtom fe.FullName
            let f = safeAtom f.LogicalName
            let args = args |> stripFezUnit |> List.map (flattenLambda [])
            modCall m f args |> constr, nm
        | None, (HasModCallAttribute _ as f), _  ->
            let args = makeArgs nm f (eraseUnit exprs)
            // module call
            let m = safeAtom fe.FullName
            let f = safeAtom f.LogicalName
            let args = args |> stripFezUnit |> List.map (flattenLambda [])
            modCall m f args |> constr, nm
        | None, f, _  -> //
            let args = makeFlatArgs nm f (eraseUnit exprs)
            // module call
            let m = safeAtom fe.FullName
            let f = safeAtom f.LogicalName
            let args = args |> stripFezUnit |> List.map (flattenLambda [])
            modCall m f args |> constr, nm
        | Some o, f, _ ->
            // attempt dispatch on object member by adding the "dispatchee"
            // as the first argument to the function
            let name = f.LogicalName
            // each args expression needs its own context
            let args = makeFlatArgs nm f (eraseUnit exprs)
            let oArg, ctx = processExpr nm o
            // flatten any lambda args
            let stripFezUnit args =
                // first arg is the dispatch object
                // so we need special version here
                match args with
                | [o; IsFezUnit _] -> [o]
                | args -> args
            let args = oArg :: args
                       |> stripFezUnit
                       |> List.map (flattenLambda [])

            let oType = nonAbbreviatedType o.Type
            if fe.IsInterface then
                // use trait call for dispatch on inteface as it will use the
                // embedded type info (if available) to dispatch the call to
                // the right function else we'd need a function
                // of format: 'Module:InterfaceType.Member'
                traitCall name args |> constr, nm
            elif fe.IsClass && not <| isStandardLibraryType fe  then
                // how to distinquish between standard sealed apis
                multiDispatch name args |> constr, nm
            elif f.IsExtensionMember then
                let f = memberFunctionName f
                let m = safeAtom fe.FullName
                modCall m f args |> constr, nm
            else
                let bts = allBaseTypes oType
                //TODO check if fe is different from o.Type
                // if so we may be dispatching on a super type
                let m = safeAtom fe.FullName
                let f = safeAtom name
                modCall m f args |> constr, nm
        | x ->
            failwithf "translateCall: not implemented %A" x

    and processDT nm (expsLookup : Map<int, FSharpMemberOrFunctionOrValue list * FSharpExpr>) expr =
        match expr with
        | B.Let ((v, e), expr) ->
            // ignore names introduced in the variable assignment expression
            let ass, _ = processExpr nm e
            let ass = flattenLambda [] ass
            let v', nm = safeVar true nm v.LogicalName
            let next, nm = processDT nm expsLookup expr
            mkLet v' ass (constr next), nm
        | B.IfThenElse(fi, neht, esle) ->
            let ifExps, nm = processExpr nm fi
            let thenExpr, nm = processDT nm expsLookup neht
            let elseExpr, nm = processDT nm expsLookup esle
            let a1 = altExpr (boolPat "true", cerl.defaultGuard, constr thenExpr)
            let a2 = altExpr (boolPat "false", cerl.defaultGuard, constr elseExpr)
            cerl.Case(ifExps, [a1;a2]), nm
        | B.DecisionTreeSuccess(i, []) ->
            let mfvs, expr = expsLookup.[i]
            let e, nm = processExpr nm expr
            match e with
            | cerl.Exp (cerl.Constr e) -> e, nm
            | _ -> failwith "no"
        | B.DecisionTreeSuccess(i, valueExprs) ->
            let mfvs, expr = expsLookup.[i]
            let mfvs = mfvs |> List.map (fun v -> v.CompiledName)
            // process expression and wrap in multi let
            let assignments, mn =
                List.zip mfvs valueExprs
                |> List.fold (fun (agg, nm) (v, e) ->
                                let v, nm = safeVar false nm v
                                let e, nm = processExpr nm e
                                ((v, e) :: agg), nm) ([], nm)

            let e, _ = processExpr nm expr

            let vls = List.map fst assignments
            let es = List.choose (fun (_, e) ->
                                    match e with
                                    | cerl.Exp ae -> Some ae
                                    | _ -> None) assignments
            cerl.Let ((vls, cerl.Exps (cerl.Constr es)), e), nm
        | e -> failwithf "processDT unexpected %A" e


    and processExpr nm (expr : FSharpExpr) : (cerl.Exps * Ctx) =
        let element nm idx e =
            let el = litAtom "element" |> constr
            let e, nm = processExpr nm e
            let idx = cerl.LInt idx
            modCall erlang el [cerl.Lit idx |> constr; e] |> constr, nm

        let (|ErlangTerm|_|) (t: FSharpType) =
            let et = t.TypeDefinition.Attributes
                     |> Seq.tryFind (fun a ->
                         a.AttributeType.LogicalName = "ErlangTerm")
            match et with
            | Some a ->
                let inclTags =
                    a.NamedArguments
                    |> Seq.exists (fun (t, name, _, b)  ->
                        name = "IncludeTagsWithTuples" && b :?> bool)
                Some (t, inclTags)
            | None -> None

        match expr with
        | B.UnionCaseTest (e, IsFSharpList t, IsCase "Cons" c) ->
            let a1, nm = processExpr nm e
            let a2 = cerl.Lit (cerl.LNil) |> constr
            modCall erlang notEquals [a1; a2] |> constr, nm
        | B.UnionCaseTest (e, IsFSharpList t, IsCase "Empty" c) ->
            let a1, nm = processExpr nm e
            let a2 = cerl.Lit (cerl.LNil) |> constr
            modCall erlang equals [a1; a2] |> constr, nm
        | B.UnionCaseTest (e, IsFSharpOption t, IsCase "Some" c) ->
            let a1, nm = processExpr nm e
            let a2 = litAtom "undefined" |> constr
            modCall erlang notEquals [a1; a2] |> constr, nm
        | B.UnionCaseTest (e, IsFSharpOption t, IsCase "None" c) ->
            let a1, nm = processExpr nm e
            let a2 = litAtom "undefined" |> constr
            modCall erlang equals [a1; a2] |> constr, nm
        | B.UnionCaseTest (e, IsFSharpResult t, IsCase "Ok" c) ->
            let a1, nm = element nm 1L e
            let a2 = litAtom "ok" |> constr
            modCall erlang equals [a1; a2] |> constr, nm
        | B.UnionCaseTest (e, IsFSharpResult t, IsCase "Error" c) ->
            let a1, nm = element nm 1L e
            let a2 = litAtom "error" |> constr
            modCall erlang equals [a1; a2] |> constr, nm
        | B.UnionCaseTest (e, ErlangTerm (t, inclTag), uc) ->
            let pat, guard = mkErlangTermCasePat nm t inclTag uc
            let alt1 = mkAlt pat guard trueExps
            let alt2 = mkAlt (cerl.PVar "_") cerl.defaultGuard falseExps
            let e, ctx = processExpr nm e
            cerl.Case(e, [alt1; alt2]) |> constr, ctx
        | B.UnionCaseTest (e, t, uc) ->
            let pat1 = mkStructuralUnionCasePat t uc
            let alt1 = mkAlt pat1 cerl.defaultGuard trueExps
            let alt2 = mkAlt (cerl.PVar "_") cerl.defaultGuard falseExps
            let e, ctx = processExpr nm e
            cerl.Case(e, [alt1; alt2]) |> constr, ctx
        | B.Call (callee, f, _, argTypes, expressions) ->
            translateCall nm callee f argTypes expressions
        | B.TraitCall (types, name, flags, someTypes, argTypes, args) ->
            let args, nm = foldNames nm processExpr args
            traitCall name args |> constr, nm
            (* modCall fezCore traitCall args |> constr, nm *)
        | B.Value v ->
            let v', nm = safeVar false nm v.LogicalName
            match Map.tryFind v' nm.Functions with
            | Some f ->
                cerl.Fun f |> constr, nm
            | None ->
                let valueExps = cerl.Var v' |> constr
                valueExps, nm
        | B.Const (o, t) ->
            mapConst o t |> constr, nm
        | B.NewTuple (fsType, args) ->
            let args, nm = foldNames nm processExpr args
            let args = List.map (flattenLambda []) args
            cerl.Tuple args |> constr, nm
        | B.TupleGet (fsType, idx, e) ->
            let idx = idx+1
            element nm (int64 idx) e
        | B.NewUnionCase (IsFSharpList t, IsCase "Empty" c, e) ->
            constr (cerl.Lit cerl.LNil), nm
        | B.NewUnionCase (IsFSharpList t, IsCase "Cons" c, e) ->
            let args, nm = foldNames nm processExpr e
            // cons should always generate exactly 2 args
            constr(cerl.List (cerl.LL([args.[0]], args.[1]))), nm
        | B.NewUnionCase (IsFSharpOption t, IsCase "Some" c, [e]) ->
            processExpr nm e
        | B.NewUnionCase (IsFSharpOption t, IsCase "None" c, e) ->
            constr (litAtom "undefined"), nm
        | B.NewUnionCase (IsFSharpResult t, IsCase "Ok" c, [e]) ->
            let a1 = litAtom "ok" |> constr
            let a2, nm  = processExpr nm e
            cerl.Tuple [a1; a2] |> constr, nm
        | B.NewUnionCase (IsFSharpResult t, IsCase "Error" c, [e]) ->
            let a1 = litAtom "error" |> constr
            let a2, nm  = processExpr nm e
            cerl.Tuple [a1; a2] |> constr, nm
        | B.NewUnionCase(ErlangTerm (t, inclTags), uc, []) as e ->
            lowerFirst uc.Name |> litAtom |> constr, nm
        | B.NewUnionCase(ErlangTerm (t, false), uc, [arg]) as e ->
            // a single arg is treated like it's value
            processExpr nm arg
        | B.NewUnionCase(ErlangTerm (t, false), uc, args) as e ->
            // if the DU case has args we treat them as any other tuple
            let args, nm = foldNames nm processExpr args
            let args = List.map (flattenLambda []) args
            cerl.Tuple args |> constr, nm
        | B.NewUnionCase(ErlangTerm (t, true), uc, args) as e ->
            // include tag in tuple
            let tag = lowerFirst uc.Name |> litAtom |> constr
            let args, nm = foldNames nm processExpr args
            let args = List.map (flattenLambda []) args
            cerl.Tuple (tag :: args) |> constr, nm
        | B.NewUnionCase(t, uc, argExprs) as e ->
            let typeTag = mkTypeTag t
            let caseTag = mkUnionTag uc
            let args, nm = foldNames nm processExpr argExprs
            cerl.Tuple (typeTag :: caseTag:: args) |> constr, nm
        | B.Let ((v, B.Call (_, (LogicalName "receive" as m), _t, [t], _)), expr) as r ->
            // generate basic structural case to for the DU type
            // then generate standard if then else
            let alias, nm = uniqueName nm
            let mkAliasP p = cerl.PAlias (cerl.Alias (alias, p))

            let alts =
                if t.IsTupleType then
                    // TODO: support tuples
                    //attempt tuple match with type based match?
                    failwith "Cannot currently receive tuples. Use DUs to define receive contracts."
                elif t.TypeDefinition.IsFSharpUnion then
                    t.TypeDefinition.UnionCases
                    |> Seq.map (fun c ->
                        let pat = mkStructuralUnionCasePat t c |> mkAliasP
                        cerl.Constr (cerl.Alt (cerl.Pat pat, cerl.defaultGuard,
                                        constr (cerl.Var alias))))
                    |> Seq.toList
                else
                    failwithf "unsupported receive type %A" t
            let infinity =
                let expiry = litAtom "infinity" |> constr
                let body = litAtom "true" |> constr
                cerl.TimeOut (expiry, body)
            let receive = cerl.Receive (alts, infinity) |> constr
            let n, nm = safeVar true nm v.LogicalName
            let letExps, nm = processExpr nm expr
            mkLet n receive letExps |> constr, nm
        | B.Let ((v, e), expr) when not v.IsMutable ->
            // check if creating a ref cell and warn about limitations
            let t = nonAbbreviatedType e.Type
            if t.HasTypeDefinition && t.TypeDefinition.TryFullName =
                (Some "Microsoft.FSharp.Core.FSharpRef`1") then
                    eprintf """
WARNING: uses of ref cells cannot be automatically garbage collected as they
are backed by the process dictionary.  To manually release the entry in the
process dictionary call the Ref.release() method.

"""

            // ignore names introduced in the variable assignment expression
            let ass, _ = processExpr nm e
            let ass = flattenLambda [] ass
            let v', nm = safeVar true nm v.LogicalName
            let next, nm = processExpr nm expr
            mkLet v' ass next |> constr, nm
        | B.FSharpFieldSet (Some o, t, f, e) ->
            failwithf "FSharpFieldSet not supported"
            (* let td = e.Type.TypeDefinition *)
            (* let tupleIndex = *)
            (*     t.TypeDefinition.FSharpFields *)
            (*     |> Seq.findIndex ((=) f) *)
            (*     |> (+) 1 *)
            (*     |> int64 *)
            (* let e, nm = processExpr nm e *)
            (* let this = cerl.Var "_this" |> constr *)
            (* tupleSet tupleIndex this e |> constr, nm *)
        | B.IfThenElse (fi, neht, esle) as ite ->
            //plain if then else without decision tree
            let ifExps, nm = processExpr nm fi
            let thenExpr, nm = processExpr nm neht
            let elseExpr, nm = processExpr nm esle
            let a1 = altExpr (boolPat "true", cerl.defaultGuard, thenExpr)
            let a2 = altExpr (boolPat "false", cerl.defaultGuard, elseExpr)
            cerl.Case(ifExps, [a1;a2]) |> constr, nm
        | B.DecisionTree (ite, branches) as tree ->
            let l = List.mapi (fun i x -> i, x) branches |> Map
            let e, nm = processDT nm l ite
            constr e, nm
        | B.FSharpFieldGet (Some e, t, fld) when e.Type.TypeDefinition.IsClass ->
            let td = e.Type.TypeDefinition
            let fld = td.FullName + "." + fld.Name |> litAtom |> constr
            let fieldGet = "field_get" |> litAtom |> constr
            let e, nm = processExpr nm e
            modCall fezCore fieldGet [fld; e] |> constr, nm
        | B.FSharpFieldGet (Some e, t, fld) ->
            let td = e.Type.TypeDefinition
            let offset =
                if e.Type.TypeDefinition.IsClass then 2
                else 1
            // TODO when would the expr be None here
            let tupleIndex =
                t.TypeDefinition.FSharpFields
                |> Seq.findIndex ((=) fld)
                |> (+) offset
                |> int64
            let e, nm = processExpr nm e
            tupleGet tupleIndex e |> constr, nm
        | B.NewRecord (t, args) ->
            let args, nm = foldNames nm processExpr args
            //type to atom
            let recordName = mkTypeTag t
            cerl.Tuple (recordName :: args) |> constr, nm
        | B.UnionCaseGet(value, IsFSharpList fsType, IsCase "Cons" uCas,
                         IsField "Head" fld) ->

             let hd = litAtom "hd" |> constr
             let e, nm = processExpr nm value
             modCall erlang hd [e] |> constr, nm
        | B.UnionCaseGet(value, IsFSharpList fsType, IsCase "Cons" uCas,
                         IsField "Tail" fld) ->
             let hd = litAtom "tl" |> constr
             let e, nm = processExpr nm value
             modCall erlang hd [e] |> constr, nm
        | B.UnionCaseGet (value, IsFSharpOption t, IsCase "Some" c, fld) ->
            processExpr nm value
        | B.UnionCaseGet(e, ErlangTerm (t, inclTag), c, f) ->
            match Seq.toList c.UnionCaseFields with
            | [] -> //atom case
                lowerFirst c.Name |> litAtom |> constr, nm
            | [a] when not inclTag ->
                processExpr nm e
            | args ->
                let idx =
                    c.UnionCaseFields
                    |> Seq.findIndex ((=) f)
                    |> int64
                let element = litAtom "element" |> constr
                let e, nm = processExpr nm e
                let incr = if inclTag then 2L else 1L
                let idx = idx + incr |> cerl.LInt
                modCall erlang element [cerl.Lit idx |> constr; e] |> constr, nm
        | B.UnionCaseGet(e, t, c, f) ->
            // turn these into element/2 calls
            let idx =
                c.UnionCaseFields
                |> Seq.findIndex ((=) f)
                |> int64
            let element = litAtom "element" |> constr
            let e, nm = processExpr nm e
            let idx = idx + 3L |> cerl.LInt
            modCall erlang element [cerl.Lit idx |> constr; e] |> constr, nm
        | B.Coerce(a, e) ->
            processExpr nm e
        | B.NewObject(IsCtor m, types, exprs) ->
            // just delegate a call to the "ctor" function here
            translateCall nm None m types exprs
        // horrendously specific match to intercept printfn and sprintf
        | B.Application (B.Let ((_, B.Call (None, (LogicalName "printfn" | LogicalName "sprintf" as p), _, _,
                                            [B.Coerce (_, B.NewObject (_, _, [B.Const (:? string as str, t)]))])), _letBody),
                         _types, args) ->
            let format = annLAtom "format"
            // primitive format converion
            let str = str.Replace("%s", "~s")
                         .Replace("%i", "~b")
                         .Replace("%A", "~p")
            let io, str =
                match p.LogicalName with
                | "printfn" ->
                    let str = str + "~n" //add newline as io:format does not
                    annLAtom "io", str
                | "sprintf" ->
                    annLAtom "io_lib", str
                | _ -> failwith "unexpected"
            let arg1 = mapConst str t |> constr
            let args, nm = foldNames nm processExpr args
            let args = [arg1; cerl.List (cerl.L args) |> constr]
            modCall io format args |> constr, nm
        | B.Application (target, _ ::_, []) ->
            // attempt to match value created by TypeLambda
            // it has "types" but no args
            processExpr nm target
        | B.Application (target, _types, args) ->
            let res = target.Type.IsUnresolved
            let cp = match target with
                     | B.Value f ->
                        let c =
                            f.CurriedParameterGroups
                            |> Seq.length
                        c - (List.length args)
                     | _ -> 0

            let missingArgs, nm =
                foldNames nm (fun nm _ -> uniqueName nm) [1..cp]

            let wrap e =
                if cp > 0 then
                    // wrap in Noop to avoid being flattened later
                    cerl.Noop (lambda missingArgs e |> constr) |> constr
                else e
            let missingArgs =
                missingArgs
                |> List.map (fun a -> cerl.Exp (cerl.Constr (cerl.Var a)))
                |> stripFezUnit
            // if the target is not a plain value or a function we
            // may not be able to process it inline and thus need to wrap it
            // in a Let
            //Stash the context
            match processExpr nm target with
            | cerl.Exp (cerl.Constr (cerl.Var _ | cerl.Fun _ )) as t, nm ->
                // we're cool the target is just a var or fun - we can inline
                let args, nm = foldNames nm processExpr args
                let args = (args @ missingArgs) |> stripFezUnit
                wrap <| (apply t args |> constr), nm
            | t, _ ->
                let t = flattenLambda [] t
                //the target is something more complex and needs to be
                //wrapped in a Let
                let name, nm = uniqueName nm
                // ignore context update from inner expression
                let app, _ =
                    let args, nm = foldNames nm processExpr (eraseUnit args)
                    apply (varExps name) args |> constr, nm
                mkLet name t app |> constr |> wrap, nm
        | B.Sequential(first, second) ->
            let f, nm = processExpr nm first
            let s, nm = processExpr nm second
            cerl.Seq (f, s) |> constr, nm
        | B.Lambda (IsUnitArg p, expr) ->
            let unitName, nm = safeVar true nm p.LogicalName
            let body, nm = processExpr nm expr
            let nm = {nm with UnitNames = nm.UnitNames.Add p.LogicalName}
            // wrap body in let so that unit arg is mapped to unit
            let body = mkLet unitName fezUnit body |> constr
            cerl.Lambda ([], body) |> constr, nm
        | B.Lambda (p, expr) ->
            let v, nm = safeVar true nm p.LogicalName
            let body, nm = processExpr nm expr
            let l = cerl.Lambda ([v], body) |> constr
            l, nm
        | B.LetRec(funs, e) ->
            let funDef nm (m : FSharpMemberOrFunctionOrValue, e : FSharpExpr) =
                //TODO do we need to use a safe name?
                let name, nm = safeVar true nm m.LogicalName
                //let recs appear to be unflattened
                //to find numargs we need to process the expr
                //then flatten then take the number of lambda args
                //we have to do it out of order to ensure the function name
                //is processed before the body so processing again after
                let numArgs, l =
                    let e, nm = processExpr nm e
                    let e = e |> flattenLambda []
                    match e with
                    | cerl.Exp (cerl.Constr (cerl.Lambda (args, exps)) as l) ->
                        List.length args, l
                    | args -> failwithf "unexpected letrec args %A" args
                let f, nm = mkFunction nm name numArgs
                let e, nm = processExpr nm e
                let e = e |> flattenLambda []
                funDef f (unconstr e), nm

            let defs, {Functions = fs} = foldNames nm funDef funs
            let nm = {nm with Functions = Map.merge (nm.Functions) fs}
            let e, nm = processExpr nm e
            cerl.LetRec (defs, e) |> constr, nm
        | B.AddressOf e ->
            processExpr nm e
        | B.TryWith(tryExpr, f1, e2, f2, caughtExpr) ->
            let tryExps, nm = processExpr nm tryExpr
            let catchExps = cerl.Catch tryExps |> constr
            let caughtName, nm = safeVar true nm f2.LogicalName
            let p = put (litAtom "last_exception" |> constr) (constr (cerl.Var caughtName))
            let caughtExps, nm = processExpr nm caughtExpr
            let e = cerl.Seq (constr p, caughtExps) |> constr
            mkLet caughtName catchExps e |> constr, nm
            (* failwithf "e1 %A f1 %A e2 %A f2 %A e3 %A" tryExpr f1 e2 f2 caughtExpr *)
        | B.TryFinally(attempt, after) ->
            let att, nm = processExpr nm attempt
            let after, nm = processExpr nm after
            mkTryAfter nm att after
        | B.TypeTest (t, valExpr) when t.TypeDefinition.IsInterface ->
            (*
              FSharp type tuples (records and unions) don't carry information
              about which interfaces they implement only {Module, TypeName}.
              If we just erase typetest we can later attempt to dispatch to the
              the 'Type.IntefaceMethod' using the `trait_call` mechanism.
              *)
            trueExps, nm
        | B.TypeTest (t, valExpr) ->
            //  attempt tuple type test on any other type
            let tag = mkTypeTag t
            let ele, nm = element nm 1L valExpr
            modCall erlang equals [tag; ele] |> constr, nm
        | B.TypeLambda(_, e) ->
            let e, nm = processExpr nm e
            e, nm
            // why did we need this to be a Noop?
            (* cerl.Noop (lambda [] e |> constr) |> constr, nm *)
        | B.FastIntegerForLoop(f, t, B.Lambda(p, expr), isUp) ->
            let v, nm = safeVar true nm p.LogicalName
            let body, nm = processExpr nm expr
            let l = cerl.Lambda ([v], body) |> constr
            let fe, nm = processExpr nm f
            let te, nm = processExpr nm t
            fastIntegerLoop fe te l |> constr, nm
        | B.Quote (e) ->
            processExpr nm e
        | B.ThisValue e ->
            let v, nm = safeVar false nm "this"
            cerl.Var v |> constr, nm


        (* | B.WhileLoop(trueExpr, expr) -> *)

        | x -> failwithf "not implemented %A" x

    type FDef =
        { Module : string
          Function : string
          Arity : int
          IsPublic : bool
          FunDef : cerl.FunDef}

    type ModDecl =
        | Fun of (cerl.Function option * cerl.FunDef)
        | Mod of (string * cerl.Module) list
        | Skip


    let rec hasTupledArg =
        function
        | (Tupled _ :: _) -> true
        | [] -> false
        | Singled _ :: rest ->
            hasTupledArg rest


    let rec doFunDecl decl : FDef list =
        let functionName (memb: FSharpMemberOrFunctionOrValue)=
            let ee = memb.LogicalEnclosingEntity
            let logicalName = memb.LogicalName
            if memb.IsExtensionMember then
                sprintf "%s.%s" ee.LogicalName logicalName
            else
                logicalName
            |> safe

        match decl with
        | MemberOrFunctionOrValue (HasModCallAttribute (args, memb),
                                   Parameters ps, _expr) ->
            let ee = memb.EnclosingEntity.Value
            let ctx = Ctx.init ee.FullName
            let functionName = functionName memb
            let e1 = litAtom ((snd args.[0]) :?> string) |> constr
            let e2 = litAtom ((snd args.[1]) :?> string) |> constr
            // the names of the arguments should not matter so just
            // generate some unique names
            let args, ctx = foldNames ctx (fun ctx _ -> uniqueName ctx) ps
            let e = modCall e1 e2 ((args |> List.map (cerl.Var >> constr))) |> constr
            let f, ctx = mkFunction ctx functionName (List.length ps)
            let l = lambda args e
            let fdef = funDef f l
            [{Module = ee.FullName //TODO clean
              Function = functionName
              Arity = args.Length
              IsPublic = memb.Accessibility.IsPublic
              FunDef = fdef }]
        | MemberOrFunctionOrValue (IsCtor memb, FlatParameters ps,
                                   B.Sequential (first,  body)) ->
            // first it calls the base constructor
            let ee = memb.EnclosingEntity.Value
            let ctx = Ctx.init ee.FullName

            let fieldSet fld v o =
                let fezCore = litAtom "Fez.Core" |> constr
                let f = constr (litAtom "field_set")
                modCall fezCore f [fld; v; o]

            let inheritF t o =
                let fezCore = litAtom "Fez.Core" |> constr
                let f = constr (litAtom "inherit")
                modCall fezCore f [t; o]

            let mkClassInstance baseType (m : FSharpMemberOrFunctionOrValue) =
                let ret = memb.FullType.GenericArguments |> Seq.toList |> List.last
                let t = litAtom ret.TypeDefinition.FullName |> constr
                inheritF t baseType

            let this =
                let o, ctx = processExpr ctx first
                mkClassInstance o memb |> constr

            let thisExps = cerl.Var "_this0" |> constr

            let rec doCtor x = [
                match x with
                | B.Sequential(first, second) ->
                    yield! doCtor first
                    yield! doCtor second
                | B.FSharpFieldSet (Some o, t, f, e)  ->
                    let td = o.Type.TypeDefinition
                    let field = td.FullName + "." + f.Name |> litAtom |> constr
                    let v, nm = processExpr ctx e
                    yield fieldSet field v thisExps |> constr
                | B.Let (_, e) ->
                    // TODO handle the settler there
                    yield! doCtor e
                | x -> eprintfn "Constructor: not done %A" x
            ]

            let body =
                this :: doCtor body
                |> List.rev
                |> List.fold (fun s e -> mkLet "_this0" e s |> constr) thisExps

            let functionName = functionName memb
            let f, ctx = mkFunction ctx functionName (List.length ps)

            let args, nm = foldNames ctx (safeVar true) ps
            let lambda = lambda args body
            let fdef = funDef f lambda
            [{ Module = safe ee.FullName
               Function = functionName
               Arity = args.Length
               IsPublic = memb.Accessibility.IsPublic
               FunDef = fdef }]

        | MemberOrFunctionOrValue (Gumpf, _, _) as m ->
            []
        | MemberOrFunctionOrValue (memb, FlatParameters ps, expr)
            when memb.IsModuleValueOrMember  ->
            let ee = memb.EnclosingEntity.Value
            let ctx = Ctx.init ee.FullName
            let functionName = functionName memb
            let ps =
                match ps with
                | [o; x] when memb.IsMember && memb.IsInstanceMember
                                && x.StartsWith("unit") ->
                    // TODO: there must be a better way to detect if the
                    // second arg is unit
                    //remove unit on member call
                    [o]
                | _ -> ps
            // how to handle tupled arguments
            // do we need to create nested case of statements to deconstruct
            // them?
            let args, nm = foldNames ctx (safeVar true) ps
            // need function context for recursion
            //TODO top level functions are unique so no need to prefix
            let f, ctx = mkFunction ctx functionName (List.length ps)
            //process expression after function has been added to context
            //such that it can be found for recursive calls
            let e, ctx = processExpr ctx expr
            let lambda = lambda args e
            let fdef = funDef f lambda
            [{Module = safe ee.FullName
              Function = functionName
              Arity = args.Length
              IsPublic = memb.Accessibility.IsPublic
              FunDef = fdef }]
        | Entity (ent, declList) when ent.IsFSharpRecord ->
            []
        | Entity (ent, declList) when ent.IsFSharpUnion ->
            []
        | Entity (ent, declList) as e when ent.IsFSharpModule ->
            doDecl e
        | Entity (ent, declList)  ->
            []
        | MemberOrFunctionOrValue(x, _, _) ->
            eprintfn "skipping member or function %+A %A"
                x.CompiledName
                (x.LogicalEnclosingEntity,
                 x.IsConstructorThisValue,
                 x.IsExtensionMember,
                 x.IsInstanceMemberInCompiledCode,
                 x.IsBaseValue,
                 x.IsValCompiledAsMethod,
                 x.IsInstanceMember,
                 x.IsDispatchSlot,
                 x.IsConstructor)
            []
        | x -> failwithf "cannot process %A" x 

    and doDecl decl =
      match decl with
      | Entity (ent, fdecls) when ent.IsFSharpModule ->
          fdecls |> List.collect doFunDecl
      | InitAction expr ->
          failwithf "Module values (InitActions) are not supported as there is no equivalent in erlang.\r\nMake it a function instead.\r\n%A" expr
      | Entity (ent, declList) ->
          failwithf "cannot process record %+A" ent.TryGetMembersFunctionsAndValues
      | x -> failwithf "cannot process %+A" x

    let moduleInfos name =
          [cerl.Function (cerl.Atom "module_info", 0)
           cerl.Function (cerl.Atom "module_info", 1)]

    let toModule moduleName (fDefs : FDef list) =
        let exported =
            fDefs
            |> List.filter (fun fd -> fd.IsPublic)
            |> List.map (fun fd -> cerl.Function (cerl.Atom fd.Function, fd.Arity))
            |> List.append (moduleInfos moduleName)
        let functions =
            fDefs
            |> List.map (fun fd -> fd.FunDef)
            |> List.append [cerl.moduleInfo0 moduleName; cerl.moduleInfo1 moduleName]
        cerl.Module (cerl.Atom moduleName, exported, [], functions)
