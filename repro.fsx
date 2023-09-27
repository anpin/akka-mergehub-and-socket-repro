#r "nuget: Akkling"
#r "nuget: Akkling.Streams"
#r "nuget: Akka, Version=1.5.12"
#r "nuget: Akka.Streams, Version=1.5.12"

open System.Net
open Akka
open Akka.Actor
open Akka.Streams
open Akka.Streams.Dsl
open Akka.IO
open Akkling
open System
open Akkling.Streams
open System.Threading.Tasks
open Akkling.IO
open Akkling.IO.Tcp

type Commands =
    | ConnectTo of IPEndPoint
    | StopConnections 

let mutable run = true
let config = Configuration.parse """
    akka {
        loglevel = DEBUG
        stdout-loglevel = DEBUG
        log-config-on-start = on
        actor {
            debug {
                receive = on
                autoreceive = on
                lifecycle = on
                event-stream = on
                unhandled = on
            }
        }
    }
"""
let actorSystem = ActorSystem.Create("my-system", config)
let mat = actorSystem.Materializer()

Console.CancelKeyPress.Add(fun eventArgs ->
    printfn "Ctrl+C pressed - exiting..."
    eventArgs.Cancel <- true
    actorSystem.Dispose()
    run <- false
)
Console.WriteLine("Press Ctrl+C to exit...")


module Server = 
 let epToActorName (ep: EndPoint) =
  ep :?> IPEndPoint |> fun ie -> $"{ie.Address.ToString()}:{ie.Port}"

 let handler connection =
  fun (ctx: Actor<obj>) ->
    monitor ctx connection |> ignore

    let rec loop () =
      actor {
        let! msg = ctx.Receive()
        logDebugf ctx "[MSG][%s]" (string msg)

        match msg with
        | Received(data) ->
          let rx = string data
          logInfof ctx "[SERVER_RX][%s]" rx
          match rx with
          | "stop\n" ->
            connection <! TcpMessage.Close()
          | "abort\n" ->
            connection <! TcpMessage.Abort()
          | _ ->
            Async.Sleep 500 |> Async.RunSynchronously
            connection <! TcpMessage.Write(data)
          return! loop()
        | Terminated(_, _, _)
        | ConnectionClosed(_) -> Stop
        // |  Connected(_,_) ->
        //     connection <! TcpMessage.Write(ByteString.FromString("Hello!\n"))
        | LifecycleEvent lf ->
          if lf = LifecycleEvent.PreStart then
            connection <! TcpMessage.Write(ByteString.FromString("Hello!\n"))
        | _ -> Unhandled
      }

    loop()

 let listener  (addr: string) port  system=
  let endpoint = IPEndPoint(IPAddress.Parse(addr), port)
  spawn system $"test-echo-server-{addr}-{port}"
  <| props(fun m ->
    IO.Tcp(m) <! TcpMessage.Bind(untyped m.Self, endpoint, 100)

    let rec loop () =
      actor {
        let! (msg: obj) = m.Receive()
        logDebugf m "[MSG][%A]" msg

        match msg with
        | Connected(remote, local) ->
          let conn = m.Sender()

          conn <! TcpMessage.Register(untyped(spawn m (epToActorName remote) (props(handler conn))))
          return! loop()
        | _ -> return Unhandled
      }

    loop())

let _ = Server.listener "127.0.0.1" 8888 actorSystem


let logFailure<'a>  mb = 
    logWarningf mb"failed: %A" >> fun _ ->  Option<'a>.None 

[<Struct>]
type SocketState = {
    Ks : UniqueKillSwitch option
    Sink : Sink<ByteString,NotUsed>
    Source : IRunnableGraph<Source<ByteString,NotUsed>>
}

let socketState (mb: Actor<_>)  = 
        // let ks = KillSwitches.Shared("ks")
        
        
        let (rxSink: Sink<ByteString,NotUsed>), rxSource =
        
            MergeHub.Source()
            // |> Source.via  (ks.Flow())
            |> Source.toMat (BroadcastHub.Sink()) Keep.both
            |> Graph.run mat 
        let s1,s2 = 
          (Source.actorRef OverflowStrategy.Fail 10 
          |> Source.recover (logFailure mb)
          |> Source.log "tx"
          |> Source.map  (fun (x: string) -> ByteString.FromString($"{x}\n"))).PreMaterialize(mat)
          |> fun struct(r1,r2) -> 
             r1, r2 |> Source.toMat (BroadcastHub.Sink()) Keep.right
        rxSource
        |> Source.recover (logFailure mb)
        |> Source.map string
        |> Source.log "rx"
        |> Source.map(fun _ -> 
            match Random.Shared.Next(0, 255) with 
            // | x when x < 10 -> "stop"
            // | x when x < 20 -> "abort" 
            | x -> string x 
            )
        |> Source.toSink (Sink.toActorRef "stop\n" s1)
        |> Graph.run mat 
        |> ignore 
        
        {
            Ks = None
            Sink = rxSink
            Source = s2
        }

let instance = 
    (fun (mb: Actor<_>) ->

    let rec loop (state: SocketState) = actor {
        let! (msg: obj) = mb.Receive()
        logDebugf mb "received: %A" msg
        match msg with 
        | :? Akka.Streams.Dsl.Tcp.OutgoingConnection as c -> 
            logInfof mb "connected to %A" c
            return! loop state
        | :? Commands as c -> 
            match c with
            | ConnectTo ep ->
                return! 
                  Flow.ofSinkAndSource state.Sink (state.Source.Run(mb.UntypedContext.Materializer()))
                  |> Flow.recover (logFailure  mb)
                  |> Flow.viaMat KillSwitch.single Keep.right
                  |> Flow.join (
                      mb.System.TcpStream().OutgoingConnection(ep)
                      |> Flow.recover (logFailure mb))
                  |> Graph.run(mb.UntypedContext.Materializer())
                  |> fun ks -> { state with Ks = Some ks }
                  |> loop
                  
            | StopConnections -> 
                state.Ks |> Option.iter (fun ks -> ks.Shutdown())
                return! loop { state with Ks = None }
        | x -> 
            logWarningf mb "unhandled message: %A" x
            return! loop state
    }
    mb
    |> socketState
    |> loop )
    |> props 
    |> spawnAnonymous actorSystem

for i in 1 .. 10 do
    instance <! ConnectTo (IPEndPoint(IPAddress.Loopback, 8888))

    Async.Sleep 5000 |> Async.RunSynchronously

    instance <! StopConnections
    Async.Sleep 1000 |> Async.RunSynchronously



while run do 
    Async.Sleep 1 |> Async.RunSynchronously
    ()

