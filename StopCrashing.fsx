// StopCrashing.fsx by Frank A. Krueger @praeclarum

#r "System.Xml"
#r "System"
#I "./"
#r "Mono.Cecil.dll"

open System
open System.Xml
open System.Text.RegularExpressions
open System.IO
open Mono.Cecil
open Mono.Cecil.Cil

let readerParams binPath =
    let res = DefaultAssemblyResolver ()
    res.AddSearchDirectory "/Library/Frameworks/Xamarin.Mac.framework/Versions/Current/lib/mono"
    res.AddSearchDirectory "/Library/Frameworks/Xamarin.iOS.framework/Versions/Current/lib/mono/Xamarin.iOS"
    res.AddSearchDirectory "/Library/Frameworks/Xamarin.iOS.framework/Versions/Current/lib/mono/2.1"
    res.AddSearchDirectory (Path.GetDirectoryName (binPath))
    ReaderParameters (AssemblyResolver = res)

let isUIType (t : TypeReference) =
    if t = null then false
    else
        let n = t.FullName
        n.StartsWith ("MonoMac.AppKit.")
        || n.StartsWith ("AppKit.")
        || n.StartsWith ("MonoTouch.UIKit.")
        || n.StartsWith ("UIKit.")
        || n.StartsWith ("MonoMac.SceneKit.")
        || n.StartsWith ("MonoTouch.SceneKit.")
        || n.StartsWith ("SceneKit.")

let isUITypeDescendent (t : TypeReference) =
    if t = null then false
    else (isUIType t) || (isUIType (t.Resolve ()).BaseType)

type Diagnosis = (MethodDefinition * MethodDefinition option) list

let rec diagnoseBinary (binPath : string) : Diagnosis =
    let m = ModuleDefinition.ReadModule (binPath, readerParams binPath)
    let rec getTypes (t : TypeDefinition) =
        t :: (t.NestedTypes |> Seq.collect getTypes |> List.ofSeq)
    let types = m.Types |> Seq.collect getTypes |> List.ofSeq
    let badUIMethods =
        types
        |> Seq.filter isUITypeDescendent
        |> Seq.collect diagnoseUIType
    let badUIDelegateMethods =
        types
        |> Seq.collect findUIDelegateMethods
        |> Seq.filter isBadMethod
    Seq.append badUIMethods badUIDelegateMethods |> List.ofSeq

and diagnoseUIType t =
    t.Methods
    |> Seq.filter isUIMethodOverride
    |> Seq.map (fun m -> m, None)
    |> Seq.filter isBadMethod

and isBadMethod (m : MethodDefinition, c) : bool =
    (not m.IsConstructor) && (callsOtherMethods m.Body) && (not m.Body.HasExceptionHandlers)

and isCall (i : Instruction) =
    match i.OpCode.Code with
    | Code.Call | Code.Calli | Code.Callvirt -> true
    | _ -> false

and callsOtherMethods body =
    body.Instructions |> Seq.exists isCall

and isUIMethodOverride (m : MethodDefinition) =
    (isExportMethod m) || (m.IsVirtual && isUIMethod m)

and isUIMethod (m : MethodDefinition) =
    if m = null then false
    else
        let dt = m.DeclaringType
        if isUIType dt then true
        else if dt.BaseType = null then false
        else
            let mutable isV = false
            let mutable bt = dt.BaseType.Resolve ()
            while not isV && bt <> null do
                isV <- bt.Methods |> Seq.exists (fun x -> x.Name = m.Name && isUIMethod x)
                bt <- if bt.BaseType <> null then bt.BaseType.Resolve () else null
            isV

and isExportAttr (m : CustomAttribute) =
    m.Constructor.DeclaringType.Name = "ExportAttribute"

and isExportMethod (m : MethodDefinition) =
    match m.CustomAttributes |> Seq.tryFind isExportAttr with
    | Some _ -> true
    | _ -> false

and findUIDelegateMethods (t : TypeDefinition) =
    let bodies =
        t.Methods
        |> Seq.filter (fun m -> m.HasBody)
        |> Seq.map (fun m -> m.Body)
    bodies
    |> Seq.collect findDels
            
and findDels (b : MethodBody) =
    let getParamDels (i : Instruction) (m : MethodDefinition) (pi : int, p : ParameterDefinition) =
        if pi <> m.Parameters.Count - 1 then
//            printfn ":-( Cannot find the argument for `%s` in call to %s.%s" p.Name m.DeclaringType.FullName m.Name
            []
        else
            match i.Previous.Previous with
            | ld when ld.OpCode.Code = Code.Ldftn ->
                match ld.Operand with
                | :? MethodReference as mr ->
                    let md = mr.Resolve ()
                    [md, Some m]
                | _ -> []
            | _ -> []
    let rec isDelegateType (t : TypeDefinition) =
        if t = null then false
        else if t.FullName = "System.MulticastDelegate" then true
        else
            let br = t.BaseType
            if br = null then false
            else isDelegateType (br.Resolve ())
    let getDelParam (i : int) (p : ParameterDefinition) =
        if isDelegateType (p.ParameterType.Resolve ()) then Some (i, p)
        else None
    let findDels' (i : Instruction) =
        match i.Operand with
        | :? MethodReference as mr
          when isUIMethod (mr.Resolve ()) && not (mr.Name.StartsWith ("remove_")) ->
            let md = mr.Resolve ()
            let delParams =
                md.Parameters
                |> Seq.mapi getDelParam
                |> Seq.choose id
                |> List.ofSeq
            delParams |> List.collect (getParamDels i md)
        | _ -> []
    b.Instructions |> Seq.filter isCall |> Seq.collect findDels'


//
// Find binaries for a solution
//
let fixDir (d : string) = d.Replace ('\\', Path.DirectorySeparatorChar)

let diagnoseProj (projPath : string) : Diagnosis =
    let binPath =
        let proj = XmlDocument ()
        proj.Load projPath
        let projDir = Path.GetDirectoryName projPath
        let outType = (proj.GetElementsByTagName ("OutputType")).[0].InnerText.Trim ()
        let ext = if outType = "Library" then ".dll" else ".exe"
        let name = (proj.GetElementsByTagName ("AssemblyName")).[0].InnerText.Trim () + ext
        let dirs =
            (proj.GetElementsByTagName ("OutputPath"))
            |> Seq.cast<XmlNode>
            |> Seq.map (fun x -> x.InnerText.Trim ())
        let paths =
            dirs
            |> Seq.map (fun x -> Path.Combine (projDir, fixDir x, name))
        let fileInfos =
            paths
            |> Seq.map (fun x -> FileInfo (x))
            |> Seq.sortBy (fun x -> x.LastWriteTime)
            |> Array.ofSeq
            |> Array.rev
        (Seq.head fileInfos).FullName

    diagnoseBinary binPath


let diagnoseSln slnPath : Diagnosis =

    let dump name arg = printfn "%s = %s" name (sprintf "%A" arg); arg

    let projLineRe = Regex ("""Project\(.*?\)\s*=.*?,\s*\"(.*?)\",""")

    let projLine line =
        match projLineRe.Match line with
        | m when m.Success -> Some m.Groups.[1].Value
        | _ -> None

    let projectPaths =
        let d = Path.GetDirectoryName (slnPath)
        File.ReadAllLines slnPath
        |> Seq.choose projLine
        |> Seq.map (fun x -> Path.Combine (d, fixDir x))
        |> Seq.filter (fun x -> (Path.GetExtension (x)).Length > 2) // Avoid directories (and ., ..)

    projectPaths |> Seq.collect diagnoseProj |> List.ofSeq

let diagnoseFile path =
    let badMethods =
        match (Path.GetExtension (path)).ToLowerInvariant () with
        | ".sln" -> diagnoseSln path
        | ".csproj" -> diagnoseProj path
        | ".fsproj" -> diagnoseProj path
        | ".dll" | ".exe" -> diagnoseBinary path
        | x -> failwithf "StopCrashing doesn't know what to do with %s files" x

    if badMethods.Length = 0 then
        Console.ForegroundColor <- ConsoleColor.Green
        printfn "GOOD JOB WITH %s!" path
    else
        printfn "%d UI METHODS IN %s NEED EXCEPTION HANDLERS" badMethods.Length path
        Console.ForegroundColor <- ConsoleColor.Red
        for t, c in badMethods do
            match c with
            | Some context -> printfn "    %s.%s (%s.%s)" t.DeclaringType.FullName t.Name context.DeclaringType.FullName context.Name
            | _ -> printfn "    %s.%s" t.DeclaringType.FullName t.Name

    Console.ResetColor ()
    badMethods


//
// Entrypoint
//

#if INTERACTIVE
let argv = fsi.CommandLineArgs |> Seq.skip 1 |> List.ofSeq
#else
let argv = ["Test.sln"]
#endif

if argv.Length < 1 then
    printfn "StopCrashing.fsx [Solution.sln] [Library.dll] [App.exe]"
    1
else
    let badMethods = argv |> List.collect diagnoseFile
    if badMethods.Length > 0 then 2 else 0


