using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Es.Dpo;
using Es.ToolsCommon;
using ICSharpCode.SharpZipLib.Zip;
using Newtonsoft.Json.Linq;

namespace Es.Upd
{
    internal sealed class UpdServer
    {
        private readonly string _dir;
        private readonly string _host;
        private readonly string _key;
        private readonly string _dpoHost;

        private readonly ConcurrentDictionary<string, ConcurrentBag<JObject>> _metaDataIndex =
            new ConcurrentDictionary<string, ConcurrentBag<JObject>>(StringComparer.OrdinalIgnoreCase);

        private readonly int _numConcurrent = 64;
        private readonly Action<string> _onError;
        private readonly Action<string> _onLog;
        private readonly TaskCompletionSource<int> _tcs = new TaskCompletionSource<int>();

        private CancellationTokenSource _cts;

        public UpdServer(string host, string dir, string key, string dpoHost, Action<string> onError, Action<string> onLog = null)
        {
            _host = host;
            _dir = dir;
            _key = key;
            _onError = onError;
            _dpoHost = dpoHost;
            _onLog = onLog;
        }

        private static int CurrentPid()
        {
            int pid;
            NativeMethods.GetWindowThreadProcessId(Process.GetCurrentProcess().MainWindowHandle, out pid);
            return pid;
        }

        public async Task Run(CancellationToken cancellationToken)
        {
            // todo:
            //  prime the directory with $UPD_DIR/installs
            //  general structure of installs:
            //     .../installs/name/uuid/...
            //                      /labels.txt (one per line: label:uuid\n)
            //     .../downloads/hh/hh.../name
            //  default is the label '_default_'
            //  versions are prefixed with '_v_'

            _cts = new CancellationTokenSource();
            CreateDirectoryRecursive(_dir);
            File.WriteAllText($"{_dir}/pid", CurrentPid().ToString());
            PruneEmpty(_dir);
            SetupMetaData();

            // ReSharper disable AccessToDisposedClosure
            cancellationToken.Register(() => { _cts?.Cancel(); });
            // ReSharper restore AccessToDisposedClosure
            _cts.Token.Register(() => _tcs.SetResult(0));

            using (var hl = new HttpListener())
            {
                //_tasks = new Task[_numConcurrent];

                try
                {
                    hl.Prefixes.Add($"http://{_host}/upd/");
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

        private void SetupMetaData()
        {
            //foreach (var f in Directory.EnumerateFiles(_dir, "*.exe", SearchOption.AllDirectories))
            //{
            //    _onLog?.Invoke($"{f}");
            //    var pn = Path.GetFileName(Path.GetDirectoryName(f));
            //    if (pn == null)
            //        continue;

            //    var zfn = f.Replace(".meta.json", ".zip");
            //    var fn = Path.GetFileName(zfn);
            //    if (!File.Exists(zfn))
            //        continue;

            //    _onLog?.Invoke($"{pn} {zfn}");
            //    var md = JObject.Parse(File.ReadAllText(f));
            //    md["pak"] = fn;

            //    foreach (var tag in md.GetValue("tags", new string[] {}))
            //    {
            //        _metaDataIndex.GetOrAdd(tag, new ConcurrentBag<JObject>()).Add(md);
            //    }
            //    _metaDataIndex.GetOrAdd(pn, new ConcurrentBag<JObject>()).Add(md);
            //}
        }

        private void Start(HttpListener hl)
        {
            ProcessRequest(hl)
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
            var resp = httpListenerContext.Response;
            var req = httpListenerContext.Request;

            var pathAndQuery = req.Url.PathAndQuery;
            if (req.HttpMethod != "GET")
            {
                var respData = Encoding.UTF8.GetBytes($"{Program._id}");
                await resp.OutputStream.WriteAsync(respData, 0, respData.Length, _cts.Token);
                resp.Close();
                return;
            }

            if (!pathAndQuery.Contains("?") || !pathAndQuery.Contains($"key={_key}"))
            {
                resp.StatusCode = 400;
                resp.Close();
                return;
            }

            var args =
                pathAndQuery.After("?")
                    .Split('&')
                    .Select(x => x.Split('='))
                    .Where(x => x.Length == 2)
                    .ToDictionary(x => x[0], x => x[1]);
            var cmd = pathAndQuery.After("upd/").Before("?").TrimEnd('/');

            switch (cmd)
            {
                case "install":
                    if (!(args.ContainsKey("name") && args.ContainsKey("version")))
                        goto default;
                    await Install(resp, args);
                    break;
                case "enable":
                    if (!(args.ContainsKey("name") && args.ContainsKey("version")))
                        goto default;
                    await Enable(resp, args);
                    break;
                case "disable":
                    if (!(args.ContainsKey("name") && args.ContainsKey("version")))
                        goto default;
                    await Disable(resp, args);
                    break;
                case "default":
                    if (!(args.ContainsKey("name") && args.ContainsKey("version")))
                        goto default;
                    await Default(resp, args);
                    break;
                case "addlabel":
                    if (!(args.ContainsKey("name") && args.ContainsKey("version") && args.ContainsKey("label")))
                        goto default;
                    await AddLabel(resp, args);
                    break;
                case "rmlabel":
                    if (!(args.ContainsKey("name") && args.ContainsKey("label")))
                        goto default;
                    await RmLabel(resp, args);
                    break;
                case "ls":
                    await Ls(resp, args);
                    break;
                case "cfg":
                    await Cfg(resp, args);
                    break;
                case "locate":
                    if (!args.ContainsKey("name"))
                        goto default;
                    await Locate(resp, args);
                    break;
                default:
                    resp.StatusCode = 400;
                    resp.Close();
                    return;
            }
        }

        private async Task Locate(HttpListenerResponse resp, Dictionary<string, string> args)
        {
            await Task.Yield();
            throw new NotImplementedException();
        }

        private async Task Cfg(HttpListenerResponse resp, Dictionary<string, string> args)
        {
            await Task.Yield();
            throw new NotImplementedException();
        }

        private async Task Ls(HttpListenerResponse resp, Dictionary<string, string> args)
        {
            await Task.Yield();
            throw new NotImplementedException();
        }

        private async Task RmLabel(HttpListenerResponse resp, Dictionary<string, string> args)
        {
            await Task.Yield();
            throw new NotImplementedException();
        }

        private async Task AddLabel(HttpListenerResponse resp, Dictionary<string, string> args)
        {
            await Task.Yield();
            throw new NotImplementedException();
        }

        private async Task Default(HttpListenerResponse resp, Dictionary<string, string> args)
        {
            await Task.Yield();
            throw new NotImplementedException();
        }

        private async Task Disable(HttpListenerResponse resp, Dictionary<string, string> args)
        {
            await Task.Yield();
            throw new NotImplementedException();
        }

        private async Task Enable(HttpListenerResponse resp, Dictionary<string, string> args)
        {
            await Task.Yield();
            throw new NotImplementedException();
        }

        private async Task Install(HttpListenerResponse resp, Dictionary<string, string> args)
        {
            // get package from Dpo.es
            var name = args["name"];
            var version = args["version"];
            var filename = $"{name}-{version}.zip";
            var hash = filename.Hash().ToString("x016");
            var path = $"{_dir}/{hash.Substring(0, 2)}/{hash.Substring(2, 2)}/{hash.Substring(4)}";
            var fullFilename = $"{path}/{filename}";
            CreateDirectoryRecursive(path);

            var url = $"http://{_dpoHost}/dpo/{filename}";
            using (var wc = new WebClient())
            {

                await wc.DownloadFileTaskAsync(url, fullFilename);

                try
                {
                    _onLog($"Install {filename}");

                    using (var zf = new ZipFile(fullFilename))
                    {
                        foreach (ZipEntry ze in zf)
                        {
                            var zs = zf.GetInputStream(ze);
                            var fileToWrite = path + "/" + Path.GetFileName(ze.Name);
                            CreateDirectoryRecursive(Path.GetDirectoryName(fileToWrite));
                            using (var ofs = new FileStream(fileToWrite, FileMode.CreateNew))
                            {
                                await zs.CopyToAsync(ofs);
                            }
                            break;
                        }
                    }

                    resp.StatusCode = 200;
                    var respData = Encoding.UTF8.GetBytes("OK");
                    await resp.OutputStream.WriteAsync(respData, 0, respData.Length, _cts.Token);
                    resp.Close();
                }
                catch (Exception ex)
                {
                    _onError($"{ex}");
                    resp.StatusCode = 500;
                    var respData = Encoding.UTF8.GetBytes("SERVER ERROR");

                    await resp.OutputStream.WriteAsync(respData, 0, respData.Length, _cts.Token);
                    resp.Close();
                }
            }
        }

        private void PruneEmpty(string dir)
        {
            _onLog?.Invoke($"Prune: {dir}");
            if (!Directory.Exists(dir))
                return;

            foreach (var d in Directory.GetDirectories(dir))
                PruneEmpty(d);

            TryDeleteDirectory(dir);
        }

        private void TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.GetFileSystemEntries(dir).Length != 0)
                    return;

                Directory.Delete(dir);
                _onLog?.Invoke($"\tDeleted: {dir}");
            }
            catch
            {
                //ignored
            }
        }

        private void CreateDirectoryRecursive(string path)
        {
            var parts = path.Split(Path.DirectorySeparatorChar);
            var sb = new StringBuilder(path.Length);

            foreach (var s in parts)
            {
                sb.Append(s).Append(Path.DirectorySeparatorChar);
                var dir = sb.ToString();
                try
                {
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                }
                catch (Exception)
                {
                    _onError($"Couldn't create directory : {dir}, building path={path} failed on part {s}");
                    throw;
                }
            }
        }

        private async Task ProcessRequest(HttpListener hl)
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

        private static class NativeMethods
        {
            [DllImport("user32")]
            public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);
        }
    }
}