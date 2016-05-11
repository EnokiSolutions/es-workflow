using System;
using System.Reflection;
using System.Threading;

namespace Es.Upd
{
    public static class Program
    {
        internal static string _id;

        public static void Main(string[] args)
        {
            // internal:
            //  curl http://upd.es/upd/install?key=key&name=name&version=version[&label=label]"
            //    e.g. curl http://upd.es/upd/install?key=ASDF&install=MyServer&version=v1.0.2-f1p1-2" // no label
            //    e.g. curl http://upd.es/upd/install?key=ASDF&install=MyServer&version=v1.0.2-p1&label=lime" // one label
            //    e.g. curl http://upd.es/upd/install?key=ASDF&install=MyServer&version=v1.0.2-p1&label=lime,p1.lime" // two labels
            //  curl http://upd.es/upd/enable?key=key&name=name&version=version" // turn on
            //  curl http://upd.es/upd/disable?key=key&nane=name&version=version" // turn off
            //  curl http://upd.es/upd/default?key=key&name=name&version=version" // assign as default (for locate w/o label)
            //  curl http://upd.es/upd/addlabel?key=key&name=name&verison=version&label=label" // set label to point to this version
            //  curl http://upd.es/upd/rmlabel?key=key&name=name&label=label" // remove label
            //  curl http://upd.es/upd/ls?key=key[&name=name]" // list all or for given name (include # times located bucketed by days, hours?)
            //   -> { all installed packages, enabled|disable, labels pointing to this version ('default' is a special label) }
            //   -> { versions installed for named package, enabled|disable, labels pointing to this version ('default' is a special label) }
            //  curl http://upd.es/upd/cfg?key=key" // dump config info
            //
            // external:
            //  curl "http://upd.es/upd/locate?name=name"
            //  curl "http://upd.es/upd/locate?name=name&label=label" // only one label can be given here.
            //   -> {
            //        "loc": "http://vz...host/name/",
            //        "sx": signed(expiry in seconds from epoch)
            //      }
            //
            // restrictions
            //  - no labels starting with _ allowed.
            //  - labels and version restricted to /^((?:_v_)?[0-9a-zA-Z\._-]+|_default_)$/


            _id = Assembly.GetExecutingAssembly().GetName().Name;
            Console.WriteLine("{0} {1}", _id, BuildInfo.Version);
            var host = Environment.GetEnvironmentVariable("UPD_HOST") ?? "upd.es";
            var dpohost = Environment.GetEnvironmentVariable("DPO_HOST") ?? "dpo.es";
            var dir = Environment.GetEnvironmentVariable("UPD_DIR") ?? "./upd";
            var key = Environment.GetEnvironmentVariable("UPD_KEY") ?? "ASDF";

            Console.WriteLine($"{_id} server started.");
            var cts = new CancellationTokenSource();
            var updServer = new UpdServer(
                host,
                dir,
                key,
                dpohost,
                s =>
                {
                    Console.WriteLine($"FATAL ERROR: {_id} {s}");
                    Environment.Exit(-1);
                }, s =>
                {
                    Console.WriteLine($"{_id} {s}");
                });

            var updTask = updServer.Run(cts.Token);
            Console.WriteLine("Press Q + ENTER to quit");
            while (Console.Read() != 'q') { }

            cts.Cancel();
            updServer.Stop();
            try
            {
                updTask.Wait();
            }
            catch
            {
                // ingored.
            }

            Console.WriteLine($"{_id} service stopped.");
        }
    }
}