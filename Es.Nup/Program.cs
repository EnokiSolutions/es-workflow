using System;
using System.IO;
using System.Linq;
using Es.ToolsCommon;
using Newtonsoft.Json.Linq;

namespace Es.Nup
{
    public static class Program
    {
        /* 
        
nuget pack $proj$/$proj$.csproj -Prop Configuration=Release

nuspec file

<?xml version="1.0"?>
<package >
  <metadata>
    <id>$id$</id>
    <version>$version$</version>
    <title>$title$</title>
    <authors>$author$</authors>
    <owners>$author$</owners>
    <licenseUrl>http://LICENSE_URL_HERE_OR_DELETE_THIS_LINE</licenseUrl>
    <projectUrl>http://PROJECT_URL_HERE_OR_DELETE_THIS_LINE</projectUrl>
    <iconUrl>http://ICON_URL_HERE_OR_DELETE_THIS_LINE</iconUrl>
    <requireLicenseAcceptance>false</requireLicenseAcceptance>
    <description>$description$</description>
    <releaseNotes>Summary of changes made in this release of the package.</releaseNotes>
    <copyright>Copyright 2016</copyright>
    <tags>Tag1 Tag2</tags>
    <dependencies>
      <dependency id="X" version="1.1.0" />
      <dependency id="Y" version="1.0.0" />
    </dependencies>
  </metadata>
</package>

        */

        private static string _nugetExe;

        public static void Main(string[] args)
        {
            Console.WriteLine("Es.Nup {0}", BuildInfo.Version);

            if (args.Length < 1)
                return;

            var enviromentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            var paths = enviromentPath.Split(';');
            Func<string, string> findExe = exe => paths.Select(x => Path.Combine(x, exe)).FirstOrDefault(File.Exists);

            _nugetExe = findExe("nuget.exe");
            if (_nugetExe == null)
            {
                Console.WriteLine("nuget not found in path");
                Environment.Exit(-1);
            }

            var version = args[0];
            var apiKey = Environment.GetEnvironmentVariable("NUP_API_KEY") ?? "LETMEIN";
            var nugetUrl = Environment.GetEnvironmentVariable("NUP_URI") ?? "http://nuget.rhi/api";
            var buildNo = Environment.GetEnvironmentVariable("BUILD_NUMBER");

            if (version.Contains("-") && !string.IsNullOrWhiteSpace(buildNo))
                version += "-" + buildNo;

            foreach (var dir in Directory.EnumerateDirectories("."))
            {
                foreach (var nup in Directory.EnumerateFiles(dir, "pack.nup"))
                {
                    try
                    {
                        Pack(version, apiKey, nugetUrl, dir, File.ReadAllText(nup));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"pack issue: {ex}");
                    }
                }
            }
        }

        private static void Pack(string version, string apiKey, string nugetUri, string csprojDir, string nup)
        {
            var nJson = JObject.Parse(nup);
            var authors = nJson.GetValue("authors", "lazy");
            var tags = nJson.GetValue("tags", "");
            var description = nJson.GetValue("description", "lazy");
            // todo: deps

            var id = csprojDir.Substring(2); // remove ./

            File.WriteAllText(Path.Combine(csprojDir, id + ".nuspec"),
                $@"<?xml version=""1.0""?>
<package>
  <metadata>
    <id>{id}</id>
    <version>{version}</version>
    <authors>{authors}</authors>
    <description>{description}</description>
    <copyright>Copyright {DateTime.Now.Year}</copyright>
    <tags>{tags}</tags>
  </metadata>
</package>
");
            ProgramRunner.Run(_nugetExe, $"pack {csprojDir}/{id}.csproj -Prop Configuration=Release");
            ProgramRunner.Run(_nugetExe, $"push {id}.{version}.nupkg -ApiKey {apiKey} -Source {nugetUri}");
        }

        private static T GetValue<T>(this JObject jObject, string path, T defaultValue)
        {
            var temp = jObject.SelectToken(path, false);
            return temp == null ? defaultValue : temp.ToObject<T>();
        }
    }
}