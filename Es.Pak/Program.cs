using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Es.ToolsCommon;
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.Zip;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Es.Pak
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            var runningExeName = Assembly.GetExecutingAssembly().GetName().Name;
            Console.WriteLine("{0} {1}", runningExeName, BuildInfo.Version);
            if (args.Length < 1)
            {
                Console.WriteLine("{0} <branch>", runningExeName);
                Environment.Exit(-200);
            }
            var version = File.Exists("version.txt") ? File.ReadAllLines("version.txt")[0] : "0.0.0";
            var branch = args[0];
            if (branch != "master")
            {
                version += "-" + branch;
                var bn = Environment.GetEnvironmentVariable("BUILD_NUMBER");
                if (bn != null)
                    version += "-" + bn;
            }
            foreach (var pakFile in Directory.EnumerateFiles(".", "*.pak"))
            {
                var withoutDotSlash = pakFile.Substring(2);
                Pack(version, withoutDotSlash);
            }
        }

        private static void Pack(string version, string pakFile)
        {
            var pakJson = JObject.Parse(File.ReadAllText(pakFile));
            var exeName = pakJson.GetValue("name",string.Empty);
            if (string.IsNullOrEmpty(exeName))
            {
                exeName = Directory.EnumerateFiles(".","*.exe").Select(x=>x.Substring(2)).OrderBy(x => x).FirstOrDefault();
            }
            if (string.IsNullOrEmpty(exeName))
            {
                Console.WriteLine("Can't pack {0}, no exe found", Path.GetFullPath(pakFile));
                Environment.Exit(-1);
            }
            var exeFi = new FileInfo(exeName);
            if (!exeFi.Exists)
            {
                Console.WriteLine("Can't pack {0}, {1} not found", Path.GetFullPath(pakFile), exeName);
                Environment.Exit(-1);
            }

            var baseName = Path.GetFileNameWithoutExtension(pakFile) + "." + version;
            var fso = File.Create(baseName + ".zip");
            var zf = new ZipOutputStream(fso) {IsStreamOwner = true};
            zf.SetLevel(9);
            
            var meta = pakJson.GetValue("meta");
            var metaData = Encoding.UTF8.GetBytes(meta.ToString(Formatting.Indented));
            var mze = new ZipEntry(baseName + "/meta.json")
            {
                Size = metaData.Length,
                DateTime = exeFi.LastWriteTime
            };
            zf.PutNextEntry(mze);
            zf.Write(metaData,0,metaData.Length);
            zf.CloseEntry();

            var eze = new ZipEntry(baseName + "/" + exeName)
            {
                Size = exeFi.Length,
                DateTime = exeFi.LastWriteTime
            };
            zf.PutNextEntry(eze);
            var buffer = new byte[4096];
            using (var streamReader = File.OpenRead(exeName))
            {
                StreamUtils.Copy(streamReader, zf, buffer);
            }
            zf.CloseEntry();

            if (Directory.Exists("dist"))
            {
                AddFolder(baseName, "dist", zf);
            }
            zf.Close();
        }
        private static void AddFolder(string basename, string path, ZipOutputStream zipStream, int folderOffset=0)
        {

            var files = Directory.GetFiles(path);

            foreach (var filename in files)
            {
                var fi = new FileInfo(filename);

                var entryName = filename.Substring(folderOffset);
                entryName = ZipEntry.CleanName(entryName);
                var newEntry = new ZipEntry(basename + "/" + entryName)
                {
                    DateTime = fi.LastWriteTime,
                    Size = fi.Length
                };
                zipStream.PutNextEntry(newEntry);

                var buffer = new byte[4096];
                using (var streamReader = File.OpenRead(filename))
                {
                    StreamUtils.Copy(streamReader, zipStream, buffer);
                }
                zipStream.CloseEntry();
            }
            var folders = Directory.GetDirectories(path);
            foreach (var folder in folders)
            {
                AddFolder(basename, folder, zipStream, folderOffset);
            }
        }
    }
}