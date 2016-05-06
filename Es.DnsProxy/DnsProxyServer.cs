using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Es.ToolsCommon;
using Newtonsoft.Json.Linq;

namespace Es.DnsProxy
{
    internal sealed class DnsProxyServer
    {
        private const int NumTasks = 8;
        private const int DnsPort = 53;
        private static readonly char[] DotSeparator = {'.'};
        private readonly Dictionary<ulong, CacheEntry> _cacheEntries = new Dictionary<ulong, CacheEntry>();
        private readonly TimeSpan _cacheTimeout = TimeSpan.FromMinutes(5);
        private readonly List<HostOverride> _hostOverrides = new List<HostOverride>();

        private readonly Action<string> _onError;
        private readonly Action<string> _onLog;
        private readonly Dictionary<ushort, ulong> _outstandingQueries = new Dictionary<ushort, ulong>();

        private readonly IPEndPoint _primaryDnsAddress;

        private static readonly DateTime EndOfTime = DateTime.MaxValue.Subtract(TimeSpan.FromDays(1));

        public DnsProxyServer(string dir, Action<object> onError, Action<object> onLog)
        {
            _onError = onError;
            _onLog = onLog;

            var configFilename = Path.Combine(dir, "dnsproxy.json");
            if (!File.Exists(configFilename))
            {
                _onError($"No config file at {configFilename}");
                return;
            }

            var config = JObject.Parse(File.ReadAllText(configFilename));

            var hostMaps = config.GetValue("hosts").ToObject<string[][]>();
            if (hostMaps != null)
            {
                SetupHostsEntries(hostMaps);
            }

            _primaryDnsAddress = new IPEndPoint(IPAddress.Parse(config.GetValue("servers").First.ToObject<string>()),
                DnsPort);
        }

        private void SetupHostsEntries(string[][] hostMaps)
        {
            var n = 0;
            var buffer = new byte[512];

            foreach (var hostMap in hostMaps)
            {
                if (hostMap.Length < 2)
                {
                    _onError?.Invoke($"didn't understand hosts entry ${string.Join(",", hostMap)}");
                    continue;
                }
                // for now only support 1 IP override.
                var hostname = hostMap[0];
                var hostip = hostMap[1];

                var ho = new HostOverride();

                var ip = IPAddress.Parse(hostip);
                var hostBits = hostname.Split(DotSeparator, StringSplitOptions.RemoveEmptyEntries);
                n = 0;
                foreach (var bit in hostBits)
                {
                    buffer[n++] = (byte) bit.Length;
                    var utf8Bytes = Encoding.UTF8.GetBytes(bit);
                    foreach (var b in utf8Bytes)
                    {
                        buffer[n++] = b;
                    }
                }
                ho.hostPostfix = buffer.Take(n).ToArray();
                ho.ipAddr = ip.GetAddressBytes();

                _hostOverrides.Add(ho);

                _onLog?.Invoke(
                    $"hostmap for {hostname} {hostip}\n\t{string.Join(" ", ho.hostPostfix.Select(b => b.ToString("X2")))}\n\t{string.Join(" ", ho.ipAddr.Select(b => b.ToString("X2")))}");
            }
        }

        public void DumpBuffer(byte[] buffer, int n)
        {
            const int perLine = 16;
            for (var i = 0; i < n; i += perLine)
            {
                var t = i + perLine > n ? n - i : perLine;
                _onLog?.Invoke(string.Join(" ", buffer.Skip(i).Take(t).Select(b => b.ToString("X2"))));
            }
        }

        public async Task Run(CancellationToken token)
        {
            try
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.IP);
                IgnoreConnReset(s);
                s.Bind(new IPEndPoint(IPAddress.Any, DnsPort));

                var buffer = new byte[512];

                while (!token.IsCancellationRequested)
                {
                    var rf = await ReceiveFrom(s, buffer);

                    if (rf==null)
                        continue;

                    var n = rf.N;
                    var remoteEp = rf.RemoteEp;

                    _onLog($"RecvFrom {remoteEp}");
                    DumpBuffer(buffer, n);

                    if (remoteEp.Equals(_primaryDnsAddress))
                    {
                        await HandleReplyFromPrimaryDnsServer(buffer, n, s);

                        _onLog($"# outstanding {_cacheEntries.Sum(x=>x.Value.Requests.Count)}");
                    }
                    else
                    {
                        await HandleRequestFromClient(buffer, n, remoteEp, s);
                    }
                }
            }
            catch (Exception ex)
            {
                _onError(ex.ToString());
            }
        }

        private static void IgnoreConnReset(Socket s)
        {
            const uint IOC_IN = 0x80000000;
            const uint IOC_VENDOR = 0x18000000;
            const uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;
            unchecked
            {
                s.IOControl((int) SIO_UDP_CONNRESET, new byte[] {0}, null);
            }
        }

        private async Task HandleRequestFromClient(byte[] buffer, int n, EndPoint remoteEp, Socket s)
        {
            // request from a client

            // skip qID for hash
            var hash = buffer.Hash(2, n - 2);
            //_onLog($"{n} {hash}");

            CacheEntry ce;
            _cacheEntries.TryGetValue(hash, out ce);

            if (ce == null)
            {
                ce = HandleOverrides(buffer, n, hash);
                if (ce != null)
                {
                    _cacheEntries[hash] = ce;
                }
            }

            var repliedAlready = false;
            if (ce?.Response != null)
            {
                // copy over qId
                ce.Response[0] = buffer[0];
                ce.Response[1] = buffer[1];

                _onLog($"CACHED SendTo {remoteEp}");
                DumpBuffer(ce.Response, ce.Response.Length);

                await SendTo(s, ce.Response, ce.Response.Length, remoteEp);

                // send again
                await Task.Delay(1);
                await SendTo(s, ce.Response, ce.Response.Length, remoteEp);

                repliedAlready = true;
                var now = DateTime.Now;

                var timeSpan = now.Subtract(ce.LastUpdated);
                if (timeSpan < _cacheTimeout)
                {
                    _onLog($"Skipped relookup, cache entry only {timeSpan.TotalSeconds}s old");
                    return;
                }
            }

            if (ce == null)
            {
                ce = new CacheEntry(hash);
            }

            var queryId = (ushort)hash;

            if (!repliedAlready)
            {
                _outstandingQueries[queryId] = hash;
                var originalQueryId = (ushort)((buffer[0] << 8) | buffer[1]);

                ce.Requests.Add(new Request(originalQueryId, remoteEp));
                _cacheEntries[hash] = ce;
            }

            // try to resolve it
            buffer[0] = (byte) (queryId >> 8);
            buffer[1] = (byte) (queryId & 0xff);

            _onLog($"SendTo {_primaryDnsAddress}");
            DumpBuffer(buffer, n);
            await SendTo(s, buffer, n, _primaryDnsAddress);
        }

        private CacheEntry HandleOverrides(byte[] buffer, int n, ulong hash)
        {
            if (!(buffer[2] == 1
                  && buffer[3] == 0
                  && buffer[4] == 0
                  && buffer[5] == 1
                  && buffer[n - 1] == 1
                  && buffer[n - 2] == 0
                  && buffer[n - 3] == 1
                  && buffer[n - 4] == 0
                )) // single question, normal flags, A, IN
            {
                return null;
            }

            var maxLen = n - 16; // offset 12 to n-4
            DumpSingleARequest(buffer, n);

            foreach (var ho in _hostOverrides)
            {
                if (ho.hostPostfix.Length > maxLen)
                    continue;

                var nk = n - 5 - ho.hostPostfix.Length;
                if (!ho.hostPostfix.All(t => buffer[nk++] == t))
                    continue;

                // postfix matches, create cache entry with pre-populated response.

                var ce = new CacheEntry(hash, EndOfTime); // never expire

                buffer[2] = 0x81; // response
                buffer[3] = 0x80; // rescursion abailable
                buffer[7] = 0x01; // one reply
                buffer[n++] = 0xc0; // ref
                buffer[n++] = 0x0c; // offset 12
                buffer[n++] = 0; // A
                buffer[n++] = 1; // A
                buffer[n++] = 0; // IN
                buffer[n++] = 1; // IN
                buffer[n++] = 0; // ttl
                buffer[n++] = 0; // ttl
                buffer[n++] = 0x0c; // ttl
                buffer[n++] = 0; // ttl
                buffer[n++] = 0; // count
                buffer[n++] = 4; // 4 bytes to follow
                buffer[n++] = ho.ipAddr[0];
                buffer[n++] = ho.ipAddr[1];
                buffer[n++] = ho.ipAddr[2];
                buffer[n++] = ho.ipAddr[3];
                ce.Response = buffer.Take(n).ToArray();
                return ce;
            }
            return null;
        }

        private void DumpSingleARequest(byte[] buffer, int n)
        {
            var p = new List<string>();
            for (var j = 11; j < n - 4;)
            {
                var l = (int) buffer[++j];
                if (l == 0)
                    break;
                var s = "";
                for (var k = 0; k < l; ++k)
                {
                    s += (char) buffer[++j];
                }
                p.Add(s);
            }
            var host = string.Join(".", p);
            _onLog($"A {host}");
        }

        private async Task HandleReplyFromPrimaryDnsServer(byte[] buffer, int n, Socket s)
        {
            // reply from primary dns server
            var originalQueryId = (ushort) ((buffer[0] << 8) | buffer[1]);

            ulong hash;
            if (!_outstandingQueries.TryGetValue(originalQueryId, out hash))
                return;

            _outstandingQueries.Remove(originalQueryId);

            CacheEntry ce;
            if (!_cacheEntries.TryGetValue(hash, out ce))
                return;

            DumpSingleARequest(buffer, n);
            foreach (var r in ce.Requests)
            {
                var queryId = r.Qid;
                var requestedEp = r.Asker;
                r.Stopwatch.Stop();

                buffer[0] = (byte) (queryId >> 8);
                buffer[1] = (byte) queryId;

                _onLog($"SendTo {requestedEp} {r.Stopwatch.ElapsedMilliseconds}ms");

                await SendTo(s, buffer, n, requestedEp);
            }
            ce.Requests.Clear();

            ce.Response = buffer.Take(n).ToArray();
        }

        private async Task SendTo(Socket s, byte[] buffer, int length, EndPoint remoteEp)
        {
            try
            {
                var tcs = new TaskCompletionSource<int>(s);
                s.BeginSendTo(buffer, 0, length, SocketFlags.None, remoteEp,
                    iar => { tcs.SetResult(s.EndSendTo(iar)); },
                    tcs);
                await tcs.Task;
            }
            catch (Exception ex)
            {
                _onLog($"{ex}");
            }
        }

        private async Task<RecvResult> ReceiveFrom(Socket s, byte[] buffer)
        {
            try
            {
                EndPoint remoteEp = new IPEndPoint(IPAddress.Any, DnsPort);
                var tcs = new TaskCompletionSource<int>(s);

                s.BeginReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref remoteEp,
                    iar => { tcs.SetResult(s.EndReceiveFrom(iar, ref remoteEp)); }, tcs);

                var n = await tcs.Task;
                return new RecvResult {N = n, RemoteEp = remoteEp};
            }
            catch (Exception ex)
            {
                _onLog($"{ex}");
            }
            return null;
        }

        private sealed class Request
        {
            public readonly ushort Qid;
            public readonly EndPoint Asker;
            public readonly Stopwatch Stopwatch;

            public Request(ushort originalQueryId, EndPoint remoteEp)
            {
                Qid = originalQueryId;
                Asker = remoteEp;
                Stopwatch = Stopwatch.StartNew();
            }
        }

        private sealed class CacheEntry
        {
            public readonly ulong Hash;
            public readonly DateTime LastUpdated;
            public readonly List<Request> Requests = new List<Request>();
            public byte[] Response;

            public CacheEntry(ulong hash)
            {
                Hash = hash;
                LastUpdated = DateTime.Now;
                Response = null;
            }
            public CacheEntry(ulong hash, DateTime lastUpdated)
            {
                Hash = hash;
                LastUpdated = lastUpdated;
                Response = null;
            }
        }

        private sealed class RecvResult
        {
            public int N;
            public EndPoint RemoteEp;
        }

        private sealed class HostOverride
        {
            public byte[] hostPostfix;
            public byte[] ipAddr;
        }
    }
}