using System;
using System.Reflection;

namespace Es.Upd
{
    public static class Program
    {
        private static string _id;

        public static void Main(string[] args)
        {
            _id = Assembly.GetExecutingAssembly().GetName().Name;
            Console.WriteLine("{0} {1}", _id, BuildInfo.Version);

            // internal:
            //  install
            //  enable
            //  disable
            //  set default
            //
            // external:
            //  locate X (optional: label)
            //   -> ( relative /name/vx.../ or abs http://host/name/vz.../ ) + CDN + signed(expiry in seconds from epoch)
            //   
            //
            //
        }
    }
}