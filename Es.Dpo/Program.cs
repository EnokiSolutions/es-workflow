using System;
using System.Reflection;

namespace Es.Dpo
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            Console.WriteLine("{0} {1}", Assembly.GetExecutingAssembly().GetName().Name, BuildInfo.Version);
        }
    }
}