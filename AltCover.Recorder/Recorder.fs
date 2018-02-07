﻿// Based upon C# code by Sergiy Sakharov (sakharov@gmail.com)
// http://code.google.com/p/dot-net-coverage/source/browse/trunk/Coverage.Counter/Coverage.Counter.csproj

namespace AltCover.Recorder

open System
open System.Collections.Generic
open System.Reflection
open System.Resources
open System.Runtime.CompilerServices

[<System.Runtime.InteropServices.ProgIdAttribute("ExcludeFromCodeCoverage hack for OpenCover issue 615")>]
type internal Close =
    | DomainUnload
    | ProcessExit

[<System.Runtime.InteropServices.ProgIdAttribute("ExcludeFromCodeCoverage hack for OpenCover issue 615")>]
type internal Carrier =
    | SequencePoint of String*int

[<System.Runtime.InteropServices.ProgIdAttribute("ExcludeFromCodeCoverage hack for OpenCover issue 615")>]
type internal Message =
    | AsyncItem of Carrier
    | Item of Carrier*AsyncReplyChannel<unit>
    | Finish of Close * AsyncReplyChannel<unit>
    | Kill

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
  /// Serialize access to the report file across AppDomains for the classic mode
  /// </summary>
  let internal mutex = new System.Threading.Mutex(false, Token + ".mutex");

  /// <summary>
  /// Reporting back to the mother-ship; only on the .net core build
  /// because this API isn't available in .net 2.0 (framework back-version support)
  /// </summary>
  let mutable internal trace = Tracer.Create (ReportFile + ".bin")

  let internal WithMutex (f : bool -> 'a) =
    let own = mutex.WaitOne(1000)
    try
      f(own)
    finally
      if own then mutex.ReleaseMutex()

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

  let rec private loop (inbox:MailboxProcessor<Message>) _ =
          async {
             let! msg = inbox.Receive(1000)
             match msg with
             | Kill -> ()
             | AsyncItem (SequencePoint (moduleId, hitPointId)) ->
                 VisitImpl moduleId hitPointId
                 return! loop inbox moduleId
             | Item (SequencePoint (moduleId, hitPointId), channel)->
                 VisitImpl moduleId hitPointId
                 channel.Reply ()
                 return! loop inbox moduleId
             | Finish (mode, channel) ->
                 FlushCounterImpl mode ()
                 channel.Reply ()
          }

  let internal MakeMailbox () =
    new MailboxProcessor<Message>(fun inbox -> loop inbox String.Empty)

  let mutable internal mailbox = MakeMailbox ()

  let internal VisitSelection (f: unit -> bool) moduleId hitPointId =
    // When writing to file for the runner to process,
    // make this synchronous to avoid choking the mailbox
    // Backlogs of over 90,000 items were observed in self-test
    // which failed to drain during the ProcessExit grace period
    // when sending async messages.
    let message = SequencePoint (moduleId, hitPointId)
    if f() then
       mailbox.TryPostAndReply ((fun c -> Item (message, c)),1) |> ignore
    else message |> AsyncItem |> mailbox.Post

  let Visit moduleId hitPointId =
     VisitSelection (fun () -> trace.IsConnected() || mailbox.CurrentQueueLength > 1000)
       moduleId hitPointId

  let here = Assembly.GetExecutingAssembly().GetName().Name

  let internal FlushCounter (finish:Close) _ =
    if here <> "AltCover.Recorder" then
      mailbox.PostAndReply (fun c -> Finish (finish, c))
    else 
      Kill  |> mailbox.Post
      printfn "Flushing %A" finish

  // unit test helpers -- avoid issues with cross CLR version calls
  let internal Peek () =
    mailbox.CurrentQueueLength

  let internal RunMailbox () =
    mailbox <- MakeMailbox ()
    mailbox.Start()

  // Register event handling
  do
    AppDomain.CurrentDomain.DomainUnload.Add(FlushCounter DomainUnload)
    AppDomain.CurrentDomain.ProcessExit.Add(FlushCounter ProcessExit)
    WithMutex (fun _ -> trace <- trace.OnStart ())
    mailbox.Start()