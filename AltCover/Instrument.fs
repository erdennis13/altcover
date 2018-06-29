﻿// Based upon C# code by Sergiy Sakharov (sakharov@gmail.com)
// http://code.google.com/p/dot-net-coverage/source/browse/trunk/Coverage/Instrument/CounterAssemblyBuilder.cs
// http://code.google.com/p/dot-net-coverage/source/browse/trunk/Coverage/Instrument/InstrumentorVisitor.cs

namespace AltCover

open System
open System.Collections.Generic
open System.Diagnostics.CodeAnalysis
open System.IO
open System.Reflection
open System.Resources

open Augment
open Mono.Cecil
open Mono.Cecil.Cil
open Mono.Cecil.Rocks
open Newtonsoft.Json.Linq

/// <summary>
/// Module to handle instrumentation visitor
/// </summary>
module Instrument =
  [<ExcludeFromCodeCoverage>]
  type internal RecorderRefs = { Visit : MethodReference
                                 Push : MethodReference
                                 Pop : MethodReference }
  with static member Build () = { Visit = null; Push = null; Pop = null }
  /// <summary>
  /// State object passed from visit to visit
  /// </summary>
  [<ExcludeFromCodeCoverage>]
  type internal Context = { InstrumentedAssemblies : string list
                            ModuleId : String
                            RecordingAssembly : AssemblyDefinition
                            RecordingMethod : MethodDefinition list // initialised once
                            RecordingMethodRef : RecorderRefs // updated each module
                            MethodBody : MethodBody
                            MethodWorker : ILProcessor } // to save fetching repeatedly
  with static member Build assemblies =
                    { InstrumentedAssemblies = assemblies
                      ModuleId = String.Empty
                      RecordingAssembly = null
                      RecordingMethod = []
                      RecordingMethodRef = RecorderRefs.Build()
                      MethodBody = null
                      MethodWorker = null }

  let private resources = ResourceManager("AltCover.JSONFragments" , Assembly.GetExecutingAssembly())
  let version = typeof<AltCover.Recorder.Tracer>.Assembly.GetName().Version.ToString()

  let monoRuntime = "Mono.Runtime" |> Type.GetType |> isNull |> not
#if NETCOREAPP2_0
  let dependencies = (resources.GetString "netcoreDependencies").Replace("version",
                                                                          version)
  let runtime = (resources.GetString "netcoreRuntime").Replace("AltCover.Recorder.g/version",
                                                               "AltCover.Recorder.g/" + version)
  let newLibraries = (resources.GetString "netcoreLibraries").Replace("AltCover.Recorder.g/version",
                                                                      "AltCover.Recorder.g/" + version)
#else
  let dependencies = (resources.GetString "frameworkDependencies").Replace("version",
                                                                            version)
  let runtime = (resources.GetString "frameworkRuntime").Replace("AltCover.Recorder.g/version",
                                                                  "AltCover.Recorder.g/" + version)
  let newLibraries = (resources.GetString "frameworkLibraries").Replace("AltCover.Recorder.g/version",
                                                                          "AltCover.Recorder.g/" + version)
#endif

  /// <summary>
  /// Workround for not being able to take typeof<SomeModule> even across
  /// assembly boundaries -- start with a pure type then iterate to the module
  /// </summary>
  /// <returns>A representation of the type used to record all coverage visits.</returns>
  let internal RecorderInstanceType () =
    let trace  = typeof<AltCover.Recorder.Tracer>
    trace.Assembly.GetExportedTypes()
    |> Seq.find (fun (t:Type) -> t.FullName = "AltCover.Recorder.Instance")

    /// <summary>
    /// Locate the method that must be called to register a code point for coverage visit.
    /// </summary>
    /// <param name="assembly">The assembly containing the recorder method</param>
    /// <returns>A representation of the method to call to signal a coverage visit.</returns>
  let internal RecordingMethod (recordingAssembly : AssemblyDefinition) =
    let other = RecorderInstanceType()
    ["Visit"; "Push"; "Pop"]
    |> List.map (fun n -> let t = other.GetMethod(n).MetadataToken
                          recordingAssembly.MainModule.LookupToken(t) :?> MethodDefinition)

  /// <summary>
  /// Applies a new key to an assembly name
  /// </summary>
  /// <param name="assemblyName">The name to update</param>
  /// <param name="key">The possibly empty key to use</param>
  let internal UpdateStrongNaming (assembly:AssemblyDefinition) (key:StrongNameKeyPair option) =
    let assemblyName = assembly.Name
#if NETCOREAPP2_0
    do
#else
    match key with
    | None ->
#endif
              assembly.MainModule.Attributes <- assembly.MainModule.Attributes &&&
                                                   (~~~ModuleAttributes.StrongNameSigned)
              assemblyName.HasPublicKey <- false
              assemblyName.PublicKey <- null
              assemblyName.PublicKeyToken <- null
#if NETCOREAPP2_0
#else
    | Some key' -> assemblyName.HasPublicKey <- true
                   assemblyName.PublicKey <- key'.PublicKey // sets token implicitly
#endif

  /// <summary>
  /// Locate the key, if any, which was used to name this assembly.
  /// </summary>
  /// <param name="name">The name of the assembly</param>
  /// <returns>A key, if we have a match.</returns>
  let internal KnownKey (name:AssemblyNameDefinition) =
      if not name.HasPublicKey then
        None
      else
        let index = KeyStore.ArrayToIndex name.PublicKey
        match Visitor.keys.TryGetValue(index) with
        | (false, _ ) -> None
        | (_, record) -> Some record.Pair

  /// <summary>
  /// Locate the key, if any, which was used to name this assembly.
  /// </summary>
  /// <param name="name">The name of the assembly</param>
  /// <returns>A key, if we have a match.</returns>
  let internal KnownToken (name:AssemblyNameReference) =
    let pktoken = name.PublicKeyToken
    if pktoken.Length <> 8  then
        None
    else
        let index = KeyStore.TokenAsULong pktoken
        match Visitor.keys.TryGetValue(index) with
        | (false, _ ) -> None
        | (_, record) -> Some record

  // This trivial extraction appeases Gendarme
  let private extractName (assembly: AssemblyDefinition) =
     assembly.Name.Name

  let Guard (assembly:AssemblyDefinition) (f:unit->unit) =
    try
      f()
      assembly
    with
    | _ -> (assembly :> IDisposable).Dispose()
           reraise()

  /// <summary>
  /// Create the new assembly that will record visits, based on the prototype.
  /// </summary>
  /// <returns>A representation of the assembly used to record all coverage visits.</returns>
  let internal PrepareAssembly (location:string) =
    let definition = AssemblyDefinition.ReadAssembly(location)
    Guard definition (fun () ->
    ProgramDatabase.ReadSymbols definition
    definition.Name.Name <- (extractName definition) + ".g"
#if NETCOREAPP2_0
    let pair = None
#else
    use stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AltCover.Recorder.snk")
    use buffer = new MemoryStream()
    stream.CopyTo(buffer)
    let pair = Some (StrongNameKeyPair(buffer.ToArray()))
#endif
    UpdateStrongNaming definition pair

    // set the coverage file path and unique token
    [
      ("get_ReportFile", Visitor.ReportPath())
      ("get_Token", "Altcover-" + Guid.NewGuid().ToString())
    ]
    |> List.iter (fun (property, value) ->
        let pathGetterDef = definition.MainModule.GetTypes()
                            |> Seq.collect (fun t -> t.Methods)
                            |> Seq.filter (fun m -> m.Name = property)
                            |> Seq.head

        let body = pathGetterDef.Body
        let worker = body.GetILProcessor()
        let initialBody = body.Instructions |> Seq.toList
        let head = initialBody |> Seq.head
        worker.InsertBefore(head, worker.Create(OpCodes.Ldstr, value))
        worker.InsertBefore(head, worker.Create(OpCodes.Ret))
        initialBody |> Seq.iter worker.Remove
    )

    // set the coverage file format
    [
      ("get_CoverageFormat", Visitor.ReportFormat() |> int)
    ]
    |> List.iter (fun (property, value) ->
        let pathGetterDef = definition.MainModule.GetTypes()
                            |> Seq.collect (fun t -> t.Methods)
                            |> Seq.filter (fun m -> m.Name = property)
                            |> Seq.head

        let body = pathGetterDef.Body
        let worker = body.GetILProcessor()
        let initialBody = body.Instructions |> Seq.toList
        let head = initialBody |> Seq.head
        worker.InsertBefore(head, worker.Create(OpCodes.Ldc_I4, value))
        worker.InsertBefore(head, worker.Create(OpCodes.Ret))
        initialBody |> Seq.iter worker.Remove
    )

    // set the timer interval in ticks
    [
      ("get_Timer", Visitor.Interval())
    ]
    |> List.iter (fun (property, value) ->
        let pathGetterDef = definition.MainModule.GetTypes()
                            |> Seq.collect (fun t -> t.Methods)
                            |> Seq.filter (fun m -> m.Name = property)
                            |> Seq.head

        let body = pathGetterDef.Body
        let worker = body.GetILProcessor()
        let initialBody = body.Instructions |> Seq.toList
        let head = initialBody |> Seq.head
        worker.InsertBefore(head, worker.Create(OpCodes.Ldc_I4, value))
        worker.InsertBefore(head, worker.Create(OpCodes.Conv_I8))
        worker.InsertBefore(head, worker.Create(OpCodes.Ret))
        initialBody |> Seq.iter worker.Remove
    ))

#if NETCOREAPP2_0
#else
  let internal CreateSymbolWriter pdb isWindows isMono =
    match (isWindows, isMono) with
    | (true, true) -> Mono.Cecil.Mdb.MdbWriterProvider() :> ISymbolWriterProvider
    | (true, false) -> match pdb   with
                       | ".pdb" -> Mono.Cecil.Pdb.PdbWriterProvider() :> ISymbolWriterProvider
                       | _ -> Mono.Cecil.Mdb.MdbWriterProvider() :> ISymbolWriterProvider
    | _ -> null
#endif

#if NETCOREAPP2_0
  let private nugetCache = Path.Combine(Path.Combine (Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
                                                      ".nuget"), "packages")

  let internal ResolutionTable = Dictionary<string, AssemblyDefinition>()

  let internal FindAssemblyName f =
    try
      (AssemblyName.GetAssemblyName f).ToString()
    with
    | :? ArgumentException
    | :? FileNotFoundException
    | :? System.Security.SecurityException
    | :? BadImageFormatException
    | :? FileLoadException -> String.Empty

  let internal ResolveFromNugetCache _ (y:AssemblyNameReference) =
    let name = y.ToString()
    if ResolutionTable.ContainsKey name then ResolutionTable.[name]
    else
        let candidate = Directory.GetFiles(nugetCache, y.Name + ".*", SearchOption.AllDirectories)
                        |> Array.rev
                        |> Seq.filter(fun f -> let x = Path.GetExtension f
                                               x.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                                               x.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                        |> Seq.filter (fun f -> y.ToString().Equals(FindAssemblyName f, StringComparison.Ordinal))
                        |> Seq.tryHead
        match candidate with
        | None -> null
        | Some x -> String.Format(System.Globalization.CultureInfo.CurrentCulture,
                      CommandLine.resources.GetString "resolved", y.ToString(), x)
                    |> (Output.WarnOn true)
                    let a = AssemblyDefinition.ReadAssembly x
                    ResolutionTable.[name] <- a
                    a

  let internal HookResolver (resolver:IAssemblyResolver) =
    if resolver |> isNull |> not then
      let hook = resolver.GetType().GetMethod("add_ResolveFailure")
      hook.Invoke(resolver, [|new AssemblyResolveEventHandler(ResolveFromNugetCache) :> obj |])
      |> ignore
#endif

  /// <summary>
  /// Commit an instrumented assembly to disk
  /// </summary>
  /// <param name="assembly">The instrumented assembly object</param>
  /// <param name="path">The full path of the output file</param>
  /// <remark>Can raise "System.Security.Cryptography.CryptographicException: Keyset does not exist" at random
  /// when asked to strongname.  This writes a new .pdb/.mdb alongside the instrumented assembly</remark>
  let internal WriteAssembly (assembly:AssemblyDefinition) (path:string) =
    let pkey = Mono.Cecil.WriterParameters()
    let pdb = ProgramDatabase.GetPdbWithFallback assembly
              |> Option.getOrElse "x.pdb" |> Path.GetExtension
#if NETCOREAPP2_0
    // Once Cecil 0.10 beta6 is taken out of the equation, this works
    pkey.WriteSymbols <- true

    pkey.SymbolWriterProvider <- match pdb with
                                 | ".pdb" -> Mono.Cecil.Pdb.PdbWriterProvider() :> ISymbolWriterProvider
                                 | _ -> Mono.Cecil.Mdb.MdbWriterProvider() :> ISymbolWriterProvider
#else

    // Assembly with pdb writing fails on mono on Windows when writing with
    // System.NullReferenceException : Object reference not set to an instance of an object.
    // from deep inside Cecil
    // Pdb writing fails on mono on non-Windows with
    // System.DllNotFoundException : ole32.dll
    //  at (wrapper managed-to-native) Mono.Cecil.Pdb.SymWriter:CoCreateInstance
    // Mdb writing now fails in .net framework, it throws
    // Mono.CompilerServices.SymbolWriter.MonoSymbolFileException :
    // Exception of type 'Mono.CompilerServices.SymbolWriter.MonoSymbolFileException' was thrown.
    // If there are portable .pdbs on mono, those fail to write, too with
    // Mono.CompilerServices.SymbolWriter.MonoSymbolFileException :
    // Exception of type 'Mono.CompilerServices.SymbolWriter.MonoSymbolFileException' was thrown.
    let isWindows = System.Environment.GetEnvironmentVariable("OS") = "Windows_NT"
    pkey.WriteSymbols <- isWindows
    pkey.SymbolWriterProvider <- CreateSymbolWriter pdb isWindows monoRuntime

    // Also, there are no strongnames in .net core
    KnownKey assembly.Name
    |> Option.iter (fun key -> pkey.StrongNameKeyPair <- key)
#endif
    let here = Directory.GetCurrentDirectory()
    try
        Directory.SetCurrentDirectory(Path.GetDirectoryName(path))
        let write (a:AssemblyDefinition) p pk  =
          use sink = File.Open (p, FileMode.Create, FileAccess.ReadWrite)
          a.Write(sink, pk)

#if NETCOREAPP2_0
        let resolver = assembly.MainModule.AssemblyResolver
        HookResolver resolver
#endif

        write assembly path pkey
    finally
        Directory.SetCurrentDirectory(here)

  type internal SubstituteInstruction (oldValue:Instruction, newValue:Instruction) =
    /// <summary>
    /// Adjust the IL for exception handling
    /// </summary>
    /// <param name="handler">The exception handler</param>
    /// <param name="oldBoundary">The uninstrumented location</param>
    /// <param name="newBoundary">Where it has moved to</param>
    member this.SubstituteExceptionBoundary (handler:ExceptionHandler) =
      if handler.FilterStart = oldValue then handler.FilterStart <- newValue
      if handler.HandlerEnd = oldValue then handler.HandlerEnd <- newValue
      if handler.HandlerStart = oldValue then handler.HandlerStart <- newValue
      if handler.TryEnd = oldValue then handler.TryEnd <- newValue
      if handler.TryStart = oldValue then handler.TryStart <- newValue

    /// <summary>
    /// Adjust the IL to substitute an opcode
    /// </summary>
    /// <param name="instruction">Instruction being processed</param>
    /// <param name="oldOperand">Type we are looking for</param>
    /// <param name="newOperand">Type to replace it with</param>
    member this.SubstituteInstructionOperand(instruction:Instruction) =
      // Performance reasons - only 3 types of operators have operands of Instruction types
      // instruction.Operand getter - is rather slow to execute it for every operator
      match instruction.OpCode.OperandType with
      | OperandType.InlineBrTarget
      | OperandType.ShortInlineBrTarget ->
        if instruction.Operand = (oldValue :> Object) then
           instruction.Operand <- newValue
        // At this point instruction.Operand will be either Operand != oldOperand
        // or instruction.Operand will be of type Instruction[]
        // (in other words - it will be a switch operator's operand)
      | OperandType.InlineSwitch ->
           let operands = instruction.Operand :?> Instruction array
           let offset = operands
                        |> Seq.tryFindIndex (fun x -> x = oldValue)
           match offset with
           | Some i -> // operands.[i] <- newValue : fails with "This expression was expected to have type    ''a [] * int * 'a'    but here has type    'Instruction array'"
                       Array.set operands i newValue // so mutate the array like this instead
           | _ -> ()
      | _ -> ()

  let internal InsertVisit (instruction:Instruction) (methodWorker:ILProcessor) (recordingMethodRef:MethodReference) (moduleId:string) (point:int) =
      let counterMethodCall = methodWorker.Create(OpCodes.Call, recordingMethodRef)
      let instrLoadModuleId = methodWorker.Create(OpCodes.Ldstr, moduleId)
      let instrLoadPointId = methodWorker.Create(OpCodes.Ldc_I4, point)

      methodWorker.InsertBefore(instruction, instrLoadModuleId)
      methodWorker.InsertAfter(instrLoadModuleId, instrLoadPointId)
      methodWorker.InsertAfter(instrLoadPointId, counterMethodCall)
      instrLoadModuleId

  /// <summary>
  /// Determine new names for input strong-named assemblies; if we have a key and
  /// the assembly was already strong-named then give it the new key token, otherwise
  /// set that there is no strongname.
  /// </summary>
  /// <param name="assembly">The assembly object being operated upon</param>
  let internal UpdateStrongReferences (assembly : AssemblyDefinition) =
    let effectiveKey = if assembly.Name.HasPublicKey then Visitor.defaultStrongNameKey else None
    UpdateStrongNaming assembly effectiveKey
    assembly.MainModule.Attributes <- assembly.MainModule.Attributes &&& (~~~ModuleAttributes.StrongNameSigned)

  let internal injectJSON json =
    let o = JObject.Parse json

    let target = ((o.Property "runtimeTarget").Value :?> JObject).Property("name").Value.ToString()
    let targets = (o.Properties()
                    |> Seq.find (fun p -> p.Name = "targets")).Value :?> JObject
    let targeted = (targets.Properties()
                    |> Seq.find (fun p -> p.Name= target)).Value :?> JObject

    let app = (targeted.PropertyValues() |> Seq.head)  :?> JObject

    let existingDependencies = app.Properties() |> Seq.tryFind (fun p -> p.Name = "dependencies")
    let prior = match existingDependencies with
                | None -> Set.empty<string>
                | Some p -> (p.Value :?> JObject).Properties()
                            |> Seq.map (fun p -> p.Name)
                            |> Set.ofSeq

    let rawDependencies = (JObject.Parse dependencies).Properties()
                           |> Seq.find (fun p -> p.Name = "dependencies")
    match app.Properties() |> Seq.tryFind (fun p -> p.Name = "dependencies") with
    | None -> app.AddFirst(rawDependencies)
    | Some p -> (rawDependencies.Value :?> JObject).Properties()
                |> Seq.filter (fun r -> prior |> Set.contains r.Name |> not)
                |> Seq.iter (fun r -> (p.Value :?> JObject).Add(r))

    let rt = JObject.Parse runtime
    rt.Properties()
    |> Seq.filter (fun r -> prior |> Set.contains (r.Name.Split('/') |> Seq.head) |> not
                            && targeted.ContainsKey(r.Name) |> not)
    |> Seq.iter (fun r -> targeted.Add(r))

    let libraries = (o.Properties()
                    |> Seq.find (fun p -> p.Name = "libraries")).Value :?> JObject
    (JObject.Parse newLibraries).Properties()
    |> Seq.filter (fun r -> prior |> Set.contains (r.Name.Split('/') |> Seq.head) |> not
                            && libraries.ContainsKey(r.Name) |> not)
    |> Seq.rev
    |> Seq.iter (libraries.AddFirst)
    o.ToString()

  let private VisitModule (state : Context) (m:ModuleDefinition) included =
         let restate = match included <> Inspect.Ignore with
                       | true ->
                         let recordingMethod = match state.RecordingMethod with
                                               | [] -> RecordingMethod state.RecordingAssembly
                                               | _ -> state.RecordingMethod
                         let refs = recordingMethod |> List.map m.ImportReference
                         { state with
                               RecordingMethodRef = { Visit = refs.[0]; Push = refs.[1]; Pop = refs.[2] }
                               RecordingMethod = recordingMethod }
                       | _ -> state
         { restate with ModuleId = match Visitor.ReportKind() with
                                   | AltCover.Base.ReportFormat.OpenCover -> KeyStore.HashFile m.FileName
                                   | _ -> m.Mvid.ToString() }

  let private VisitMethod (state : Context) (m:MethodDefinition) included =
         match Visitor.IsInstrumented included with
         | true ->
           let body = m.Body
           { state with
              MethodBody = body
              MethodWorker = body.GetILProcessor() }
         | _ -> state

  let private UpdateBranchReferences (body:MethodBody) instruction injected =
    // Change references in operands from "instruction" to first counter invocation instruction (instrLoadModuleId)
    let subs = SubstituteInstruction (instruction, injected)
    body.Instructions
    |> Seq.iter subs.SubstituteInstructionOperand

    body.ExceptionHandlers
    |> Seq.iter subs.SubstituteExceptionBoundary

  let private VisitMethodPoint (state : Context) instruction point included =
    if included then // by construction the sequence point is included
      let instrLoadModuleId = InsertVisit instruction state.MethodWorker state.RecordingMethodRef.Visit state.ModuleId point
      UpdateBranchReferences state.MethodBody instruction instrLoadModuleId
    state

  let internal VisitBranchPoint (state:Context) branch =
    if branch.Included && state.MethodWorker |> isNull |> not then
      let point = (branch.Uid ||| Base.Counter.BranchFlag)
      let instrument instruction =
        InsertVisit instruction state.MethodWorker state.RecordingMethodRef.Visit state.ModuleId point

      let updateSwitch update =
        let operands = branch.Start.Operand :?> Instruction[]
        branch.Indexes
        |> Seq.filter (fun i -> i >= 0)
        // See SubstituteInstructionOperand for why we do it this way
        |> Seq.iter (fun i -> Array.set operands i update)

      match branch.Indexes |> Seq.tryFind (fun i -> i = -1) with
      | Some _ -> // immediate next instruction; by construction this one comes first
        // before
        // Cond_Branch xxx
        // Next
        //
        // after
        // Cond_Branch xxx
        // jump instrument#-1
        // instrument#-1
        // Next
        let target = branch.Start.Next
        let preamble = instrument target
        let jump = state.MethodWorker.Create(OpCodes.Br, preamble)
        state.MethodWorker.InsertAfter (branch.Start, jump)
        if branch.Start.OpCode = OpCodes.Switch then
          updateSwitch jump
      | None ->
        // before
        // Cond_Branch #n
        // jump instrument#-1
        // ...
        // instrument#-1
        // Next
        //
        // after
        // Cond_Branch instrument#n
        // jump instrument#-1
        // instrument#n
        // jump #n
        // ...
        // instrument#-1
        // Next
        let target = if branch.Start.OpCode = OpCodes.Switch then
                      branch.Start.Operand :?> Instruction[]
                      |> Seq.skip (branch.Indexes.Head)
                      |> Seq.head
                     else branch.Start.Operand :?> Instruction
        let jump = state.MethodWorker.Create(OpCodes.Br, target)
        state.MethodWorker.InsertAfter (branch.Start.Next, jump)
        let preamble = instrument jump
        if branch.Start.OpCode = OpCodes.Switch then
          updateSwitch preamble
        else branch.Start.Operand <- preamble
    state

  let private FinishVisit (state : Context) =
    try
                 let counterAssemblyFile = Path.Combine(Visitor.InstrumentDirectory(), (extractName state.RecordingAssembly) + ".dll")
                 WriteAssembly (state.RecordingAssembly) counterAssemblyFile
                 Directory.GetFiles(Visitor.InstrumentDirectory(), "*.deps.json", SearchOption.TopDirectoryOnly)
                 |> Seq.iter (fun f -> File.WriteAllText(f, (f |> File.ReadAllText |> injectJSON))                                       )
#if NETCOREAPP2_0
                 let fsharplib = Path.Combine(Visitor.InstrumentDirectory(), "FSharp.Core.dll")
                 if not (File.Exists fsharplib) then
                   use fsharpbytes = new FileStream(AltCover.Recorder.Tracer.Core(), FileMode.Open, FileAccess.Read)
                   use libstream = new FileStream(fsharplib, FileMode.Create)
                   fsharpbytes.CopyTo libstream
#endif
    finally
      (state.RecordingAssembly :>IDisposable).Dispose()

    { state with RecordingAssembly = null }

  let internal Track state (m:MethodDefinition) included (track:(int*string) option) =
    track
    |> Option.iter (fun (n,_) ->
            let body = if Visitor.IsInstrumented included then state.MethodBody else m.Body
            let instructions = body.Instructions
            let methodWorker = body.GetILProcessor()

            let nop = methodWorker.Create(OpCodes.Nop)

            let rets = instructions
                       |> Seq.filter (fun i -> i.OpCode = OpCodes.Ret)
                       |> Seq.toList

            let tailcalls = instructions
                            |> Seq.filter (fun i -> i.OpCode = OpCodes.Tail)
                            |> Seq.toList

            let tail = instructions |> Seq.last
            let popper = methodWorker.Create(OpCodes.Call, state.RecordingMethodRef.Pop)
            methodWorker.InsertAfter(tail, popper)
            let enfin = methodWorker.Create(OpCodes.Endfinally)
            methodWorker.InsertAfter(popper, enfin)
            let ret = methodWorker.Create(OpCodes.Ret)
            methodWorker.InsertAfter(enfin, ret)

            rets
            |> Seq.iter (fun i -> let leave = methodWorker.Create(OpCodes.Leave, ret)
                                  UpdateBranchReferences body i leave
                                  methodWorker.Replace (i, leave) )

            tailcalls
            |> Seq.iter (fun i -> UpdateBranchReferences body i i.Next
                                  methodWorker.Remove i)

            let handler = ExceptionHandler(ExceptionHandlerType.Finally)
            handler.TryStart <- instructions |> Seq.head
            handler.TryEnd <- popper
            handler.HandlerStart <- popper
            handler.HandlerEnd <- ret
            body.ExceptionHandlers.Add handler

            let pushMethodCall = methodWorker.Create(OpCodes.Call, state.RecordingMethodRef.Push)
            let instrLoadId = methodWorker.Create(OpCodes.Ldc_I4, n)
            methodWorker.InsertBefore (handler.TryStart, pushMethodCall)
            methodWorker.InsertBefore(pushMethodCall, instrLoadId)
        )

  let private VisitAfterMethod state m included track =
    if Visitor.IsInstrumented included then
        let body = state.MethodBody
        // changes conditional (br.s, brtrue.s ...) operators to corresponding "long" ones (br, brtrue)
        body.SimplifyMacros()
        // changes "long" conditional operators to their short representation where possible
        body.OptimizeMacros()
    Track state m included track
    state

  let private VisitAfterAssembly state (assembly:AssemblyDefinition) =
    let originalFileName = Path.GetFileName assembly.MainModule.FileName
    let path = Path.Combine(Visitor.InstrumentDirectory(), originalFileName)
    String.Format(System.Globalization.CultureInfo.CurrentCulture,
                  CommandLine.resources.GetString "instrumented", assembly, path)
    |> Output.Info
    WriteAssembly assembly path
    state

  let private VisitStart state =
    let recorder = typeof<AltCover.Recorder.Tracer>
    let recordingAssembly = PrepareAssembly(recorder.Assembly.Location)
    { state with RecordingAssembly = recordingAssembly }

  /// <summary>
  /// Perform visitor operations
  /// </summary>
  /// <param name="state">Contextual information for the visit</param>
  /// <param name="node">The node being visited</param>
  /// <returns>Updated state</returns>
  let internal InstrumentationVisitorCore (state : Context) (node:Node) =
    match node with
    | Start _ -> VisitStart state
    | Assembly (assembly, included) -> if included <> Inspect.Ignore then
                                             assembly.MainModule.AssemblyReferences.Add(state.RecordingAssembly.Name)
                                       state
    | Module (m, included) -> VisitModule state m included
    | Type _ -> state
    | Method (m,  included, _) -> VisitMethod state m included

    | MethodPoint (instruction, _, point, included) ->
               VisitMethodPoint state instruction point included
    | BranchPoint branch -> VisitBranchPoint state branch
    | AfterMethod (m, included, track) -> VisitAfterMethod state m included track

    | AfterType -> state
    | AfterModule -> state
    | AfterAssembly assembly -> VisitAfterAssembly state assembly
    | Finish -> FinishVisit state

  let internal InstrumentationVisitorWrapper (core  : Context -> Node -> Context) (state : Context) (node:Node) =
    try
      core state node
    with
    | _ -> match node with
           | Finish -> ()
           | _ -> if state.RecordingAssembly |> isNull |> not
                  then (state.RecordingAssembly :>IDisposable).Dispose()
           reraise()

  let internal InstrumentationVisitor (state : Context) (node:Node) =
    InstrumentationVisitorWrapper InstrumentationVisitorCore state node

  /// <summary>
  /// Higher-order function that returns a visitor
  /// </summary>
  /// <param name="assemblies">List of assembly paths to visit</param>
  /// <returns>Stateful visitor function</returns>
  let internal InstrumentGenerator (assemblies : string list) =
    Visitor.EncloseState InstrumentationVisitor (Context.Build assemblies)