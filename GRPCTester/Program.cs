using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace GRPCTester {
    internal class Program {
        static void Main(string[] args) {
            Console.WriteLine(" gRPC-Tester® (v.1.0 by P - 03.05.2026)");

            var Client = new HttpClient();
            var channel = Grpc.Net.Client.GrpcChannel.ForAddress("http://localhost:50051", new Grpc.Net.Client.GrpcChannelOptions {
                HttpHandler = new SocketsHttpHandler {
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    EnableMultipleHttp2Connections = true,
                    ConnectCallback = async (context, cancellationToken) => {
                        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                        try {
                            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
                            return new NetworkStream(socket, ownsSocket: true);
                        } catch {
                            socket.Dispose();
                            throw;
                        }
                    }
                }
            });
        }
    }
}
