using System;
using System.CodeDom;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Es.ToolsCommon;
using Newtonsoft.Json.Linq;

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
            var dnsProxyServer = new DnsProxyServer(
                dir,
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
            var dnsProxyServerTask = dnsProxyServer.Run(cts.Token);
            Console.WriteLine("Press Q + ENTER to quit");
            while (Console.Read() != 'q') {}

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

    internal sealed class DnsProxyServer
    {
        sealed class CacheEntry
        {
            public readonly ulong Hash;
            public DateTime LastUpdated;
            public byte[] Response;
            public List<Tuple<ushort, EndPoint>> Requesters = new List<Tuple<ushort, EndPoint>>();

            public CacheEntry(ulong hash)
            {
                Hash = hash;
                LastUpdated = DateTime.Now;
                Response = null;
            }
        }

        private readonly Action<string> _onError;
        private readonly Action<string> _onLog;
        private const int NumTasks = 8;
        private const int DnsPort = 53;
        private readonly Task[] _tasks = new Task[NumTasks];
        private readonly Dictionary<ulong,CacheEntry> _cacheEntries = new Dictionary<ulong,CacheEntry>();
        private readonly Dictionary<ushort, ulong> _outstandingQueries = new Dictionary<ushort, ulong>();

        private readonly IPEndPoint _primaryDnsAddress;
        private readonly TimeSpan _cacheTimeout = TimeSpan.FromMinutes(5);

        public DnsProxyServer(string dir, Action<object> onError, Action<object> onLog)
        {
            var config = JObject.Parse(File.ReadAllText(Path.Combine(dir, "dnsproxy.json")));
            _primaryDnsAddress = new IPEndPoint(IPAddress.Parse(config.GetValue("servers").First.ToObject<string>()), DnsPort);
            _onError = onError;
            _onLog = onLog;
        }

        public void DumpBuffer(byte[] buffer, int n)
        {
            const int perLine = 16;
            for (var i = 0; i < n; ++i)
            {
                var t = i + perLine > n ? n - i : perLine;
                _onLog(string.Join(" ", buffer.Skip(i).Take(t).Select(b=>b.ToString("X2"))));
                i += perLine;
            }
        }

        public async Task Run(CancellationToken token)
        {
            try
            {
                Func<Task<int>> nop = async () =>
                {
                    await Task.Yield();
                    return 0;
                };

                var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.IP);
                s.Bind(new IPEndPoint(IPAddress.Any, DnsPort));

                var buffer = new byte[512];

                while (!token.IsCancellationRequested)
                {
                    var rf = await ReceiveFrom(s, buffer);
                    var n = rf.Item1;
                    var remoteEp = rf.Item2;

                    _onLog($"RecvFrom {remoteEp}");
                    DumpBuffer(buffer, n);

                    if (remoteEp.Equals(_primaryDnsAddress))
                    {
                        // reply from primary dns server
                        var originalQueryId = (ushort)((buffer[0] << 8) | buffer[1]);

                        ulong hash;
                        if (!_outstandingQueries.TryGetValue(originalQueryId, out hash))
                            continue;

                        _outstandingQueries.Remove(originalQueryId);

                        CacheEntry ce;
                        if (!_cacheEntries.TryGetValue(hash, out ce))
                            continue;

                        foreach (var r in ce.Requesters)
                        {
                            var queryId = r.Item1;
                            var requestedEp = r.Item2;

                            buffer[0] = (byte) (queryId >> 8);
                            buffer[1] = (byte) queryId;

                            _onLog($"SendTo {requestedEp}");
                            DumpBuffer(buffer, n);

                            await SendTo(s, buffer, n, requestedEp);
                        }
                        ce.Requesters.Clear();
                            
                        ce.Response = buffer.Take(n).ToArray();
                    }
                    else
                    {
                        // request from a client

                        // skip qID for hash
                        var hash = buffer.Hash(2, n - 2);
                        //_onLog($"{n} {hash}");

                        CacheEntry ce;
                        if (_cacheEntries.TryGetValue(hash, out ce))
                        {
                            if (ce.Response != null)
                            {
                                // copy over qId
                                ce.Response[0] = buffer[0];
                                ce.Response[1] = buffer[1];

                                _onLog($"SendTo {remoteEp}");
                                DumpBuffer(ce.Response, ce.Response.Length);

                                await SendTo(s, ce.Response, ce.Response.Length, remoteEp);

                                var now = DateTime.Now;

                                if (now.Subtract(ce.LastUpdated) < _cacheTimeout)
                                    continue;
                            }
                        }

                        var queryId = (ushort) hash;
                        _outstandingQueries[queryId] = hash;
                        var originalQueryId = (ushort)((buffer[0] << 8) | buffer[1]);
                        var cacheEntry = new CacheEntry(hash);
                        cacheEntry.Requesters.Add(Tuple.Create(originalQueryId,remoteEp));
                        _cacheEntries[hash] = cacheEntry;

                        // try to resolve it
                        buffer[0] = (byte) (queryId >> 8);
                        buffer[1] = (byte) (queryId & 0xff);

                        _onLog($"SendTo {_primaryDnsAddress}");
                        DumpBuffer(buffer, n);
                        await SendTo(s, buffer, n, _primaryDnsAddress);
                    }
                }
            }
            catch (Exception ex)
            {
                _onError(ex.ToString());
            }
        }

        private static async Task SendTo(Socket s, byte[] buffer, int length, EndPoint remoteEp)
        {
            var tcs = new TaskCompletionSource<int>(s);
            s.BeginSendTo(buffer, 0, length, SocketFlags.None, remoteEp,
                iar =>
                {
                    tcs.SetResult(s.EndSendTo(iar)); 
                },
                tcs);
            await tcs.Task;
        }

        private static async Task<Tuple<int,EndPoint>> ReceiveFrom(Socket s, byte[] buffer)
        {
            EndPoint remoteEP = new IPEndPoint(IPAddress.Any, IPEndPoint.MinPort);
            var tcs = new TaskCompletionSource<int>(s);

            s.BeginReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref remoteEP,
                iar => { tcs.SetResult(s.EndReceiveFrom(iar, ref remoteEP)); }, tcs);

            var n = await tcs.Task;
            return Tuple.Create(n,remoteEP);
        }
    }
}