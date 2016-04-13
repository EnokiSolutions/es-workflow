using System;
using System.Reflection;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace Es.Dpo
{
    public sealed class NtService : ServiceBase
    {
        private static string _id;
        private static string _host;
        private static string _dir;
        private CancellationTokenSource _cts;
        private DpoServer _dpoServer;
        private Task _dpoTask;
        private static string _key;

        public static void Main()
        {
            _id = $"{Assembly.GetExecutingAssembly().GetName().Name} {BuildInfo.Version}";

            _host = Environment.GetEnvironmentVariable("DPO_HOST") ?? "dpo.es";
            _dir = Environment.GetEnvironmentVariable("DPO_DIR") ?? ".";
            _key = Environment.GetEnvironmentVariable("DPO_KEY") ?? "ASDF";

            // More than one user service may run in the same process. To add
            // another service to this process, change the following line to
            // create a second service object. For example,
            //
            //   ServicesToRun = New System.ServiceProcess.ServiceBase[] {new Service1(), new MySecondUserService()};
            //

            Run(new ServiceBase[] {new NtService()});
        }

        protected override void OnStart(string[] args)
        {
            EventLog.WriteEntry($"{_id} service started.");
            _cts = new CancellationTokenSource();
            _dpoServer = new DpoServer(
                _host,
                _dir,
                _key,
                s =>
                {
                    EventLog.WriteEntry($"{_id} {s}");
                    Environment.Exit(-1);
                },
                s =>
                {
                    EventLog.WriteEntry($"{_id} {s}");
                }
                );
            _dpoTask = _dpoServer.Run(_cts.Token);
        }

        /// <summary>
        ///     Stop this service.
        /// </summary>
        protected override void OnStop()
        {
            _cts.Cancel();
            _dpoServer.Stop();
            _dpoTask.Wait();

            EventLog.WriteEntry($"{_id} service stopped.");
        }
    }
}