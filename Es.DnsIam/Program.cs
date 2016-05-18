using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace Es.DnsIam
{
    public static class Program
    {
        private const int DnsIamPort = 453;

        public static void Main(string[] args)
        {
            var id = Assembly.GetExecutingAssembly().GetName().Name;
            if (args.Length < 2)
            {
                Console.WriteLine($"{id} {BuildInfo.Version}");
                Console.WriteLine($"usage: {id} dnsproxy hostname [ip]");
                Environment.Exit(-1);
            }
            
            var bcep = new IPEndPoint(Dns.GetHostAddresses(args[0]).First(), DnsIamPort);
            byte cmd = 0;
            byte[] ip = null;
            if (args.Length == 3)
            {
                ip = IPAddress.Parse(args[2]).GetAddressBytes();
                cmd = 1;
            }

            var sIam = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.IP) { Blocking = true, SendTimeout = 60, ReceiveTimeout = 60, DontFragment = true, MulticastLoopback = false};

            var buffer = new byte[512];
            var hostnameBytes = Encoding.ASCII.GetBytes(args[1]);
            if (hostnameBytes.Length > 255)
            {
                Console.WriteLine("hostname too long");
                Environment.Exit(-2);
            }

            var n = 3 + hostnameBytes.Length;

            buffer[0] = 0;
            buffer[1] = cmd;
            buffer[2] = (byte)hostnameBytes.Length;
            Buffer.BlockCopy(hostnameBytes,0,buffer,3,hostnameBytes.Length);
            if (cmd == 1)
            {
                buffer[n++] = ip[0];
                buffer[n++] = ip[1];
                buffer[n++] = ip[2];
                buffer[n++] = ip[3];
            }

            var sn = sIam.SendTo(buffer, 0, n, SocketFlags.None, bcep);
            if (sn != n)
            {
                Console.WriteLine("SendTo didn't work. :(");
                Environment.Exit(-3);
            }
            var endPoint = new IPEndPoint(IPAddress.Any, DnsIamPort) as EndPoint;
            var rn = sIam.ReceiveFrom(buffer, SocketFlags.None, ref endPoint);
            if (rn <= 0)
            {
                Console.WriteLine("ReceiveFrom didn't work. :(");
                Environment.Exit(-4);
            }
        }
    }

}