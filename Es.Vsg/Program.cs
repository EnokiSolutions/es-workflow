using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Es.ToolsCommon;
using Newtonsoft.Json.Linq;

namespace Es.Vsg
{
    public static class Program
    {
        private const string SlnVsgFilename = "sln.vsg";
        private const string CsprojVsgFilename = "csproj.vsg";
        private const string VersionFilename = "version.txt";
        private const string DefaultVersion = "0.0.0";

        private const int WaitTimeMilliseconds = 60000;
        private static string _nugetExe;
        private static string _ilMergeExe;

        public static void Main(string[] args)
        {
            Console.WriteLine("Es.Vsg {0}",BuildInfo.Version);

            if (!File.Exists(SlnVsgFilename))
            {
                Console.WriteLine("Current working directory {1} must have a {0} file", SlnVsgFilename,
                    Environment.CurrentDirectory);
                return;
            }

            var enviromentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            var paths = enviromentPath.Split(';');
            Func<string, string> findExe = exe => paths.Select(x => Path.Combine(x, exe)).FirstOrDefault(File.Exists);

            _nugetExe = findExe("nuget.exe");
            _ilMergeExe = findExe("ilmerge.exe");

            if (_nugetExe == null)
            {
                Console.WriteLine("nuget not found in path");
                return;
            }

            var slnJson = JObject.Parse(File.ReadAllText(SlnVsgFilename));
            var slnFilename = slnJson["name"].ToObject<string>() + ".sln";
            var hasContracts = slnJson.GetValue("contracts", false);
            Console.WriteLine("{0}", slnFilename);

            var packageInfos = slnJson["packages"].ToObject<JArray>().Select(x => x.ToObject<string[]>()).ToArray();

            UpdatePackages(packageInfos);
            var packagesDllMap = ScanPackages(packageInfos);

            var version = DefaultVersion;
            if (File.Exists(VersionFilename))
                version = File.ReadAllLines(VersionFilename).FirstOrDefault()??DefaultVersion;

            var csProjAndGuids = new Dictionary<string, Guid>(StringComparer.CurrentCultureIgnoreCase);
            foreach (
                var dir in
                    Directory
                        .EnumerateDirectories(".")
                        .Where(x => File.Exists(Path.Combine(x, "csproj.vsg")))
                        .Select(x => new DirectoryInfo(x).Name))
            {
                csProjAndGuids[dir] = ToGuid(dir);
            }
            

            foreach (var csProjAndGuid in csProjAndGuids)
            {
                WriteVersionFile(version, csProjAndGuid.Key);
                WriteCsProjAndAssemblyInfo(csProjAndGuid, csProjAndGuids, packagesDllMap, hasContracts);
            }
            WriteSln(slnFilename, csProjAndGuids);
        }

        private static void WriteVersionFile(string version, string csProjDir)
        {
            File.WriteAllText(
                Path.Combine(csProjDir,"BuildInfo.cs"),
                $@"//generated code do not edit
// ReSharper disable UnusedMember.Local
// ReSharper disable UnusedMember.Global
namespace {csProjDir}
{{
    internal static class BuildInfo
    {{
        public const string Version=""{version}"";
    }}
}}");
        }

        private static void WriteCsProjAndAssemblyInfo(KeyValuePair<string, Guid> csProjAndGuid,
            IDictionary<string, Guid> csProjAndGuids, IDictionary<string, string> packagesDllMap, bool hasContracts)
        {
            var csprojJson = JObject.Parse(File.ReadAllText(Path.Combine(csProjAndGuid.Key, CsprojVsgFilename)));
            var refs = csprojJson["refs"].ToObject<JArray>().Select(x => x.ToObject<string>());
            var startupWorkingDirectory = csprojJson.GetValue("debugdir", "../../../..");

            var csProjDir = csProjAndGuid.Key;
            var guid = csProjAndGuid.Value;

            var hasTests = File.Exists(Path.Combine(csProjDir + ".Test", CsprojVsgFilename));

            var fuckingUserFile = Path.Combine(csProjDir, csProjDir + ".csproj.user");
            if (File.Exists(fuckingUserFile))
                File.Delete(fuckingUserFile);

            var outputType = "library";
            if (File.Exists(Path.Combine(csProjDir, "Program.cs")))
                outputType = "exe";

            var propertiesDir = Path.Combine(csProjDir, "Properties");
            if (!Directory.Exists(propertiesDir))
                Directory.CreateDirectory(propertiesDir);
            var assemblyInfoFile = Path.Combine(propertiesDir, "AssemblyInfo.cs");
            var assemblyInfoContents =
                $@"using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
[assembly: AssemblyTitle(""{csProjDir}"")]
[assembly: AssemblyProduct(""{csProjDir}"")]
[assembly: AssemblyCopyright(""Copyright © {DateTime
                    .Now.Year}"")]
[assembly: ComVisible(false)]
[assembly: AssemblyVersion(""1.0.0.0"")]
[assembly: AssemblyFileVersion(""1.0.0.0"")]
[assembly: InternalsVisibleTo(""DynamicProxyGenAssembly2"")]";
            if (hasTests)
                assemblyInfoContents += $@"
[assembly: InternalsVisibleTo(""{csProjDir}.Test"")]";
            UpdateOnlyIfDifferent(assemblyInfoFile, assemblyInfoContents);

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine(
                "<Project ToolsVersion=\"14.0\" DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");
            sb.AppendLine(
                "  <Import Project=\"$(MSBuildExtensionsPath)\\$(MSBuildToolsVersion)\\Microsoft.Common.props\" Condition=\"Exists(\'$(MSBuildExtensionsPath)\\$(MSBuildToolsVersion)\\Microsoft.Common.props\')\" />");
            sb.AppendLine("  <PropertyGroup>");
            sb.AppendLine("    <Configuration Condition=\" \'$(Configuration)\' == \'\' \">Debug</Configuration>");
            sb.AppendLine("    <Platform Condition=\" \'$(Platform)\' == \'\' \">x64</Platform>");
            sb.AppendLine($"    <ProjectGuid>{{{guid}}}</ProjectGuid>");
            sb.AppendLine($"    <OutputType>{outputType}</OutputType>");
            sb.AppendLine("    <AppDesignerFolder>Properties</AppDesignerFolder>");
            sb.AppendLine($"    <RootNamespace>{csProjDir}</RootNamespace>");
            sb.AppendLine($"    <AssemblyName>{csProjDir}</AssemblyName>");
            sb.AppendLine("    <TargetFrameworkVersion>v4.5</TargetFrameworkVersion>");
            sb.AppendLine("    <FileAlignment>512</FileAlignment>");
            sb.AppendLine("    <AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>");
            sb.AppendLine("    <TargetFrameworkProfile />");
            sb.AppendLine($"    <StartWorkingDirectory>{startupWorkingDirectory}</StartWorkingDirectory>");
            if (hasContracts)
                sb.AppendLine("    <CodeContractsAssemblyMode>0</CodeContractsAssemblyMode>");
            sb.AppendLine("  </PropertyGroup>");
            sb.AppendLine("  <PropertyGroup Condition=\" \'$(Configuration)|$(Platform)\' == \'Debug|x64\' \">");
            sb.AppendLine("    <PlatformTarget>x64</PlatformTarget>");
            sb.AppendLine("    <DebugSymbols>true</DebugSymbols>");
            sb.AppendLine("    <DebugType>full</DebugType>");
            sb.AppendLine("    <Optimize>false</Optimize>");
            sb.AppendLine($"    <OutputPath>..\\o\\{csProjDir}\\bin\\Debug\\</OutputPath>");
            sb.AppendLine($"    <IntermediateOutputPath>..\\o\\{csProjDir}\\obj\\Debug\\</IntermediateOutputPath>");
            if (hasContracts)
                sb.AppendLine("    <DefineConstants>DEBUG;TRACE;CODE_ANALYSIS;CONTRACTS_FULL</DefineConstants>");
            else
                sb.AppendLine("    <DefineConstants>DEBUG;TRACE</DefineConstants>");
            sb.AppendLine("    <ErrorReport>prompt</ErrorReport>");
            sb.AppendLine("    <WarningLevel>4</WarningLevel>");
            sb.AppendLine("    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>");
            sb.AppendLine("    <RunCodeAnalysis>true</RunCodeAnalysis>");
            sb.AppendLine("    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>");
            if (hasContracts)
            {
                sb.AppendLine("    <CodeContractsEnableRuntimeChecking>True</CodeContractsEnableRuntimeChecking>");
                sb.AppendLine("    <CodeContractsRuntimeOnlyPublicSurface>False</CodeContractsRuntimeOnlyPublicSurface>");
                sb.AppendLine("    <CodeContractsRuntimeThrowOnFailure>True</CodeContractsRuntimeThrowOnFailure>");
                sb.AppendLine("    <CodeContractsRuntimeCallSiteRequires>True</CodeContractsRuntimeCallSiteRequires>");
                sb.AppendLine("    <CodeContractsRuntimeSkipQuantifiers>False</CodeContractsRuntimeSkipQuantifiers>");
                sb.AppendLine("    <CodeContractsRunCodeAnalysis>True</CodeContractsRunCodeAnalysis>");
                sb.AppendLine("    <CodeContractsNonNullObligations>True</CodeContractsNonNullObligations>");
                sb.AppendLine("    <CodeContractsBoundsObligations>True</CodeContractsBoundsObligations>");
                sb.AppendLine("    <CodeContractsArithmeticObligations>True</CodeContractsArithmeticObligations>");
                sb.AppendLine("    <CodeContractsEnumObligations>True</CodeContractsEnumObligations>");
                sb.AppendLine("    <CodeContractsRedundantAssumptions>True</CodeContractsRedundantAssumptions>");
                sb.AppendLine(
                    "    <CodeContractsAssertsToContractsCheckBox>True</CodeContractsAssertsToContractsCheckBox>");
                sb.AppendLine("    <CodeContractsRedundantTests>True</CodeContractsRedundantTests>");
                sb.AppendLine(
                    "    <CodeContractsMissingPublicRequiresAsWarnings>True</CodeContractsMissingPublicRequiresAsWarnings>");
                sb.AppendLine(
                    "    <CodeContractsMissingPublicEnsuresAsWarnings>True</CodeContractsMissingPublicEnsuresAsWarnings>");
                sb.AppendLine("    <CodeContractsInferRequires>True</CodeContractsInferRequires>");
                sb.AppendLine("    <CodeContractsInferEnsures>True</CodeContractsInferEnsures>");
                sb.AppendLine(
                    "    <CodeContractsInferEnsuresAutoProperties>True</CodeContractsInferEnsuresAutoProperties>");
                sb.AppendLine("    <CodeContractsInferObjectInvariants>True</CodeContractsInferObjectInvariants>");
                sb.AppendLine("    <CodeContractsSuggestAssumptions>True</CodeContractsSuggestAssumptions>");
                sb.AppendLine(
                    "    <CodeContractsSuggestAssumptionsForCallees>True</CodeContractsSuggestAssumptionsForCallees>");
                sb.AppendLine("    <CodeContractsSuggestRequires>True</CodeContractsSuggestRequires>");
                sb.AppendLine("    <CodeContractsNecessaryEnsures>True</CodeContractsNecessaryEnsures>");
                sb.AppendLine("    <CodeContractsSuggestObjectInvariants>True</CodeContractsSuggestObjectInvariants>");
                sb.AppendLine("    <CodeContractsSuggestReadonly>True</CodeContractsSuggestReadonly>");
                sb.AppendLine("    <CodeContractsRunInBackground>True</CodeContractsRunInBackground>");
                sb.AppendLine("    <CodeContractsShowSquigglies>True</CodeContractsShowSquigglies>");
                sb.AppendLine("    <CodeContractsUseBaseLine>False</CodeContractsUseBaseLine>");
                sb.AppendLine("    <CodeContractsEmitXMLDocs>True</CodeContractsEmitXMLDocs>");
                sb.AppendLine("    <CodeContractsCustomRewriterAssembly />");
                sb.AppendLine("    <CodeContractsCustomRewriterClass />");
                sb.AppendLine("    <CodeContractsLibPaths />");
                sb.AppendLine("    <CodeContractsExtraRewriteOptions />");
                sb.AppendLine("    <CodeContractsExtraAnalysisOptions />");
                sb.AppendLine("    <CodeContractsSQLServerOption />");
                sb.AppendLine("    <CodeContractsBaseLineFile />");
                sb.AppendLine("    <CodeContractsCacheAnalysisResults>True</CodeContractsCacheAnalysisResults>");
                sb.AppendLine(
                    "    <CodeContractsSkipAnalysisIfCannotConnectToCache>False</CodeContractsSkipAnalysisIfCannotConnectToCache>");
                sb.AppendLine("    <CodeContractsFailBuildOnWarnings>True</CodeContractsFailBuildOnWarnings>");
                sb.AppendLine(
                    "    <CodeContractsBeingOptimisticOnExternal>True</CodeContractsBeingOptimisticOnExternal>");
                sb.AppendLine("    <CodeContractsRuntimeCheckingLevel>Full</CodeContractsRuntimeCheckingLevel>");
                sb.AppendLine("    <CodeContractsReferenceAssembly>Build</CodeContractsReferenceAssembly>");
                sb.AppendLine("    <CodeContractsAnalysisWarningLevel>3</CodeContractsAnalysisWarningLevel>");
            }
            sb.AppendLine("  </PropertyGroup>");
            sb.AppendLine("  <PropertyGroup Condition=\" \'$(Configuration)|$(Platform)\' == \'Release|x64\' \">");
            sb.AppendLine("    <PlatformTarget>x64</PlatformTarget>");
            sb.AppendLine("    <DebugType>pdbonly</DebugType>");
            sb.AppendLine("    <Optimize>true</Optimize>");
            sb.AppendLine($"    <OutputPath>..\\o\\{csProjDir}\\bin\\Release\\</OutputPath>");
            sb.AppendLine($"    <IntermediateOutputPath>..\\o\\{csProjDir}\\obj\\Release\\</IntermediateOutputPath>");
            if (hasContracts)
                sb.AppendLine("    <DefineConstants>TRACE;CODE_ANALYSIS;CONTRACTS_FULL</DefineConstants>");
            else
                sb.AppendLine("    <DefineConstants>TRACE</DefineConstants>");
            sb.AppendLine("    <ErrorReport>prompt</ErrorReport>");
            sb.AppendLine("    <WarningLevel>4</WarningLevel>");
            sb.AppendLine("    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>");
            sb.AppendLine("    <RunCodeAnalysis>true</RunCodeAnalysis>");
            sb.AppendLine("    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>");
            if (hasContracts)
            {
                sb.AppendLine("    <CodeContractsEnableRuntimeChecking>True</CodeContractsEnableRuntimeChecking>");
                sb.AppendLine("    <CodeContractsRuntimeOnlyPublicSurface>False</CodeContractsRuntimeOnlyPublicSurface>");
                sb.AppendLine("    <CodeContractsRuntimeThrowOnFailure>True</CodeContractsRuntimeThrowOnFailure>");
                sb.AppendLine("    <CodeContractsRuntimeCallSiteRequires>True</CodeContractsRuntimeCallSiteRequires>");
                sb.AppendLine("    <CodeContractsRuntimeSkipQuantifiers>False</CodeContractsRuntimeSkipQuantifiers>");
                sb.AppendLine("    <CodeContractsRunCodeAnalysis>True</CodeContractsRunCodeAnalysis>");
                sb.AppendLine("    <CodeContractsNonNullObligations>True</CodeContractsNonNullObligations>");
                sb.AppendLine("    <CodeContractsBoundsObligations>True</CodeContractsBoundsObligations>");
                sb.AppendLine("    <CodeContractsArithmeticObligations>True</CodeContractsArithmeticObligations>");
                sb.AppendLine("    <CodeContractsEnumObligations>True</CodeContractsEnumObligations>");
                sb.AppendLine("    <CodeContractsRedundantAssumptions>True</CodeContractsRedundantAssumptions>");
                sb.AppendLine("    <CodeContractsAssertsToContractsCheckBox>True</CodeContractsAssertsToContractsCheckBox>");
                sb.AppendLine("    <CodeContractsRedundantTests>True</CodeContractsRedundantTests>");
                sb.AppendLine("    <CodeContractsMissingPublicRequiresAsWarnings>True</CodeContractsMissingPublicRequiresAsWarnings>");
                sb.AppendLine("    <CodeContractsMissingPublicEnsuresAsWarnings>True</CodeContractsMissingPublicEnsuresAsWarnings>");
                sb.AppendLine("    <CodeContractsInferRequires>True</CodeContractsInferRequires>");
                sb.AppendLine("    <CodeContractsInferEnsures>True</CodeContractsInferEnsures>");
                sb.AppendLine("    <CodeContractsInferEnsuresAutoProperties>True</CodeContractsInferEnsuresAutoProperties>");
                sb.AppendLine("    <CodeContractsInferObjectInvariants>True</CodeContractsInferObjectInvariants>");
                sb.AppendLine("    <CodeContractsSuggestAssumptions>True</CodeContractsSuggestAssumptions>");
                sb.AppendLine("    <CodeContractsSuggestAssumptionsForCallees>True</CodeContractsSuggestAssumptionsForCallees>");
                sb.AppendLine("    <CodeContractsSuggestRequires>True</CodeContractsSuggestRequires>");
                sb.AppendLine("    <CodeContractsNecessaryEnsures>True</CodeContractsNecessaryEnsures>");
                sb.AppendLine("    <CodeContractsSuggestObjectInvariants>True</CodeContractsSuggestObjectInvariants>");
                sb.AppendLine("    <CodeContractsSuggestReadonly>True</CodeContractsSuggestReadonly>");
                sb.AppendLine("    <CodeContractsRunInBackground>True</CodeContractsRunInBackground>");
                sb.AppendLine("    <CodeContractsShowSquigglies>True</CodeContractsShowSquigglies>");
                sb.AppendLine("    <CodeContractsUseBaseLine>False</CodeContractsUseBaseLine>");
                sb.AppendLine("    <CodeContractsCustomRewriterAssembly />");
                sb.AppendLine("    <CodeContractsCustomRewriterClass />");
                sb.AppendLine("    <CodeContractsLibPaths />");
                sb.AppendLine("    <CodeContractsExtraRewriteOptions />");
                sb.AppendLine("    <CodeContractsExtraAnalysisOptions />");
                sb.AppendLine("    <CodeContractsSQLServerOption />");
                sb.AppendLine("    <CodeContractsBaseLineFile />");
                sb.AppendLine("    <CodeContractsCacheAnalysisResults>True</CodeContractsCacheAnalysisResults>");
                sb.AppendLine("    <CodeContractsSkipAnalysisIfCannotConnectToCache>False</CodeContractsSkipAnalysisIfCannotConnectToCache>");
                sb.AppendLine("    <CodeContractsFailBuildOnWarnings>True</CodeContractsFailBuildOnWarnings>");
                sb.AppendLine("    <CodeContractsBeingOptimisticOnExternal>True</CodeContractsBeingOptimisticOnExternal>");
                sb.AppendLine("    <CodeContractsRuntimeCheckingLevel>Full</CodeContractsRuntimeCheckingLevel>");
                sb.AppendLine("    <CodeContractsAnalysisWarningLevel>3</CodeContractsAnalysisWarningLevel>");
                sb.AppendLine("    <CodeContractsReferenceAssembly>Build</CodeContractsReferenceAssembly>");
                sb.AppendLine("    <CodeContractsEmitXMLDocs>True</CodeContractsEmitXMLDocs>");
            }
            sb.AppendLine("  </PropertyGroup>");

            if (outputType == "exe")
            {
                sb.AppendLine("  <PropertyGroup>");
                sb.AppendLine($"    <StartupObject>{csProjDir}.Program</StartupObject>");
                sb.AppendLine("  </PropertyGroup>");
            }

            sb.AppendLine("  <ItemGroup>");
            var anyDllRefs = false;
            foreach (var r in refs)
            {
                if (csProjAndGuids.ContainsKey(r))
                {
                    anyDllRefs = true;
                    sb.AppendLine($"    <ProjectReference Include=\"..\\{r}\\{r}.csproj\">");
                    sb.AppendLine($"      <Project>{{{csProjAndGuids[r]}}}</Project>");
                    sb.AppendLine($"      <Name>{r}</Name>");
                    sb.AppendLine("    </ProjectReference>");
                }
                else if (packagesDllMap.ContainsKey(r))
                {
                    anyDllRefs = true;
                    sb.AppendLine($"    <Reference Include=\"{r}\">");
                    sb.AppendLine($"      <HintPath>..\\{packagesDllMap[r]}</HintPath>");
                    sb.AppendLine("      <Private>True</Private>");
                    sb.AppendLine("    </Reference>");
                }
                else
                {
                    sb.AppendLine($"    <Reference Include=\"{r}\" />");
                }
            }
            sb.AppendLine("  </ItemGroup>");

            sb.AppendLine("  <ItemGroup>");
            foreach (var csf in Directory.EnumerateFiles(csProjDir, "*.cs", SearchOption.AllDirectories))
            {
                if (csf.Contains("TemporaryGeneratedFile_"))
                    continue;

                var relCsf = csf.Substring(csProjDir.Length + 1);
                sb.AppendLine($"    <Compile Include=\"{relCsf}\" />");
            }
            sb.AppendLine("  </ItemGroup>");

            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine("    <None Include=\"App.config\" />");
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine("  <Import Project=\"$(MSBuildToolsPath)\\Microsoft.CSharp.targets\" />");
            if (hasContracts)
            {
                var ccid = Environment.GetEnvironmentVariable("CodeContractsInstallDir") ?? "C:\\Program Files (x86)\\Microsoft\\Contracts\\";
                sb.AppendLine(
                    $"  <Import Project=\"{ccid}MsBuild\\v14.0\\Microsoft.CodeContracts.targets\" />");
            }
            
            if (outputType == "exe" && _ilMergeExe != null)
            {
                sb.AppendLine("  <PropertyGroup>");
                if (anyDllRefs)
                {
                    sb.AppendLine("    <PostBuildEvent>ilmerge /wildcards /out:$(SolutionDir)$(TargetFileName) /v4 *.exe *.dll</PostBuildEvent>");
                }
                else
                {
                    sb.AppendLine("    <PostBuildEvent>ilmerge /wildcards /out:$(SolutionDir)$(TargetFileName) /v4 *.exe</PostBuildEvent>");
                }
                sb.AppendLine("  </PropertyGroup>");
            }
            sb.AppendLine("</Project>");

            var appConfigFile = Path.Combine(csProjDir, "app.config");
            if (!File.Exists(appConfigFile))
            {
                File.WriteAllText(appConfigFile,
                    @"<?xml version=""1.0"" encoding=""utf-8""?>
<configuration>
    <startup>
        <supportedRuntime version=""v4.0"" sku="".NETFramework,Version=v4.5"" />
    </startup>
</configuration>");
            }


            var csProjFilename = Path.Combine(csProjDir, csProjDir + ".csproj");
            UpdateOnlyIfDifferent(csProjFilename, sb.ToString());
            //Console.WriteLine($"---- {csProjDir}");
            //Console.Write(sb.ToString());
            //Console.WriteLine("----");

            // TODO: edit assembly info file to add InternalsVisibleTo the tests if tests exist, and enforce assembly settings
        }

        public static void UpdateOnlyIfDifferent(string filename, string contents)
        {
            if (File.Exists(filename))
            {
                var oldContents = File.ReadAllText(filename);
                if (oldContents == contents)
                    return;
            }
            File.WriteAllText(filename, contents);
        }

        private static void WriteSln(string slnFilename, IDictionary<string, Guid> csProjAndGuids)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");

            foreach (var csProjAndGuid in csProjAndGuids)
            {
                var csProjDir = csProjAndGuid.Key;
                var guid = csProjAndGuid.Value;
                Console.WriteLine("{0} -> {1} {2}", csProjDir, Path.Combine(csProjDir, csProjDir + ".csproj"), guid);
                sb.AppendLine(
                    $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{csProjDir}\", \"{csProjDir}\\{csProjDir}.csproj\", \"{{{guid}}}\"");
                sb.AppendLine("EndProject");
            }

            sb.AppendLine("Global");
            sb.AppendLine("        GlobalSection(SolutionConfigurationPlatforms) = preSolution");
            sb.AppendLine("                Debug|x64 = Debug|x64");
            sb.AppendLine("                Release|x64 = Release|x64");
            sb.AppendLine("        EndGlobalSection");
            sb.AppendLine("        GlobalSection(ProjectConfigurationPlatforms) = postSolution");

            foreach (var csProjAndGuid in csProjAndGuids)
            {
                var guid = csProjAndGuid.Value;
                sb.AppendLine($"                {{{guid}}}.Debug|x64.ActiveCfg = Debug|x64");
                sb.AppendLine($"                {{{guid}}}.Debug|x64.Build.0 = Debug|x64");
                sb.AppendLine($"                {{{guid}}}.Release|x64.ActiveCfg = Release|x64");
                sb.AppendLine($"                {{{guid}}}.Release|x64.Build.0 = Release|x64");
            }
            sb.AppendLine("        EndGlobalSection");
            sb.AppendLine("        GlobalSection(SolutionProperties) = preSolution");
            sb.AppendLine("                HideSolutionNode = FALSE");
            sb.AppendLine("        EndGlobalSection");
            sb.AppendLine("EndGlobal");

            // always update to force VS to reload

            if (File.Exists(slnFilename))
                File.Delete(slnFilename);

            File.WriteAllText(slnFilename, sb.ToString());
            var now = DateTime.Now;
            File.SetLastAccessTime(slnFilename, now);
            File.SetLastWriteTime(slnFilename, now);
            File.SetCreationTime(slnFilename, now);

            //Console.WriteLine($"---- {slnFilename}");
            //Console.Write(sb.ToString());
            //Console.WriteLine("----");
        }

        private static Guid ToGuid(string x)
        {
            var h = XxHashEx.Hash(x);
            var b = new byte[16];
            for (var i = 0; i < 8; ++i)
                b[i + 8] = (byte) ((h >> i) & 0xff);
            return new Guid(b);
        }

        private static Dictionary<string, string> ScanPackages(string[][] packages)
        {
            var d = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var package in packages)
            {
                var dir = $"packages/{package[0]}.{package[1]}/lib/";
                if (package.Length>=3)
                    dir += package[2] + "/";

                foreach (var dllFile in Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    var fn = Path.GetFileNameWithoutExtension(dllFile);
                    if (string.IsNullOrWhiteSpace(fn))
                        continue;

                    if (d.ContainsKey(fn))
                    {
                        Console.WriteLine("Duplicate dll mapping found for {0}, using {1}", fn, d[fn]);
                    }
                    else
                    {
                        Console.WriteLine("{0} -> {1}", fn, dllFile);
                        d[fn] = dllFile;
                    }
                }
            }
            return d;
        }

        private static void UpdatePackages(string[][] packages)
        {
            if (!Directory.Exists("packages"))
                Directory.CreateDirectory("packages");

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<packages>");
            foreach (var package in packages)
            {
                sb.AppendLine($"  <package id=\"{package[0]}\" version=\"{package[1]}\"/>");
            }
            sb.AppendLine("</packages>");
            var packagesConfig = sb.ToString();
            
            File.WriteAllText("packages.config", packagesConfig);
            ProgramRunner.Run(_nugetExe,"Install -Verbosity detailed -OutputDirectory packages",outputTextWriter:Console.Out,timeoutMilliseconds:WaitTimeMilliseconds);
            File.Delete("packages.config");
        }
    }
}