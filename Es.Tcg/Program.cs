using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Es.ToolsCommon;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Es.Tcg
{
    public static class Program
    {
        private static readonly Regex RxCommand = new Regex(@"^((?:[+-])(?=[RBC])(?:REPO|BRANCH|CONFIG))\s*(.*)$",
            RegexOptions.Compiled);

        private static string _privateKeyPath;
        private static readonly HashSet<string> ToRemove = new HashSet<string>(); 

        public static void Main(string[] args)
        {
            Console.Out.WriteLine("Es.Tcg {0}",BuildInfo.Version);
            var textReader = Console.In;
            if (args.Length == 1)
                textReader = File.OpenText(args[0]);

            _privateKeyPath = Environment.GetEnvironmentVariable("TCG_PK") ?? @"C:\TeamCityData\config\id_rsa";
            if (!Directory.Exists("_Root"))
            {
                var tcgDir = Environment.GetEnvironmentVariable("TCG_DIR") ?? @"C:\TeamCityData\config\projects\";
                if (Directory.Exists(tcgDir))
                {
                    Environment.CurrentDirectory = tcgDir;
                }
                if (!Directory.Exists("_Root"))
                {
                    Console.Out.WriteLine(
                        "Expected a _Root directory to exist in the current working directory. Run in the TeamCity config directory or set TCG_DIR to the config directory path.");
                    Environment.Exit(-1);
                }
            }

            var repo = string.Empty;
            var branch = string.Empty;
            var version = string.Empty;
            var configSb = new StringBuilder();

            foreach (var dir in Directory.EnumerateDirectories(".").Select(x => x.Substring(2)).Where(x => x[0] != '_'))
                ToRemove.Add(dir);

            for (;;)
            {
                var line = textReader.ReadLine();

                if (line == null)
                    break;

                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var commandMatch = RxCommand.Match(line);
                if (commandMatch.Success)
                {
                    var cmd = commandMatch.Groups[1].Value;
                    var arg = commandMatch.Groups[2].Value.Trim();

                    switch (cmd)
                    {
                        case "+REPO":
                            if (repo != string.Empty)
                            {
                                Console.Out.WriteLine("+REPO encounted while repo already set!");
                                Environment.Exit(-1);
                            }
                            repo = arg;
                            break;
                        case "-REPO":
                            if (repo == string.Empty)
                            {
                                Console.Out.WriteLine("-REPO encounted while repo not set!");
                                Environment.Exit(-1);
                            }
                            repo = string.Empty;
                            break;
                        case "+BRANCH":
                            if (repo == string.Empty)
                            {
                                Console.Out.WriteLine("+BRANCH outside of a +REPO!");
                                Environment.Exit(-1);
                            }
                            if (branch != string.Empty)
                            {
                                Console.Out.WriteLine("+BRANCH encounted while branch already set!");
                                Environment.Exit(-1);
                            }
                        {
                            var bargs = arg.Split(new[] {' '}, StringSplitOptions.RemoveEmptyEntries);
                            branch = bargs[0];
                            version = bargs[1];
                            break;
                        }
                        case "-BRANCH":
                            if (branch == string.Empty)
                            {
                                Console.Out.WriteLine("-BRANCH encounted while branch not set!");
                                Environment.Exit(-1);
                            }
                            try
                            {
                                WriteTeamCityConfig(repo, branch, version, configSb.ToString());
                            }
                            catch (Exception ex)
                            {
                                Console.Out.WriteLine("Could not configure {0} {1} {2}{3}", repo, branch,
                                    Environment.NewLine, ex);
                            }
                            branch = string.Empty;
                            configSb.Clear();
                            break;
                        default:
                            Console.Out.WriteLine("Invalid command: {0}", cmd);
                            Environment.Exit(-1);
                            break;
                    }
                }
                else
                {
                    configSb.AppendLine(line);
                }
            }
            //using (var wc = new WebClient())
            {
                //wc.Credentials = new NetworkCredential { UserName = "root", Password = "admin" };
                foreach (var d in ToRemove)
                {
                    Console.WriteLine($"removing {d}");

                    try
                    {
                        //wc.UploadString($"http://teamcity.rhi:82/httpAuth/app/rest/projects/{d}", "DELETE", "");
                        Directory.Delete(d,true);
                    }
                    catch
                    {
                        //ignored
                    }
                }
            }
        }

        private static Guid ToGuid(string x)
        {
            var h = x.Hash();
            var b = new byte[16];
            for (var i = 0; i < 8; ++i)
                b[i + 8] = (byte) ((h >> i) & 0xff);
            return new Guid(b);
        }

        private static void WriteTeamCityConfig(string repo, string branch, string version, string config)
        {
            if (config.StartsWith("skip")) // build.tcg wasn't present in the branch, skip it.
                return;

            Console.WriteLine("Configuring {0} {1}", repo, branch);

            var repoPath = repo.Replace(".git", "").Split(':')[1];
            var repoPathArray = repoPath.Split('/');
            var branchPrefix = BranchPrefix(branch);
            var branchParentPrefix = BranchParentPrefix(branch);

            var outputDir = EnsureHierarchy(repoPathArray, branch, branchPrefix, branchParentPrefix);

            Console.WriteLine("-> {0}", outputDir);
            var cjson = JObject.Parse(config);
            var publish = cjson.GetValue("publish", string.Empty);
            var name = cjson.GetValue("name", repoPathArray.Last());
            var sln = cjson.GetValue("sln", name + ".sln");

            if (branch != "master")
                version = version + "-" + branchPrefix;

            ConfigureVcs(outputDir, repo, branch);
            ConfigureBuild(outputDir, name, sln, version, publish);
        }

        private static void ConfigureBuild(string outputDir, string name, string sln, string version, string publish)
        {
            var sb = new StringBuilder();
            var buildTypesDir = Path.Combine(outputDir, "buildTypes");
            if (!Directory.Exists(buildTypesDir))
                Directory.CreateDirectory(buildTypesDir);

            var uuid = ToGuid(outputDir + "\0build");

            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine(
                $"<build-type xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" uuid=\"{uuid}\" xsi:noNamespaceSchemaLocation=\"http://www.jetbrains.com/teamcity/schemas/9.0/project-config.xsd\">");
            sb.AppendLine($"  <name>{name}.{version}</name>");
            sb.AppendLine("  <description></description>");
            sb.AppendLine("  <settings>");

            if (!string.IsNullOrEmpty(publish))
            {
                sb.AppendLine("  <options>");
                sb.AppendLine($"    <option name=\"artifactRules\" value=\"{publish}\" />");
                sb.AppendLine("  </options>");
            }

            sb.AppendLine("    <parameters />");
            sb.AppendLine("    <build-runners>");
            sb.AppendLine("      <runner id=\"RUNNER_5\" name=\"Configure\" type=\"simpleRunner\">");
            sb.AppendLine("        <parameters>");
            sb.AppendLine("          <param name=\"command.executable\" value=\"es.vsg.exe\" />");
            sb.AppendLine("          <param name=\"teamcity.step.mode\" value=\"default\" />");
            sb.AppendLine("        </parameters>");
            sb.AppendLine("      </runner>");
            sb.AppendLine("      <runner id=\"RUNNER_3\" name=\"\" type=\"dotnet-tools-inspectcode\">");
            sb.AppendLine("        <parameters>");
            sb.AppendLine($"          <param name=\"dotnet-tools-inspectcode.solution\" value=\"{sln}\" />");
            sb.AppendLine("          <param name=\"teamcity.step.mode\" value=\"default\" />");
            sb.AppendLine("        </parameters>");
            sb.AppendLine("      </runner>");
            sb.AppendLine("      <runner id=\"RUNNER_4\" name=\"\" type=\"dotnet-tools-dupfinder\">");
            sb.AppendLine("        <parameters>");
            sb.AppendLine("          <param name=\"dotnet-tools-dupfinder.discard_cost\" value=\"70\" />");
            sb.AppendLine("          <param name=\"dotnet-tools-dupfinder.hashing.discard_literals\" value=\"true\" />");
            sb.AppendLine("          <param name=\"dotnet-tools-dupfinder.include_files\" value=\"**/*.cs\" />");
            sb.AppendLine("          <param name=\"dotnet-tools-dupfinder.exclude_by_opening_comment\"><![CDATA[exclude from duplicate code check\ngenerated code]]></param>");
            sb.AppendLine("          <param name=\"teamcity.step.mode\" value=\"default\" />");
            sb.AppendLine("        </parameters>");
            sb.AppendLine("      </runner>");
            sb.AppendLine("      <runner id=\"RUNNER_1\" name=\"msbuild\" type=\"MSBuild\">");
            sb.AppendLine("        <parameters>");
            sb.AppendLine($"          <param name=\"build-file-path\" value=\"{sln}\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.NCover.HTMLReport.File.Sort\" value=\"0\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.NCover.HTMLReport.File.Type\" value=\"1\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.NCover.Reg\" value=\"selected\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.NCover.platformBitness\" value=\"x86\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.NCover.platformVersion\" value=\"v2.0\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.NCover3.Reg\" value=\"selected\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.NCover3.args\" value=\"//ias .*\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.NCover3.platformBitness\" value=\"x86\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.NCover3.platformVersion\" value=\"v2.0\" />");
            sb.AppendLine(
                "          <param name=\"dotNetCoverage.NCover3.reporter.executable.args\" value=\"//or FullCoverageReport:Html:{teamcity.report.path}\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.PartCover.Reg\" value=\"selected\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.PartCover.includes\" value=\"[*]*\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.PartCover.platformBitness\" value=\"x86\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.PartCover.platformVersion\" value=\"v2.0\" />");
            sb.AppendLine("          <param name=\"msbuild_version\" value=\"14.0\" />");
            sb.AppendLine("          <param name=\"run-platform\" value=\"x64\" />");
            sb.AppendLine("          <param name=\"runnerArgs\" value=\"/Property:Configuration=Release\" />");
            sb.AppendLine("          <param name=\"teamcity.step.mode\" value=\"default\" />");
            sb.AppendLine("          <param name=\"toolsVersion\" value=\"14.0\" />");
            sb.AppendLine("        </parameters>");
            sb.AppendLine("      </runner>");
            sb.AppendLine("      <runner id=\"RUNNER_2\" name=\"\" type=\"NUnit\">");
            sb.AppendLine("        <parameters>");
            sb.AppendLine("          <param name=\"dotNetCoverage.NCover.HTMLReport.File.Sort\" value=\"0\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.NCover.HTMLReport.File.Type\" value=\"1\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.NCover.Reg\" value=\"selected\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.NCover.platformBitness\" value=\"x86\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.NCover.platformVersion\" value=\"v2.0\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.NCover3.Reg\" value=\"selected\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.NCover3.args\" value=\"//ias .*\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.NCover3.platformBitness\" value=\"x86\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.NCover3.platformVersion\" value=\"v2.0\" />");
            sb.AppendLine(
                "          <param name=\"dotNetCoverage.NCover3.reporter.executable.args\" value=\"//or FullCoverageReport:Html:{teamcity.report.path}\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.PartCover.Reg\" value=\"selected\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.PartCover.includes\" value=\"[*]*\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.PartCover.platformBitness\" value=\"x86\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.PartCover.platformVersion\" value=\"v2.0\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.dotCover.attributeFilters\" value=\"-:System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute\" />");
            sb.AppendLine("          <param name=\"dotNetCoverage.tool\" value=\"dotcover\" />");
            sb.AppendLine("          <param name=\"dotNetTestRunner.Type\" value=\"NUnit\" />");
            sb.AppendLine("          <param name=\"nunit_enabled\" value=\"checked\" />");
            sb.AppendLine("          <param name=\"nunit_environment\" value=\"v4.0\" />");
            sb.AppendLine("          <param name=\"nunit_include\" value=\"o/*/bin/Release/*.Test.dll\" />");
            //sb.AppendLine("          <param name=\"nunit_path\" value=\"C:\\Program Files (x86)\\NUnit.org\\nunit-console\\nunit3-console.exe\" />");
            sb.AppendLine("          <param name=\"nunit_platform\" value=\"MSIL\" />");
            sb.AppendLine("          <param name=\"nunit_version\" value=\"NUnit-2.6.4\" />");
            sb.AppendLine("          <param name=\"teamcity.step.mode\" value=\"default\" />");
            sb.AppendLine("        </parameters>");
            sb.AppendLine("      </runner>");
            sb.AppendLine("      <runner id=\"RUNNER_6\" name=\"Nuget Package and Push\" type=\"simpleRunner\">");
            sb.AppendLine("        <parameters>");
            sb.AppendLine("          <param name=\"command.executable\" value=\"es.nup.exe\" />");
            sb.AppendLine($"          <param name=\"command.parameters\" value=\"{version}\" />");
            sb.AppendLine("          <param name=\"teamcity.step.mode\" value=\"default\" />");
            sb.AppendLine("        </parameters>");
            sb.AppendLine("      </runner>");
            sb.AppendLine("    </build-runners>");
            sb.AppendLine("    <vcs-settings>");
            sb.AppendLine($"      <vcs-entry-ref root-id=\"{outputDir}\" />");
            sb.AppendLine("    </vcs-settings>");
            sb.AppendLine("    <requirements />");
            sb.AppendLine("    <build-triggers>");
            sb.AppendLine("      <build-trigger id=\"vcsTrigger\" type=\"vcsTrigger\">");
            sb.AppendLine("        <parameters>");
            sb.AppendLine($"          <param name=\"branchFilter\" value=\"+:*\" />");
            //sb.AppendLine("          <param name=\"groupCheckinsByCommitter\" value=\"true\" />");
            //sb.AppendLine("          <param name=\"perCheckinTriggering\" value=\"true\" />");
            sb.AppendLine("          <param name=\"quietPeriodMode\" value=\"DO_NOT_USE\" />");
            sb.AppendLine("        </parameters>");
            sb.AppendLine("      </build-trigger>");
            sb.AppendLine("    </build-triggers>");
            sb.AppendLine("    <build-extensions>");
            sb.AppendLine("      <extension id=\"BUILD_EXT_1\" type=\"BuildFailureOnMetric\">");
            sb.AppendLine("        <parameters>");
            sb.AppendLine("          <param name=\"anchorBuild\" value=\"lastSuccessful\" />");
            sb.AppendLine("          <param name=\"metricKey\" value=\"CodeCoverageS\" />");
            sb.AppendLine("          <param name=\"metricThreshold\" value=\"100\" />");
            sb.AppendLine("          <param name=\"metricUnits\" value=\"metricUnitsDefault\" />");
            sb.AppendLine("          <param name=\"moreOrLess\" value=\"less\" />");
            sb.AppendLine("          <param name=\"withBuildAnchor\" value=\"false\" />");
            sb.AppendLine("        </parameters>");
            sb.AppendLine("      </extension>");
            sb.AppendLine("      <extension id=\"BUILD_EXT_2\" type=\"BuildFailureOnMetric\">");
            sb.AppendLine("        <parameters>");
            sb.AppendLine("          <param name=\"anchorBuild\" value=\"lastSuccessful\" />");
            sb.AppendLine("          <param name=\"metricKey\" value=\"InspectionStatsE\" />");
            sb.AppendLine("          <param name=\"metricThreshold\" value=\"0\" />");
            sb.AppendLine("          <param name=\"metricUnits\" value=\"metricUnitsDefault\" />");
            sb.AppendLine("          <param name=\"moreOrLess\" value=\"more\" />");
            sb.AppendLine("          <param name=\"withBuildAnchor\" value=\"false\" />");
            sb.AppendLine("        </parameters>");
            sb.AppendLine("      </extension>");
            sb.AppendLine("      <extension id=\"BUILD_EXT_3\" type=\"BuildFailureOnMetric\">");
            sb.AppendLine("        <parameters>");
            sb.AppendLine("          <param name=\"anchorBuild\" value=\"lastSuccessful\" />");
            sb.AppendLine("          <param name=\"metricKey\" value=\"buildFailedTestCount\" />");
            sb.AppendLine("          <param name=\"metricThreshold\" value=\"0\" />");
            sb.AppendLine("          <param name=\"metricUnits\" value=\"metricUnitsDefault\" />");
            sb.AppendLine("          <param name=\"moreOrLess\" value=\"more\" />");
            sb.AppendLine("          <param name=\"withBuildAnchor\" value=\"false\" />");
            sb.AppendLine("        </parameters>");
            sb.AppendLine("      </extension>");
            sb.AppendLine("    </build-extensions>");
            sb.AppendLine("    <cleanup />");
            sb.AppendLine("  </settings>");
            sb.AppendLine("</build-type>");
            sb.AppendLine("");

            var buildFile = Path.Combine(buildTypesDir, outputDir + ".xml");
            Console.Out.WriteLine("creating {0}", buildFile);
            File.WriteAllText(buildFile, sb.ToString());
        }

        private static string EnsureHierarchy(string[] repoParts, string branch, string branchPrefix, string branchParentPrefix)
        {
            string parent = null;
            string dir = null;
            Guid projectUuid;
            var sb = new StringBuilder();
            string projectConfigFile;

            if (branch != "master")
            {
                parent = branchParentPrefix=="master"?"":branchParentPrefix;
                dir = branchPrefix;

                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                projectUuid = ToGuid(dir);
                sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                if (string.IsNullOrWhiteSpace(parent))
                {
                    sb.AppendLine(
                        $"<project xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" uuid=\"{projectUuid}\" xsi:noNamespaceSchemaLocation=\"http://www.jetbrains.com/teamcity/schemas/9.0/project-config.xsd\">");
                }
                else
                {
                    sb.AppendLine(
                        $"<project xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" parent-id=\"{parent}\" uuid=\"{projectUuid}\" xsi:noNamespaceSchemaLocation=\"http://www.jetbrains.com/teamcity/schemas/9.0/project-config.xsd\">");
                }

                sb.AppendLine($"  <name>{branch}</name>");
                sb.AppendLine("  <parameters />");
                sb.AppendLine("  <cleanup />");
                sb.AppendLine("</project>");

                ToRemove.Remove(dir);
                projectConfigFile = Path.Combine(dir, "project-config.xml");
                Console.Out.WriteLine("creating {0}", projectConfigFile);
                File.WriteAllText(projectConfigFile, sb.ToString());
                sb.Clear();
                parent = dir;
                dir += "_";
            }
            dir += string.Join("_", repoParts);
            projectUuid = ToGuid(dir);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            if (string.IsNullOrWhiteSpace(parent))
            {
                sb.AppendLine(
                    $"<project xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" uuid=\"{projectUuid}\" xsi:noNamespaceSchemaLocation=\"http://www.jetbrains.com/teamcity/schemas/9.0/project-config.xsd\">");
            }
            else
            {
                sb.AppendLine(
                    $"<project xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" parent-id=\"{parent}\" uuid=\"{projectUuid}\" xsi:noNamespaceSchemaLocation=\"http://www.jetbrains.com/teamcity/schemas/9.0/project-config.xsd\">");
            }
            sb.AppendLine($"  <name>{string.Join("/", repoParts)}</name>");
            sb.AppendLine("  <parameters />");
            sb.AppendLine("  <cleanup />");
            sb.AppendLine("</project>");

            ToRemove.Remove(dir);
            projectConfigFile = Path.Combine(dir, "project-config.xml");
            Console.Out.WriteLine("creating {0}", projectConfigFile);
            File.WriteAllText(projectConfigFile, sb.ToString());

            return dir;
        }

        static readonly Regex RxBranchPrefix = new Regex(@"^((?:[a-zA-Z]+[0-9]+)+)\.", RegexOptions.Compiled);
        static readonly Regex RxBranchParentPrefix = new Regex(@"^[a-zA-Z]+[0-9]+((?:[a-zA-Z]+[0-9]+)+)\.", RegexOptions.Compiled);

        internal static string BranchPrefix(string branch)
        {
            var m = RxBranchPrefix.Match(branch);
            return !m.Success ? branch : m.Groups[1].Value;
        }

        internal static string BranchParentPrefix(string branch)
        {
            var m = RxBranchParentPrefix.Match(branch);
            return !m.Success ? "master" : m.Groups[1].Value;
        }

        private static void ConfigureVcs(string outputDir, string repo, string branch)
        {
            var vcsUuid = ToGuid(outputDir);
            var vcsRootsDir = Path.Combine(outputDir, "vcsRoots");
            if (!Directory.Exists(vcsRootsDir))
                Directory.CreateDirectory(vcsRootsDir);

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine(
                $"<vcs-root xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" uuid=\"{vcsUuid}\" type=\"jetbrains.git\" xsi:noNamespaceSchemaLocation=\"http://www.jetbrains.com/teamcity/schemas/9.0/project-config.xsd\">");
            sb.AppendLine($"  <name>{outputDir}</name>");
            sb.AppendLine("  <param name=\"agentCleanFilesPolicy\" value=\"ALL_UNTRACKED\" />");
            sb.AppendLine("  <param name=\"agentCleanPolicy\" value=\"ALWAYS\" />");
            sb.AppendLine("  <param name=\"authMethod\" value=\"PRIVATE_KEY_FILE\" />");
            sb.AppendLine($"  <param name=\"branch\" value=\"refs/heads/{branch}\" />");
            sb.AppendLine("  <param name=\"ignoreKnownHosts\" value=\"true\" />");
            sb.AppendLine($"  <param name=\"privateKeyPath\" value=\"{_privateKeyPath}\"/>");
            sb.AppendLine("  <param name=\"submoduleCheckout\" value=\"CHECKOUT\" />");
            sb.AppendLine($"  <param name=\"url\" value=\"{repo}\" />");
            sb.AppendLine("  <param name=\"useAlternates\" value=\"true\" />");
            sb.AppendLine("  <param name=\"usernameStyle\" value=\"USERID\" />");
            sb.AppendLine("</vcs-root>");
            sb.AppendLine("");

            File.WriteAllText(Path.Combine(vcsRootsDir, outputDir + ".xml"), sb.ToString());
        }

        private static T GetValue<T>(this JObject jObject, string path, T defaultValue)
        {
            var temp = jObject.SelectToken(path, false);
            return temp == null ? defaultValue : temp.ToObject<T>();
        }
    }

    [TestFixture]
    public class Tf
    {
        [Test]
        public void Test()
        {
            Assert.AreEqual("p4f2",Program.BranchPrefix("p4f2.foo"));
            Assert.AreEqual("p4", Program.BranchParentPrefix("p4f2.foo"));
            Assert.AreEqual("master", Program.BranchParentPrefix("p4.foo"));
            Assert.AreEqual("master", Program.BranchPrefix("master"));
        }
    }
}