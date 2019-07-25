﻿#nowarn "211"
#I @"../../../../packages/build"
#I @"packages"
#r @"FAKE/tools/FakeLib.dll"
#r @"System.IO.Compression.dll"
#r @"System.IO.Compression.FileSystem.dll"

namespace Aardvark.Fake

open System.IO
open System
open System.Diagnostics
open Fake
open Fake.Core
open Fake.IO
open Fake.IO.Globbing.Operators
open Fake.DotNet
open System.Text.RegularExpressions
open System.IO.Compression
open System.Security.Cryptography
open System.Text

module Array =
    let skip (n : int) (a : 'a[]) =
        let res = Array.zeroCreate (a.Length - n)
        for i in n..a.Length-1 do
            res.[i - n] <- a.[i]
        res
 
[<AutoOpen>]
module PathHelpers =

    let tryDeleteDir d =
        if Directory.Exists d then
            try 
                Directory.Delete(d, true)
                true
            with e -> 
                Trace.traceErrorfn "could not delete directory %A" d
                false
        else
            true

    let deleteDir d =
        if Directory.Exists d then
            try Directory.Delete(d, true)
            with e -> Trace.traceErrorfn "could not delete directory %A" d

    let createDir d =
        if not <| Directory.Exists d then
            Directory.CreateDirectory d |> ignore

    let deleteFile f =
        if File.Exists f then
            try File.Delete f
            with e -> Trace.traceErrorfn "could not delete %A" f

[<AutoOpen>]
module IncrediblyUglyHackfulNugetOverride =

    open System.Text.RegularExpressions
    open System.IO.Compression
    open System.Collections.Generic


    let userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
    let hackedPackagesFile = Path.Combine(userProfile,".nuget","hackedFiles.txt")
    
    let isHackActive () =
        if File.Exists hackedPackagesFile then
            let hacks = File.ReadAllLines hackedPackagesFile
            let nonEmptyHack = hacks |> Array.filter (fun hack -> hack.Length > 0)
            if Array.isEmpty nonEmptyHack then false
            else
                Trace.logfn "here are hacked packages: %A" nonEmptyHack
                true
        else false

    let removeAllHacked () =
        let hacked = File.ReadAllLines hackedPackagesFile |> HashSet
        for h in hacked |> Seq.toArray do
            try 
                Trace.logfn "removing hack: %A" h
                Directory.delete h 
                hacked.Remove h |> ignore
            with e -> Trace.logfn "could not remove hacked file %A" e
        if hacked.Count = 0 then 
            Trace.logfn "no hacks remaining. removing hacked file."
            File.Delete hackedPackagesFile
        else File.WriteAllLines(hackedPackagesFile, hacked |> Seq.toArray)

    let copyToGlobal (getVersion : unit -> string) (removeHacks : bool) =
        let packages = !!"bin/*.nupkg"
        let packageNameRx = Regex @"^(?<name>[a-zA-Z_0-9\.-]+?)\.(?<version>([0-9]+\.)*[0-9]+)(.*?)\.nupkg$"

        let myPackages = 
            packages 
                |> Seq.choose (fun p ->
                    let m = packageNameRx.Match (Path.GetFileName p)
                    if m.Success then 
                        Some(m.Groups.["name"].Value)
                    else
                        None
                )
                |> Set.ofSeq

        let tag = getVersion()

        if not (File.Exists hackedPackagesFile) then File.WriteAllLines(hackedPackagesFile,[||])

        let addToHacked (path : string) =
            let hacked = File.ReadAllLines hackedPackagesFile |> HashSet
            if hacked.Add path then
                // fresh hack
                File.AppendAllLines(hackedPackagesFile,[|path|])

        let removeHacked (path : string) =
            let hacked = File.ReadAllLines hackedPackagesFile |> HashSet
            if hacked.Remove path then
                File.Delete hackedPackagesFile
                if hacked.Count = 0 then 
                    Trace.logfn "no hacks remaining. removing hacked file."
                    File.Delete hackedPackagesFile
                else File.WriteAllLines(hackedPackagesFile, hacked |> Seq.toArray)

        let rec reallyDelete path =
            if Directory.Exists path then
                for d in Directory.GetDirectories path do reallyDelete d
                System.Threading.Thread.Sleep 10
                try Directory.Delete(path,true) with | :? IOException as e -> Trace.logfn "could not delete. retry"; System.Threading.Thread.Sleep 100; reallyDelete path


        for id in myPackages do
            let names = [ sprintf "bin/%s.%s.nupkg" id tag ]
            for packageName in names do
                let target = Path.Combine(userProfile,".nuget","packages",id,string tag)
                if removeHacks then    
                    if Directory.Exists target then 
                        printfn "deleting: %s" target
                        reallyDelete target
                        removeHacked target
                else
                    Trace.logfn "unzip: %s -> %s" packageName target
                    if not (Directory.Exists target) || Seq.isEmpty (Directory.EnumerateFileSystemEntries(target)) then
                        Trace.traceError "cannot hack non existing project"
                    else
                        use archive = System.IO.Compression.ZipFile.OpenRead(packageName)
                        let mutable hacked = false
                        for entry in archive.Entries do
                            if entry.Name.EndsWith("dll") then
                                let entryPath = Path.Combine(target,entry.FullName)
                                Trace.logfn "extract %s to %s" entry.FullName entryPath 
                                entry.ExtractToFile(entryPath,true)
                                // record that we destroyed integrity of this package
                                hacked <- true
                        if hacked then  
                            addToHacked target

        let hackedFiles = File.ReadAllLines hackedPackagesFile
        Trace.logfn "hacked files: \n%A" hackedFiles

module AdditionalSources =

    do Environment.CurrentDirectory <- System.IO.Path.Combine(__SOURCE_DIRECTORY__,@"../../../../")
    
    let shellExecutePaket sln args =
// possible way to active detailed output...
//        let args = 
//            if verbose then
//                String.concat " " [| args; "--verbose" |]
//            else
//                args
        let paketPath = @".paket/paket.exe"

        if File.Exists paketPath |> not then printf ".packet\paket.exe is not available!"

        let tool, args = 
            match System.Environment.OSVersion.Platform with
                | PlatformID.Unix | PlatformID.MacOSX -> "mono", paketPath + " " + args
                | _ -> paketPath, args

        let startInfo = new ProcessStartInfo()
        startInfo.FileName <- tool
        startInfo.Arguments <- args
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true

        let proc = Process.Start(startInfo)

        proc.ErrorDataReceived.AddHandler(DataReceivedEventHandler (fun sender args -> 
            if args.Data <> null then
                let defaultColor = Console.ForegroundColor
                Console.ForegroundColor<-ConsoleColor.Red
                printfn "%s" args.Data
                Console.ForegroundColor<-defaultColor))
        proc.OutputDataReceived.AddHandler(DataReceivedEventHandler (fun sender args -> if args.Data <> null then printfn "%s" args.Data))

        proc.BeginErrorReadLine()
        proc.BeginOutputReadLine()
        
        proc.WaitForExit()

        match sln with
            | Some sln ->
                sln |> DotNet.restore id
            | None ->
                ()

    // register the logger
//    Logging.event.Publish.Subscribe (fun a -> 
//        match a.Level with
//            | TraceLevel.Error -> traceError a.Text
//            | TraceLevel.Info ->  trace a.Text
//            | TraceLevel.Warning -> traceImportant a.Text
//            | TraceLevel.Verbose -> traceVerbose a.Text
//            | _ -> ()
//    ) |> ignore

    let private packageNameRx = Regex @"(?<name>[a-zA-Z_0-9\.]+?)\.(?<version>([0-9]+\.)*[0-9]+)\.nupkg"
    let private templateIdRx = Regex @"^id[ \t]+(?<id>.*)$"
    let private templateVersionRx = Regex @"^version[ \t]+(?<version>([0-9]+\.)*[0-9]+)$"
    let private versionRx = Regex @"(?<version>([0-9]+\.)*[0-9]+).*"
    let private sourcesFileName = "sources.lock"

    // compute a base64 encoded hash for the given string
    let getHash (str : string) =
        let bytes = MD5.Create().ComputeHash(UnicodeEncoding.Unicode.GetBytes(str))
        Convert.ToBase64String(bytes).Replace('/', '_').Replace('\\', '_')

    // a hash based on the current path
    let private cacheFile = Path.Combine(Path.GetTempPath(), getHash Environment.CurrentDirectory)

    // since autorestore might overwrite our source-dependencies we simply turn it off
//    let paketDependencies = Paket.Dependencies.Locate(Environment.CurrentDirectory)
//    do paketDependencies.TurnOffAutoRestore()
    
    // read a package id and version from a paket.template file
    let tryReadPackageIdAndVersion (file : string) =
        let lines = file |> File.ReadAllLines
        
        let id = 
            lines |> Array.tryPick (fun l ->
                let m = templateIdRx.Match l
                if m.Success then Some m.Groups.["id"].Value
                else None
            )

        match id with
            | Some id ->
                let version =
                    lines |> Array.tryPick (fun l ->
                        let m = templateVersionRx.Match l
                        if m.Success then Some m.Groups.["version"].Value
                        else None
                    )
                Some(id, version)
            | None -> None

    // find all created packages
    let findCreatedPackages (folder : string) =
        let files = !!Path.Combine(folder, "**", "*paket.template") |> Seq.toList
        let ids = files |> List.choose tryReadPackageIdAndVersion
        let tag = 
            try Fake.Tools.Git.Information.describe folder
            with _ -> ""

        let m = versionRx.Match tag
        if m.Success then
            let version = m.Groups.["version"].Value
            ids |> List.map (fun (id,_) -> (id,version))
        else
            ids |> List.map (fun (id,v) -> 
                    match v with
                        | Some v -> (id,v)
                        | None -> (id,"")
                   )

    // unzip a specific package to the local packages folder
    let installPackage (pkgFile : string) =
        let m = pkgFile |> Path.GetFileName |> packageNameRx.Match
        if m.Success then
            let id = m.Groups.["name"].Value
            let outputFolder = Path.Combine("packages", id) |> Path.GetFullPath
            
            createDir outputFolder

            // http://community.sharpdevelop.net/forums/p/1954/36951.aspx
            //ICSharpCode.SharpZipLib.Zip.ZipConstants.DefaultCodePage <- System.Text.Encoding.Default.CodePage;
            //System.Threading.Thread.CurrentThread.CurrentCulture.TextInfo.OEMCodePage
            //Unzip outputFolder pkgFile

            System.IO.Compression.ZipFile.ExtractToDirectory(pkgFile, outputFolder);
            File.Copy(pkgFile, Path.Combine(outputFolder, Path.GetFileName pkgFile), true)
            true
        else
            false

    // get the latest modification date for an entire folder
    let latestModificationDate (folder : string) =
        let file = DirectoryInfo(folder).GetFileSystemInfos("*", SearchOption.AllDirectories) |> Seq.maxBy (fun fi -> fi.LastWriteTime)
        file.LastWriteTime

    // install all packages from source-dependencies
    let installSources () =
        let sourceLines =
            if File.Exists sourcesFileName then 
                File.ReadAllLines sourcesFileName |> Array.toList
            else 
                []

        let cacheTimes = 
            if File.Exists cacheFile then 
                cacheFile |> File.ReadAllLines |> Array.choose (fun str -> match str.Split [|';'|] with [|a;b|] -> Some (a,DateTime(b |> int64)) | _ -> None) |> Map.ofArray |> ref
            else
                Map.empty |> ref

        let buildSourceFolder (folder : string) (debug : bool) : Map<string, Version> =
            let cacheTime =
                match Map.tryFind folder !cacheTimes with
                    | Some t -> t
                    | None -> DateTime.MinValue

            let modTime = latestModificationDate folder

            let code = 
                if modTime > cacheTime then
                    if System.Environment.OSVersion.Platform = System.PlatformID.Unix then
                        Process.shellExec 
                            { CommandLine = sprintf "-c \"%s CreatePackage %s\"" "./build.sh" (if debug then "--debug Configuration=Debug" else ""); 
                              Program = "/bin/bash"; WorkingDir = folder; Args = [] }
                    else
                        Process.shellExec 
                            { CommandLine = sprintf "/C %s CreatePackage %s" "build.cmd" (if debug then "--debug Configuration=Debug" else ""); 
                              Program = "cmd.exe"; WorkingDir = folder; Args = [] }
                else
                    0

            if code <> 0 then
                failwithf "failed to build: %A" folder
            else
                cacheTimes := Map.add folder DateTime.Now !cacheTimes
                let binPath = Path.Combine(folder, "bin", "*.nupkg")
                !!binPath 
                    |> Seq.choose (fun str ->
                        let m = packageNameRx.Match str
                        if m.Success then
                            Some (m.Groups.["name"].Value, Version.Parse m.Groups.["version"].Value)
                        else
                            None
                        )
                    |> Seq.groupBy fst
                    |> Seq.map (fun (id,versions) -> (id, versions |> Seq.map snd |> Seq.max))
                    |> Map.ofSeq

        printfn "grabbing packages"
        let sourcePackages = 
            sourceLines 
                |> List.map (fun line -> 
                        match line.Split(';') with
                         | [|source|] -> source, buildSourceFolder source true
                         | [|source;config|] -> source, buildSourceFolder source (if config.ToLower() = "debug" then true else false)
                         | _ -> failwithf "could not parse source dependency line (sources.lock): %s" line
                    ) 
                |> Map.ofList
        printfn "source packages: %A" sourcePackages
        //let installedPackages = paketDependencies.GetInstalledPackages() |> List.map (fun (a,_,_) -> a) |> Set.ofList


        for (source, packages) in Map.toSeq sourcePackages do
            for (id, version) in Map.toSeq packages do
                let fileName = sprintf "%s.%s.nupkg" id (string version)
                let path = Path.Combine(source, "bin", fileName)
                let installPath = Path.Combine("packages", id)

                if tryDeleteDir installPath && installPackage path then
                    Trace.logfn "reinstalled %A" id
                else
                    Trace.traceErrorfn "failed to reinstall: %A" id

        File.WriteAllLines(cacheFile, !cacheTimes |> Map.toSeq |> Seq.map (fun (a, time) -> sprintf "%s;%d" a time.Ticks))

    // add source-dependencies
    let addSources (sln : string) (folders : list<string>) = 
        
        let folders = folders |> List.filter Directory.Exists
        match folders with
            | [] -> 
                Trace.traceImportant "no sources found"
            | folders ->
                let taskName = sprintf "adding sources: %A" folders
          
                Trace.traceVerbose "reading sources.lock"
                let sourceFolders =
                    if File.Exists sourcesFileName then 
                        File.ReadAllLines sourcesFileName |> Set.ofArray
                    else 
                        Set.empty

                let newSourceFolders = Set.union (Set.ofList folders) sourceFolders

                Trace.traceVerbose "writing to sources.lock"
                File.WriteAllLines(sourcesFileName, newSourceFolders)

                //trace "restoring missing packages"
                //try paketDependencies.Restore() // TODO! Trigger manually!
                //with e -> traceError (sprintf "failed to restore packages: %A" e.Message)
                
                shellExecutePaket (Some sln) "restore"

                installSources()

    // remove source dependencies
    let removeSources (sln : string) (folders : list<string>) =
        let sourceFolders =
            if File.Exists sourcesFileName then 
                File.ReadAllLines sourcesFileName |> Set.ofArray
            else 
                Set.empty

        let newSourceFolders = Set.difference sourceFolders (Set.ofList folders)

        if Set.isEmpty newSourceFolders then
            deleteFile sourcesFileName
        else
            File.WriteAllLines(sourcesFileName, newSourceFolders)

        for f in folders do
            let packages = findCreatedPackages f
            for (id,v) in packages do
                let path = Path.Combine("packages", id)
                deleteDir path

        shellExecutePaket (Some sln)  "restore"

        installSources()

