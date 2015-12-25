open System
open Suave
open Suave.Form
open Suave.Http.Successful
open Suave.Web
open Suave.Http
open Suave.Http.Applicatives
open Suave.Model.Binding
open Suave.Http.ServerErrors
open HttpFs.Client

//very important
System.Net.ServicePointManager.DefaultConnectionLimit <- 10000

//helpers
let guid guid = System.Guid.Parse(guid)

//web stuff
let logAndShow500 error =
  printfn "%A" error
  INTERNAL_ERROR "ERROR"

let bindToForm form handler =
  bindReq (bindForm form) handler logAndShow500

type SendFormData =
  {
    JobId : string
    NumberOfRequests : string
    MaxWorkers : string
  }

type SendData =
  {
    JobId : System.Guid
    NumberOfRequests : int
    MaxWorkers : int
  }

let convertSendData (sendFormData : SendFormData) =
  {
    JobId = guid sendFormData.JobId
    NumberOfRequests = int sendFormData.NumberOfRequests
    MaxWorkers = int sendFormData.MaxWorkers
  }

let send : Form<SendFormData> = Form ([],[])

let html jobId =
 sprintf """
<html>
<body>

<form method="POST" action="/send">
  Number of requests:
  <input type="text" name="NumberOfRequests">
  <br/>
  Max number of concurrent requests:
  <input type="text" name="MaxWorkers">
  <input type="hidden" name="JobId" value="%s">
  <input type="submit" value="Send">
</form>

</body>
</html>
  """ jobId

//actor stuff
type actor<'t> = MailboxProcessor<'t>

type Command =
  | Process of string

type Worker =
  | Do
  | Retire

type Manager =
  | Initialize of SendData * Command
  | WorkerDone of float * actor<Worker>

type MetaManager =
  | Send of SendData

let doit uri =
  let stopWatch = System.Diagnostics.Stopwatch.StartNew()
  let request = createRequest Get <| Uri(uri)
  use response = getResponse request |> Async.RunSynchronously
  let responseTime = stopWatch.Elapsed.TotalMilliseconds
  responseTime

let newWorker (manager : actor<Manager>) (command : Command): actor<Worker> =
  actor.Start(fun self ->
    let rec loop () =
      async {
        let! msg = self.Receive ()
        match msg with
        | Worker.Retire ->
          return ()
        | Worker.Do ->
          match command with
          | Process uri ->
            let results = doit uri
            manager.Post(Manager.WorkerDone(results, self))
          return! loop ()
      }
    loop ())

let rec haveWorkersWork (idleWorkers : actor<Worker> list) numberOfRequests =
    match idleWorkers with
    | [] -> numberOfRequests
    | worker :: remainingWorkers ->
      worker.Post(Worker.Do)
      haveWorkersWork remainingWorkers (numberOfRequests - 1)

let newManager () : actor<Manager> =
  let sw = System.Diagnostics.Stopwatch()
  actor.Start(fun self ->
    let rec loop numberOfRequests pendingRequests results =
      async {
        let! msg = self.Receive ()
        match msg with
        | Manager.Initialize (sendData, command) ->
          //build up a list of all the work to do
          printfn "Requests: %A, Concurrency %A" sendData.NumberOfRequests sendData.MaxWorkers
          let numberOfRequests = sendData.NumberOfRequests
          let workers = [ 1 .. sendData.MaxWorkers ] |> List.map (fun _ -> newWorker self command)
          let results = []
          let pendingRequests = sendData.MaxWorkers
          sw.Restart()
          let numberOfRequests = haveWorkersWork workers numberOfRequests
          return! loop numberOfRequests pendingRequests results
        | Manager.WorkerDone(ms, worker) ->
          let results = ms :: results
          if numberOfRequests > 0 then
            let numberOfRequests = haveWorkersWork [worker] numberOfRequests
            return! loop numberOfRequests pendingRequests results
          else if pendingRequests > 1 then //if only 1 pendingRequest, then this that pendingRequest so we are done
            let pendingRequests = pendingRequests - 1
            return! loop numberOfRequests pendingRequests results
          else
            sw.Stop()
            let avg = results |> List.average
            let min = results |> List.min
            let max = results |> List.max
            printfn "Total seconds: %A, Average ms: %A, Max ms: %A, Min ms: %A" sw.Elapsed.TotalSeconds avg max min
            return! loop numberOfRequests pendingRequests results
      }
    loop 0 0 [])

let getManager managers jobId =
  let maybeManager = managers |> List.tryFind (fun (jobId', _) -> jobId' = jobId)
  match maybeManager with
    | Some (_, manager) -> manager, managers
    | None ->
      let manager = newManager()
      let managers = (jobId, manager) :: managers
      manager, managers

let newMetaManager () : actor<MetaManager> =
  actor.Start(fun self ->
    let rec loop (managers : (Guid * actor<Manager>) list) =
      async {
        let! msg = self.Receive ()
        let uri = "http://localhost/"
        match msg with
        | Send sendData ->
          let manager, managers = getManager managers sendData.JobId
          manager.Post(Initialize(sendData, Process uri))
          return! loop managers
      }
    loop [])

let metaManager = newMetaManager()

let webPart =
  choose
    [
      path "/test" >>= choose [ GET >>= OK "this is a test" ]
      path "/" >>= choose [ GET >>= warbler (fun _ -> OK <| html (System.Guid.NewGuid().ToString())) ]
      path "/send" >>= choose [ POST >>= bindToForm send
                                           (fun sendFormData ->
                                              metaManager.Post(Send(convertSendData sendFormData))
                                              OK <| html sendFormData.JobId) ]
    ]

startWebServer defaultConfig webPart
