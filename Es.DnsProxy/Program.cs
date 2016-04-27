using System;
using System.Reflection;
using System.Threading;

namespace Es.DnsProxy
{
    public static class Program
    {
        internal static string _id;
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

        public static void Main(string[] args)
        {
            _id = Assembly.GetExecutingAssembly().GetName().Name;
            Console.WriteLine("{0} {1}", _id, BuildInfo.Version);
            var dir = Environment.GetEnvironmentVariable("DNSPROXY_DIR") ?? ".";

            Console.WriteLine($"{_id} server started.");
            var cts = new CancellationTokenSource();
            var dnsProxyServer = new DnsProxyServer(
                dir,
                s =>
                {
                    Console.WriteLine($"FATAL ERROR: {_id} {s}");
                    Environment.Exit(-1);
                },
                s => { Console.WriteLine($"{_id} {s}"); }
                );
            var dnsProxyServerTask = dnsProxyServer.Run(cts.Token);
            Console.WriteLine("Press Q + ENTER to quit");
            while (Console.Read() != 'q')
            {
            }

            cts.Cancel();

            try
            {
                dnsProxyServerTask.Wait(ShutdownTimeout);
            }
            catch
            {
                // ingored.
            }

            Console.WriteLine($"{_id} service stopped.");
        }
    }
}