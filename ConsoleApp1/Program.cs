using System.Net;
using System.Text;
using Akka;
using Akka.Actor;
using Akka.Configuration;
using Akka.Streams;
using Akka.IO;
using Akka.Streams.Dsl;
using Akka.Util;
using Tcp = Akka.Streams.Dsl.Tcp;

namespace ConsoleApp1
{
    public static class Program
    {
        static bool run = true;
        static Config config = Akka.Configuration.ConfigurationFactory.ParseString(
        """
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
        """);
        static readonly ActorSystem actorSystem = ActorSystem.Create("my-system", config);
        static void Main(string[] args)
        {
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                Console.WriteLine("Ctrl+C pressed - exiting...");
                eventArgs.Cancel = true;
                actorSystem.Dispose();
                run = false;
            };
            Console.WriteLine("Press Ctrl+C to exit...");


            var module = actorSystem.ActorOf(SocketActor.Props);


            module.Tell(new ConnectTo(new IPEndPoint(IPAddress.Loopback, 8888)));

            Task.Delay(1000).Wait();

            module.Tell(new StopConnections());


            do
            {
                Thread.Sleep(1);
            } while (run);

        }
        public record ConnectTo(IPEndPoint endpoint);
        public record StopConnections();


        public class SocketActor : UntypedActor
        {
            Sink<ByteString, NotUsed> SharedSink;
            Source<ByteString, NotUsed> SharedSource;
            SharedKillSwitch ks;
            public SocketActor()
            {
                var rnd = new Random();
                var mat = Context.Materializer();
                ks = KillSwitches.Shared("ks");
                var rxTx =
                    Flow.Create<ByteString>()
                        .Select(x => x.ToString())
                        .Log("rx")
                        .Select(x => ByteString.FromString($"{rnd.Next(0, 255)}\n"))
                        .Via(ks.Flow<ByteString>())
                        .Recover(logFailure)
                        .Log("tx");
                ;
                var (rxSink, rxSource) = MergeHub
                    .Source<ByteString>(perProducerBufferSize: 16)
                    .Via(ks.Flow<ByteString>())
                    .ToMaterialized(BroadcastHub.Sink<ByteString>(bufferSize: 256), Keep.Both)
                    .Run(mat);
                var (txSink, txSource) = MergeHub
                    .Source<ByteString>(perProducerBufferSize: 16)
                    .Via(ks.Flow<ByteString>())
                    .ToMaterialized(BroadcastHub.Sink<ByteString>(bufferSize: 256), Keep.Both)
                    .Run(mat);
                rxSource.Recover(logFailure).Via(rxTx).To(txSink).Run(mat);
                SharedSink = rxSink;
                SharedSource = txSource;


                Source<Tcp.IncomingConnection, Task<Tcp.ServerBinding>> connections =
                    Context.System.TcpStream().Bind("127.0.0.1", 8888);

                connections.RunForeach(connection =>
                {
                    Console.WriteLine($"New connection from: {connection.RemoteAddress}");

                    var echo = Flow.Create<ByteString>()
                        .Via(Framing.Delimiter(
                            ByteString.FromString("\n"),
                            maximumFrameLength: 256,
                            allowTruncation: true))
                        .Select(c => c.ToString())
                        .Select(c =>
                            {
                                var x = Int32.Parse(c);
                                if (x < 100)
                                {
                                    return (x - 1).ToString();
                                }
                                else
                                {
                                    throw new ArgumentOutOfRangeException();
                                }
                            })
                        .Merge(Source.Single("HI!\n"))
                        .Select(ByteString.FromString);

                    connection.HandleWith(echo, mat);
                }, mat);
            }
            static Option<ByteString> logFailure(Exception ex)
            {

                Console.WriteLine($"Error: {ex}");
                return Option<ByteString>.None;
            }
            protected override void OnReceive(object message)
            {
                switch (message)
                {
                    case StopConnections:
                        ks.Shutdown();
                        break;
                    case ConnectTo msg:
                        Context.System.TcpStream()
                            .OutgoingConnection(msg.endpoint)
                            .Recover(logFailure)
                            .Via(ks.Flow<ByteString>())
                            .Join(Flow.FromSinkAndSource(SharedSink, SharedSource).Recover(logFailure))
                            .Run(Context.Materializer());
                        break;
                }
            }

            public static Props Props => Props.Create<SocketActor>();
        }

    }
}