using System;
using System.Collections.Generic;
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
        private readonly Task[] _tasks = new Task[NumTasks];

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
            for (var i = 0; i < n; ++i)
            {
                var t = i + perLine > n ? n - i : perLine;
                _onLog?.Invoke(string.Join(" ", buffer.Skip(i).Take(t).Select(b => b.ToString("X2"))));
                i += perLine;
            }
        }

        public async Task Run(CancellationToken token)
        {
            try
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.IP);
                s.Bind(new IPEndPoint(IPAddress.Any, DnsPort));

                var buffer = new byte[512];

                while (!token.IsCancellationRequested)
                {
                    var rf = await ReceiveFrom(s, buffer);
                    var n = rf.N;
                    var remoteEp = rf.RemoteEp;

                    _onLog($"RecvFrom {remoteEp}");
                    DumpBuffer(buffer, n);

                    if (remoteEp.Equals(_primaryDnsAddress))
                    {
                        await HandleReplyFromPrimaryDnsServer(buffer, n, s);
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

            if (ce?.Response != null)
            {
                // copy over qId
                ce.Response[0] = buffer[0];
                ce.Response[1] = buffer[1];

                _onLog($"SendTo {remoteEp}");
                DumpBuffer(ce.Response, ce.Response.Length);

                await SendTo(s, ce.Response, ce.Response.Length, remoteEp);

                var now = DateTime.Now;

                if (now.Subtract(ce.LastUpdated) < _cacheTimeout)
                    return;
            }

            var queryId = (ushort) hash;
            _outstandingQueries[queryId] = hash;
            var originalQueryId = (ushort) ((buffer[0] << 8) | buffer[1]);
            var cacheEntry = new CacheEntry(hash);
            cacheEntry.Requesters.Add(Tuple.Create(originalQueryId, remoteEp));
            _cacheEntries[hash] = cacheEntry;

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
            foreach (var ho in _hostOverrides)
            {
                if (ho.hostPostfix.Length > maxLen)
                    continue;

                var nk = n - 5 - ho.hostPostfix.Length;
                if (!ho.hostPostfix.All(t => buffer[nk++] == t))
                    continue;

                // postfix matches, create cache entry with pre-populated response.

                var ce = new CacheEntry(hash);

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

        private static async Task SendTo(Socket s, byte[] buffer, int length, EndPoint remoteEp)
        {
            var tcs = new TaskCompletionSource<int>(s);
            s.BeginSendTo(buffer, 0, length, SocketFlags.None, remoteEp,
                iar => { tcs.SetResult(s.EndSendTo(iar)); },
                tcs);
            await tcs.Task;
        }

        private static async Task<RecvResult> ReceiveFrom(Socket s, byte[] buffer)
        {
            EndPoint remoteEp = new IPEndPoint(IPAddress.Any, IPEndPoint.MinPort);
            var tcs = new TaskCompletionSource<int>(s);

            s.BeginReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref remoteEp,
                iar => { tcs.SetResult(s.EndReceiveFrom(iar, ref remoteEp)); }, tcs);

            var n = await tcs.Task;
            return new RecvResult {N = n, RemoteEp = remoteEp};
        }

        private sealed class CacheEntry
        {
            public readonly ulong Hash;
            public readonly DateTime LastUpdated;
            public readonly List<Tuple<ushort, EndPoint>> Requesters = new List<Tuple<ushort, EndPoint>>();
            public byte[] Response;

            public CacheEntry(ulong hash)
            {
                Hash = hash;
                LastUpdated = DateTime.Now;
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