using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace Es.Dpo
{
    public static class Program
    {
        internal static string _id;

        public static void Main(string[] args)
        {
            _id = Assembly.GetExecutingAssembly().GetName().Name;
            Console.WriteLine("{0} {1}", _id, BuildInfo.Version);
            var host = Environment.GetEnvironmentVariable("DPO_HOST") ?? "dpo.es";
            var dir = Environment.GetEnvironmentVariable("DPO_DIR") ?? "./dpo";
            var key = Environment.GetEnvironmentVariable("DPO_KEY") ?? "ASDF";

            // dpo server is set in DPO_HOST="dpo.es" by default.
            // 
            // upload package: curl -T package-version.zip "http://${DPO_HOST}/dpo/package-version.zip?key=${DPO_KEY}
            //    
            // download package: curl -O "http://${DPO_HOST}/dpo/package-version.zip"
            //
            // search: curl="http://${DPO_HOST}/dpo?q=X,Y,Z,..."
            //    X,Y,Z can be tags or package names.
            //    returns all matching package-version + metadata

            Console.WriteLine($"{_id} server started.");
            var cts = new CancellationTokenSource();
            var dpoServer = new DpoServer(
                host,
                dir,
                key,
                s =>
                {
                    Console.WriteLine($"FATAL ERROR: {_id} {s}");
                    Environment.Exit(-1);
                },
                s =>
                {
                    Console.WriteLine($"{_id} {s}");
                }
                );
            var dpoTask = dpoServer.Run(cts.Token);
            Console.WriteLine("Press Q + ENTER to quit");
            while (Console.Read() != 'q') {}

            cts.Cancel();
            dpoServer.Stop();
            try
            {
                dpoTask.Wait();
            }
            catch
            {
                // ingored.
            }

            Console.WriteLine($"{_id} service stopped.");
        }
    }
}