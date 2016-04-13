using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Es.ToolsCommon;

namespace Es.Dpo
{
    public static class Program
    {
        private static string _id;

        public static void Main(string[] args)
        {
            _id = Assembly.GetExecutingAssembly().GetName().Name;
            Console.WriteLine("{0} {1}", _id, BuildInfo.Version);
            var host = Environment.GetEnvironmentVariable("DPO_HOST") ?? "dpo.rhi";
            var dir = Environment.GetEnvironmentVariable("DPO_DIR") ?? ".";
            // dpo server is set in DPO_HOST="dpo.es" by default.
            // es.dpo push key package-version.zip
            //    push package to dpo server
            // es.dpo query package
            //    query package versions from dpo.es server
            // es.dpo search X
            //    search for X on dpo.es server, X can be tags or package names.
            // es.dpo server key
            //    run as a server using "key" for pushes
            // es.dpo can also be run as an NT service.

            if (args[0] == "server")
            {
                Console.WriteLine($"{_id} service started.");
                var cts = new CancellationTokenSource();
                var dpoServer = new DpoServer(
                    host,
                    dir,
                    s =>
                    {
                        Console.WriteLine($"{_id} {s}");
                        Environment.Exit(-1);
                    }
                    );
                var dpoTask = dpoServer.Run(cts.Token);
                Console.WriteLine("Press Q to quit");
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

    public sealed class NtService : ServiceBase
    {
        private static string _id;
        private static string _host;
        private static string _dir;
        private CancellationTokenSource _cts;
        private DpoServer _dpoServer;
        private Task _dpoTask;

        public static void Main()
        {
            _id = $"{Assembly.GetExecutingAssembly().GetName().Name} {BuildInfo.Version}";

            _host = Environment.GetEnvironmentVariable("DPO_HOST") ?? "dpo.es";
            _dir = Environment.GetEnvironmentVariable("DPO_DIR") ?? ".";

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
                s =>
                {
                    EventLog.WriteEntry($"{_id} {s}");
                    Environment.Exit(-1);
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

    internal sealed class DpoServer
    {
        private readonly string _host;
        private readonly int _numConcurrent = 64;
        private readonly Action<string> _onError;
        private readonly TaskCompletionSource<int> _tcs = new TaskCompletionSource<int>();

        private CancellationTokenSource _cts;
        private readonly string _dir;

        public DpoServer(string host, string dir, Action<string> onError)
        {
            _host = host;
            _dir = dir;
            _onError = onError;
        }

        public async Task Run(CancellationToken cancellationToken)
        {
            _cts = new CancellationTokenSource();
            // ReSharper disable AccessToDisposedClosure
            cancellationToken.Register(() => { _cts?.Cancel(); });
            // ReSharper restore AccessToDisposedClosure
            _cts.Token.Register(() => _tcs.SetResult(0));

            using (var hl = new HttpListener())
            {
                //_tasks = new Task[_numConcurrent];

                try
                {
                    hl.Prefixes.Add($"http://{_host}/dpo/");
                    hl.Start();
                }
                catch (Exception ex)
                {
                    _onError(ex.ToString());
                }

                for (var i = 0; i < _numConcurrent; ++i)
                    Start(hl);

                await _tcs.Task;

                hl.Abort();
                hl.Stop();

                // Because GetContextAsync doesn't take a cancellation token, or check for abort/stop
                // conditions, our tasks are stuck and your only option is to orphan them. Thanks Microsoft!
            }
            _cts.Dispose();
            _cts = null;
        }

        private void Start(HttpListener hl)
        {
            Process(hl)
                .ContinueWith(
                    t =>
                    {
                        if (t.IsCompleted && !t.IsCanceled)
                        {
                            Start(hl);
                        }
                    });
        }

        private async Task DoRequest(HttpListenerContext httpListenerContext)
        {
            httpListenerContext.Response.ContentType = "text/plain";

            var req = httpListenerContext.Request;
            if (req.HttpMethod == "GET")
            {
                var respData = Encoding.UTF8.GetBytes("Hello");
                await httpListenerContext.Response.OutputStream.WriteAsync(respData, 0, respData.Length, _cts.Token);
                httpListenerContext.Response.Close();
                return;

            }

            if (req.HttpMethod == "DELETE")
            {

            }

            if (req.HttpMethod == "PUT")
            {
                var key = req.Url.PathAndQuery.After("?key=");
                var file = req.Url.PathAndQuery.After("dpo/").Before("?");
                var valid = !(file.Contains("/") || file.Contains("\\")) && file.EndsWith(".zip");
                var path = "invalid";
                var basename = "invalid";
                if (valid)
                {
                    basename = file.Before(".zip").Before("-");
                    var hash = basename.Hash().ToString();
                    path = _dir + "/" + hash.Substring(0, 2) + "/" + hash.Substring(2, 2) + "/" + hash.Substring(4) +
                           "/" + file;
                }
                var respData = Encoding.UTF8.GetBytes($"PUT\nfile: {file}\nvalid: {valid}\nbasename: {basename}\npath: {path}\nkey: {key}");

                await httpListenerContext.Response.OutputStream.WriteAsync(respData, 0, respData.Length, _cts.Token);
                httpListenerContext.Response.Close();
                return;
            }
        }

        private async Task Process(HttpListener hl)
        {
            try
            {
                var getContext = hl.GetContextAsync();
                await getContext;
                if (_cts.IsCancellationRequested || getContext.IsCanceled)
                    return;

                var httpListenerContext = getContext.Result;

                await DoRequest(httpListenerContext);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                _onError(ex.ToString());
            }
        }

        public void Stop()
        {
            using (var wc = new WebClient())
            {
                try
                {
                    // ping the server to cause all the pending GetContentAsyncs to fail so the server actually shuts down
                    wc.DownloadStringTaskAsync(new Uri($"http://{_host}/dpo/ping/"));
                }
                catch
                {
                    // ignored
                }
            }
        }

    }


    public static class UrlEx
    {
        public static string After(this string x, string what)
        {
            var i = x.IndexOf(what, StringComparison.Ordinal);
            return i < 0 ? x : x.Substring(i+what.Length);
        }
        public static string Before(this string x, string what)
        {
            var i = x.IndexOf(what, StringComparison.Ordinal);
            return i < 0 ? x : x.Substring(0,i);
        }
        public static string UrlDecode(this string what)
        {
            var whatChars = what.ToCharArray();
            var chars = new char[whatChars.Length]; // might be smaller

            var o = 0;
            for (var i = 0; i < whatChars.Length; ++i)
            {
                var whatByte = whatChars[i];
                if (whatByte != '%')
                {
                    chars[o++] = whatByte;
                }
                else
                {
                    var highChar = (int) whatChars[++i];
                    var low = (int) whatChars[++i];
                    var highValue = highChar - (highChar < 58 ? 48 : (highChar < 97 ? 55 : 87));
                    var lowValue = low - (low < 58 ? 48 : (low < 97 ? 55 : 87));

                    if (highValue < 0 || highValue > 15 || lowValue < 0 || lowValue > 15)
                        throw new FormatException("urlDecode");

                    chars[o++] = (char) ((highValue << 4) + lowValue);
                }
            }

            return new string(chars);
        }

        public static string UrlEncode(this string what)
        {
            var sb = new StringBuilder();
            foreach (var b in what.ToCharArray())
            {
                sb.Append(
                    b <= ' ' || (b >= '[' && b <= '`') || (b >= 'z')
                        ? "%" + ((ushort) b).ToString("x2")
                        : b.ToString()
                    );
            }
            return sb.ToString();
        }
    }
}