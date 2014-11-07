// StopCrashing.fsx by Frank A. Krueger @praeclarum

#r "System.Xml"
#r "Mono.Cecil.dll"

open System
open System.Xml
open System.Text.RegularExpressions
open System.IO
open Mono.Cecil
open Mono.Cecil.Cil

let readerParams =
    let res = DefaultAssemblyResolver ()
    res.AddSearchDirectory "/Library/Frameworks/Xamarin.Mac.framework/Versions/Current/lib/mono"
    res.AddSearchDirectory "/Library/Frameworks/Xamarin.iOS.framework/Versions/Current/lib/mono/2.1"
    ReaderParameters (AssemblyResolver = res)

let rec diagnoseBinary (binPath : string) =
    let m = ModuleDefinition.ReadModule (binPath, readerParams)
    m.Types |> Seq.filter isUITypeDesc |> Seq.collect diagnoseType |> List.ofSeq

and diagnoseType t =
    t.Methods |> Seq.filter isUIMethod |> Seq.filter isBadMethod

and isBadMethod (m : MethodDefinition) : bool =
    (not m.IsConstructor) && (callsOtherMethods m.Body) && (not m.Body.HasExceptionHandlers)

and callsOtherMethods body =
    body.Instructions |> Seq.exists (fun i ->
        match i.OpCode.Code with
        | Code.Call | Code.Calli | Code.Callvirt -> true
        | _ -> false)

and isUIMethod (m : MethodDefinition) =
    (isExportMethod m) || (isVirtualUIMethod m)

and isVirtualUIMethod (m : MethodDefinition) =
    if not m.IsVirtual then false
    else
        let dt = m.DeclaringType
        if isUIType dt then true
        else
            let bt = dt.BaseType.Resolve ()
            match bt.Methods |> Seq.tryFind (fun x -> x.Name = m.Name) with
            | Some bm -> isVirtualUIMethod bm
            | None -> false

and isExportAttr (m : CustomAttribute) =
    m.Constructor.DeclaringType.Name = "ExportAttribute"

and isExportMethod (m : MethodDefinition) =
    match m.CustomAttributes |> Seq.tryFind isExportAttr with
    | Some _ -> true
    | _ -> false

and isUIType (t : TypeReference) =
    if t = null then false
    else
        let n = t.FullName
        n.StartsWith ("MonoMac.AppKit.")
        || n.StartsWith ("MonoTouch.UIKit.")
        || n.StartsWith ("AppKit.")
        || n.StartsWith ("UIKit.")

and isUITypeDesc (t : TypeReference) =
    if t = null then false
    else (isUIType t) || (isUIType (t.Resolve ()).BaseType)


//
// Find binaries for a solution
//

let diagnoseSln slnPath =

    let dump name arg = printfn "%s = %s" name (sprintf "%A" arg); arg

    let projLineRe = Regex ("""Project\(.*?\)\s*=.*?,\s*\"(.*?)\",""")

    let projLine line =
        match projLineRe.Match line with
        | m when m.Success -> Some m.Groups.[1].Value
        | _ -> None

    let fixDir (d : string) = d.Replace ('\\', Path.DirectorySeparatorChar)

    let projectPaths =
        let d = Path.GetDirectoryName (slnPath)
        File.ReadAllLines slnPath
        |> Seq.choose projLine
        |> Seq.map (fun x -> Path.Combine (d, fixDir x))

    let getProjectBin (projPath : string) : string =
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

    let binPaths =
        projectPaths
        |> Seq.fold (fun bs path -> Set.add (getProjectBin path) bs) Set.empty

    binPaths
    |> Seq.collect diagnoseBinary
    |> Seq.sortBy (fun x -> x.DeclaringType.FullName + x.Name) |> List.ofSeq


let diagnoseFile path =
    let badMethods =
        match (Path.GetExtension (path)).ToLowerInvariant () with
        | ".sln" -> diagnoseSln path
        | ".dll" | ".exe" -> diagnoseBinary path
        | x -> failwithf "StopCrashing doesn't know what to do with %s files" x

    let fc = Console.ForegroundColor
    if badMethods.Length = 0 then
        Console.ForegroundColor <- ConsoleColor.Green
        printfn "GOOD JOB WITH %s!" path
    else
        printfn "%d UI METHODS IN %s NEED EXCEPTION HANDLERS" badMethods.Length path
        Console.ForegroundColor <- ConsoleColor.Red
        for t in badMethods do
            printfn "    %s.%s" t.DeclaringType.FullName t.Name
    Console.ForegroundColor <- fc

    badMethods


//
// Entrypoint
//

let argv = fsi.CommandLineArgs |> Seq.skip 1 |> List.ofSeq

if argv.Length < 1 then
    printfn "StopCrashing.exe [Solution.sln] [Library.dll] [App.exe]"
    1
else
    let badMethods = argv |> List.collect diagnoseFile
    if badMethods.Length > 0 then 2 else 0


