﻿// Based upon C# code by Sergiy Sakharov (sakharov@gmail.com)
// http://code.google.com/p/dot-net-coverage/source/browse/trunk/Coverage.Counter/Coverage.Counter.csproj

namespace AltCover.Recorder

open System
open System.Collections.Generic
open System.Reflection
open System.Resources
open System.Runtime.CompilerServices

type internal Close =
    | DomainUnload
    | ProcessExit

type internal Message = 
    | SequencePoint of String*int
    | Finish of Close * AsyncReplyChannel<Close>

module Instance =

  // Can't hard-code what with .net-core and .net-core tests as well as classic .net
  // all giving this a different namespace
  let private resource = Assembly.GetExecutingAssembly().GetManifestResourceNames()
                         |> Seq.map (fun s -> s.Substring(0, s.Length - 10)) // trim ".resources"
                         |> Seq.find (fun n -> n.EndsWith("Strings", StringComparison.Ordinal))
  let internal resources = ResourceManager(resource , Assembly.GetExecutingAssembly())

  let GetResource s =
    [
      System.Globalization.CultureInfo.CurrentUICulture.Name
      System.Globalization.CultureInfo.CurrentUICulture.Parent.Name
      "en"
    ]
    |> Seq.map (fun l -> resources.GetString(s + "." + l))
    |> Seq.tryFind (String.IsNullOrEmpty >> not)

  /// <summary>
  /// Gets the location of coverage xml file
  /// This property's IL code is modified to store actual file location
  /// </summary>
  [<MethodImplAttribute(MethodImplOptions.NoInlining)>]
  let ReportFile = "Coverage.Default.xml"

  /// <summary>
  /// Accumulation of visit records
  /// </summary>
  let internal Visits = new Dictionary<string, Dictionary<int, int>>();

  /// <summary>
  /// Gets the unique token for this instance
  /// This property's IL code is modified to store a GUID-based token
  /// </summary>
  [<MethodImplAttribute(MethodImplOptions.NoInlining)>]
  let Token = "AltCover"

  /// <summary>
  /// Interlock for report instances
  /// </summary>
  let internal mutex = new System.Threading.Mutex(false, Token + ".mutex");

  /// <summary>
  /// Reporting back to the mother-ship; only on the .net core build
  /// because this API isn't available in .net 2.0 (framework back-version support)
  /// </summary>
  let mutable internal trace = Tracer.Create (ReportFile + ".bin")

  let internal WithMutex (f : bool -> 'a) =
    let own = mutex.WaitOne(10000)
    try
      f(own)
    finally
      if own then mutex.ReleaseMutex()

  /// <summary>
  /// Save sequence point hit counts to xml report file
  /// </summary>
  /// <param name="hitCounts">The coverage results to incorporate</param>
  /// <param name="coverageFile">The coverage file to update as a stream</param>
  let internal UpdateReport (counts:Dictionary<string, Dictionary<int, int>>) coverageFile =
    WithMutex (fun own -> Counter.UpdateReport own counts coverageFile)

  let internal IsFinish msg =
    match msg with
    | ProcessExit -> true
    | _ -> false

  /// <summary>
  /// This method flushes hit count buffers.
  /// </summary>
  let internal FlushCounterImpl (finish:Close) _ =
    trace.OnConnected (fun () -> finish |> IsFinish |> (trace.OnFinish Visits))
      (fun () ->
      if IsFinish finish then
        trace.Close()
      match Visits.Count with
      | 0 -> ()
      | _ -> let counts = Dictionary<string, Dictionary<int, int>> Visits
             Visits.Clear()
             WithMutex (fun own ->
                let delta = Counter.DoFlush own counts ReportFile
                GetResource "Coverage statistics flushing took {0:N} seconds"
                |> Option.iter (fun s -> Console.Out.WriteLine(s, delta.TotalSeconds))
             ))

  let internal TraceVisit moduleId hitPointId =
     trace.OnVisit Visits moduleId hitPointId

  /// <summary>
  /// This method is executed from instrumented assemblies.
  /// </summary>
  /// <param name="moduleId">Assembly being visited</param>
  /// <param name="hitPointId">Sequence Point identifier</param>
  let internal VisitImpl moduleId hitPointId =
    if not <| String.IsNullOrEmpty(moduleId) then
      trace.OnConnected (fun () -> TraceVisit moduleId hitPointId)
                        (fun () -> Counter.AddVisit Visits moduleId hitPointId)

  let internal mailbox = new MailboxProcessor<Message>(fun inbox ->
    let rec loop prev =
        async { 
           let! msg = inbox.TryReceive(1000)
           match msg with
           | Some (SequencePoint (moduleId, hitPointId)) ->
               VisitImpl moduleId hitPointId
               return! loop moduleId
           | Some (Finish (mode, channel)) -> 
               FlushCounterImpl mode ()
               channel.Reply mode
           | None -> 
               return! loop prev
        }

    loop String.Empty)

  let Visit moduleId hitPointId =
    let message = SequencePoint (moduleId, hitPointId)
    mailbox.Post message

  let internal FlushCounter (finish:Close) _ =  
    mailbox.PostAndReply (fun c -> Finish (finish, c)) |> ignore

  // Register event handling
  do
    AppDomain.CurrentDomain.DomainUnload.Add(FlushCounter DomainUnload)
    AppDomain.CurrentDomain.ProcessExit.Add(FlushCounter ProcessExit)
    trace <- trace.OnStart ()
    mailbox.Start()