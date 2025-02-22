using Nuke.Common;
using Nuke.Common.CI.Jenkins;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.AzureKeyVault;
using Nuke.Common.Tools.Coverlet;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.ReportGenerator;
using Nuke.Common.Tools.Teams;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using Nuke.GitHub;
using Nuke.WebDocu;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Xml.XPath;
using static Nuke.Common.ChangeLog.ChangelogTasks;
using static Nuke.Common.IO.XmlTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.Npm.NpmTasks;
using static Nuke.Common.Tools.ReportGenerator.ReportGeneratorTasks;
using static Nuke.GitHub.ChangeLogExtensions;
using static Nuke.GitHub.GitHubTasks;
using static Nuke.WebDocu.WebDocuTasks;

class Build : NukeBuild
{
    public static int Main () => Execute<Build>(x => x.Compile);

    [AzureKeyVaultConfiguration(
            BaseUrlParameterName = nameof(KeyVaultBaseUrl),
            ClientIdParameterName = nameof(KeyVaultClientId),
            ClientSecretParameterName = nameof(KeyVaultClientSecret),
            TenantIdParameterName = nameof(KeyVaultTenantId))]
    readonly AzureKeyVaultConfiguration KeyVaultSettings;

    [AzureKeyVault] readonly AzureKeyVault KeyVault;

    [Parameter] readonly string KeyVaultBaseUrl;
    [Parameter] readonly string KeyVaultClientId;
    [Parameter] readonly string KeyVaultClientSecret;
    [Parameter] readonly string KeyVaultTenantId;

    [GitVersion(Framework = "net6.0")] readonly GitVersion GitVersion;
    [GitRepository] readonly GitRepository GitRepository;

    [AzureKeyVaultSecret] readonly string DocuBaseUrl;
    [AzureKeyVaultSecret] readonly string DanglPublicFeedSource;
    [AzureKeyVaultSecret] readonly string FeedzAccessToken;
    [AzureKeyVaultSecret] readonly string NuGetApiKey;
    [AzureKeyVaultSecret("LightQuery-DocuApiKey")] readonly string DocuApiKey;
    [AzureKeyVaultSecret] readonly string GitHubAuthenticationToken;
    [AzureKeyVaultSecret] readonly string DanglCiCdTeamsWebhookUrl;

    [Parameter] readonly string Configuration = IsLocalBuild ? "Debug" : "Release";

    [Solution("LightQuery.sln")] readonly Solution Solution;
    AbsolutePath SolutionDirectory => Solution.Directory;
    AbsolutePath OutputDirectory => SolutionDirectory / "output";
    AbsolutePath SourceDirectory => SolutionDirectory / "src";

    string DocFxFile => SolutionDirectory / "docfx.json";

    string ChangeLogFile => RootDirectory / "CHANGELOG.md";

    protected override void OnTargetFailed(string target)
    {
        if (IsServerBuild)
        {
            SendTeamsMessage("Build Failed", $"Target {target} failed for LightQuery, " +
                        $"Branch: {GitRepository.Branch}", true);
        }
    }

    void SendTeamsMessage(string title, string message, bool isError)
    {
        if (!string.IsNullOrWhiteSpace(DanglCiCdTeamsWebhookUrl))
        {
            var themeColor = isError ? "f44336" : "00acc1";
            TeamsTasks
                .SendTeamsMessage(m => m
                    .SetTitle(title)
                    .SetText(message)
                    .SetThemeColor(themeColor),
                    DanglCiCdTeamsWebhookUrl);
        }
    }

    Target Clean => _ => _
        .Executes(() =>
        {
            (SourceDirectory / "LightQuery").GlobDirectories("**/bin", "**/obj").ForEach(d => d.DeleteDirectory());
            (SourceDirectory / "LightQuery.Client").GlobDirectories("**/bin", "**/obj").ForEach(d => d.DeleteDirectory());
            (SourceDirectory / "LightQuery.EntityFrameworkCore").GlobDirectories("**/bin", "**/obj").ForEach(d => d.DeleteDirectory());
            (SourceDirectory / "LightQuery.Shared").GlobDirectories("**/bin", "**/obj").ForEach(d => d.DeleteDirectory());
            (SourceDirectory / "LightQuery.NSwag").GlobDirectories("**/bin", "**/obj").ForEach(d => d.DeleteDirectory());
            (SourceDirectory / "LightQuery.Swashbuckle").GlobDirectories("**/bin", "**/obj").ForEach(d => d.DeleteDirectory());
            (RootDirectory / "test").GlobDirectories("**/bin", "**/obj").ForEach(d => d.DeleteDirectory());
            OutputDirectory.CreateOrCleanDirectory();
        });

    Target Restore => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {
            DotNetRestore();
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(x => x
                .SetConfiguration(Configuration)
                .EnableNoRestore()
                .SetProcessArgumentConfigurator(a => a.Add("-nodereuse:false"))
                .SetFileVersion(GitVersion.AssemblySemFileVer)
                .SetAssemblyVersion($"{GitVersion.Major}.{GitVersion.Minor}.{GitVersion.Patch}.0")
                .SetInformationalVersion(GitVersion.InformationalVersion));
        });

    private Target Pack => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            var changeLog = GetCompleteChangeLog(ChangeLogFile)
                .EscapeStringPropertyForMsBuild();

            DotNetPack(x => x
                .SetConfiguration(Configuration)
                .SetPackageReleaseNotes(changeLog)
                .EnableNoBuild()
                .SetOutputDirectory(OutputDirectory)
                .SetVersion(GitVersion.NuGetVersion));
        });

    Target Coverage => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            var testProjects = (SolutionDirectory / "test").GlobFiles("**/*.csproj")
                .Select(t => t.ToString())
                .Where(t => !t.EndsWith("LightQuery.IntegrationTestsServer.csproj"))
                .ToList();

            var hasFailedTests = false;
            try
            {
                DotNetTest(c => c
                    .SetDataCollector("XPlat Code Coverage")
                    .SetResultsDirectory(OutputDirectory)
                    .AddRunSetting("DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format", "cobertura")
                    .AddRunSetting("DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include", "[LightQuery*]*")
                    .AddRunSetting("DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Exclude", "[*Tests*]*")
                    .AddRunSetting("DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ExcludeByAttribute", "Obsolete,GeneratedCodeAttribute,CompilerGeneratedAttribute")
                    .EnableNoBuild()
                    .SetTestAdapterPath(".")
                    .CombineWith(cc => testProjects
                        .SelectMany(testProject =>
                        {
                            var projectDirectory = Path.GetDirectoryName(testProject);
                            var projectName = Path.GetFileNameWithoutExtension(testProject);
                            var targetFrameworks = GetTestFrameworksForProjectFile(testProject);
                            return targetFrameworks
                            .Select(targetFramework => cc
                                .SetProjectFile(testProject)
                                .SetFramework(targetFramework)
                                .SetLoggers($"xunit;LogFilePath={OutputDirectory / projectName}_testresults-{targetFramework}.xml")
                                .SetCoverletOutput($"{OutputDirectory / projectName}_coverage.xml")
                                .SetProcessArgumentConfigurator(a => a
                                    .Add("-nodereuse:false")
                                    // This part is required to ensure that xUnit isn't using app domains or shadow copying, since coverlet
                                    // needs to modify the dlls to collect coverage. See here for more information:
                                    // https://github.com/coverlet-coverage/coverlet/issues/347
                                    // Also, this argument must be at the end.
                                    .Add("-- RunConfiguration.DisableAppDomain=true")));
                        })),
                            degreeOfParallelism: Environment.ProcessorCount,
                            completeOnFailure: true);
            }
            catch
            {
                hasFailedTests = true;
            }

            PrependFrameworkToTestresults();

            // Merge coverage reports, otherwise they might not be completely
            // picked up by Jenkins
            ReportGenerator(c => c
                .SetFramework("net6.0")
                .SetReports(OutputDirectory / "**/*cobertura.xml")
                .SetTargetDirectory(OutputDirectory)
                .SetReportTypes(ReportTypes.Cobertura));

            MakeSourceEntriesRelativeInCoberturaFormat(OutputDirectory / "Cobertura.xml");

            if (hasFailedTests)
            {
                Assert.Fail("Some tests have failed");
            }
        });

    private void MakeSourceEntriesRelativeInCoberturaFormat(AbsolutePath coberturaReportPath)
    {
        var originalText = coberturaReportPath.ReadAllText();
        var xml = XDocument.Parse(originalText);

        var xDoc = XDocument.Load(coberturaReportPath);

        var sourcesEntry = xDoc
            .Root
            .Elements()
            .Where(e => e.Name.LocalName == "sources")
            .Single();

        string basePath;
        if (sourcesEntry.HasElements)
        {
            var elements = sourcesEntry.Elements().ToList();
            basePath = elements
                .Select(e => e.Value)
                .OrderBy(p => p.Length)
                .First();
            foreach (var element in elements)
            {
                if (element.Value != basePath)
                {
                    element.Remove();
                }
            }
        }
        else
        {
            basePath = sourcesEntry.Value;
        }

        Serilog.Log.Information($"Normalizing Cobertura report to base path: \"{basePath}\"");

        var filenameAttributes = xDoc
            .Root
            .Descendants()
            .Where(d => d.Attributes().Any(a => a.Name.LocalName == "filename"))
            .Select(d => d.Attributes().First(a => a.Name.LocalName == "filename"));
        foreach (var filenameAttribute in filenameAttributes)
        {
            if (filenameAttribute.Value.StartsWith(basePath))
            {
                filenameAttribute.Value = filenameAttribute.Value.Substring(basePath.Length);
            }
        }

        xDoc.Save(coberturaReportPath);
    }

    IEnumerable<string> GetTestFrameworksForProjectFile(string projectFile)
    {
        var targetFrameworks = XmlPeek(projectFile, "//Project/PropertyGroup//TargetFrameworks")
            .Concat(XmlPeek(projectFile, "//Project/PropertyGroup//TargetFramework"))
            .Distinct()
            .SelectMany(f => f.Split(';'))
            .Distinct();
        return targetFrameworks;
    }

    Target Push => _ => _
        .DependsOn(Pack)
        .Requires(() => DanglPublicFeedSource)
        .Requires(() => FeedzAccessToken)
        .Requires(() => NuGetApiKey)
        .Requires(() => Configuration.EqualsOrdinalIgnoreCase("Release"))
        .OnlyWhenDynamic(() => Jenkins.Instance == null
            || Jenkins.Instance.ChangeId == null)
        .Executes(() =>
        {
            var packages = OutputDirectory.GlobFiles("*.nupkg")
                .Select(p => p.ToString())
                .Where(x => !x.EndsWith("symbols.nupkg"))
                .ToList();
            Assert.NotEmpty(packages);
            packages
                .ForEach(x =>
                {
                    DotNetNuGetPush(s => s
                        .SetTargetPath(x)
                        .SetSource(DanglPublicFeedSource)
                        .SetApiKey(FeedzAccessToken));

                    if (GitVersion.BranchName.Equals("master") || GitVersion.BranchName.Equals("origin/master"))
                    {
                        // Stable releases are published to NuGet
                        DotNetNuGetPush(s => s
                            .SetTargetPath(x)
                            .SetSource("https://api.nuget.org/v3/index.json")
                            .SetApiKey(NuGetApiKey));
                    }
                });

            SendTeamsMessage("New Release", $"New release available for LightQuery: {GitVersion.NuGetVersion}", false);
        });

    Target BuildDocFxMetadata => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            var environmentVariables = EnvironmentInfo.Variables.ToDictionary();
            environmentVariables.Add("DOCFX_SOURCE_BRANCH_NAME", GitVersion.BranchName);
            var docFxPath = NuGetToolPathResolver.GetPackageExecutable("docfx", "tools/net8.0/any/docfx.dll");
            DotNet($"{docFxPath} metadata {DocFxFile}", environmentVariables: environmentVariables);
        });

    Target BuildDocumentation => _ => _
        .DependsOn(Clean)
        .DependsOn(BuildDocFxMetadata)
        .Executes(() =>
        {
            // Using README.md as index.md
            if (File.Exists(SolutionDirectory / "index.md"))
            {
                File.Delete(SolutionDirectory / "index.md");
            }

            File.Copy(SolutionDirectory / "README.md", SolutionDirectory / "index.md");

            var environmentVariables = EnvironmentInfo.Variables.ToDictionary();
            environmentVariables.Add("DOCFX_SOURCE_BRANCH_NAME", GitVersion.BranchName);
            var docFxPath = NuGetToolPathResolver.GetPackageExecutable("docfx", "tools/net8.0/any/docfx.dll");
            DotNet($"{docFxPath} {DocFxFile}", environmentVariables: environmentVariables);

            File.Delete(SolutionDirectory / "index.md");
            Directory.Delete(SolutionDirectory / "lightquery", true);
            Directory.Delete(SolutionDirectory / "client", true);
            Directory.Delete(SolutionDirectory / "shared", true);
            Directory.Delete(SolutionDirectory / "efcore", true);
        });

    Target UploadDocumentation => _ => _
        .DependsOn(Push) // To have a relation between pushed package version and published docs version
        .DependsOn(BuildDocumentation)
        .Requires(() => DocuApiKey)
        .Requires(() => DocuBaseUrl)
        .OnlyWhenDynamic(() => Jenkins.Instance == null
            || Jenkins.Instance.ChangeId == null)
        .Executes(() =>
        {
             var changeLog = GetCompleteChangeLog(ChangeLogFile);

            WebDocu(s => s
                .SetDocuBaseUrl(DocuBaseUrl)
                .SetDocuApiKey(DocuApiKey)
                .SetMarkdownChangelog(changeLog)
                .SetSourceDirectory(OutputDirectory / "docs")
                .SetVersion(GitVersion.NuGetVersion)
            );
        });

    Target PublishGitHubRelease => _ => _
        .DependsOn(Pack)
        .Requires(() => GitHubAuthenticationToken)
        .OnlyWhenDynamic(() => GitVersion.BranchName.Equals("master") || GitVersion.BranchName.Equals("origin/master"))
        .Executes(async () =>
        {
            var releaseTag = $"v{GitVersion.MajorMinorPatch}";

            var changeLogSectionEntries = ExtractChangelogSectionNotes(ChangeLogFile);
            var latestChangeLog = changeLogSectionEntries
                .Aggregate((c, n) => c + Environment.NewLine + n);
            var completeChangeLog = $"## {releaseTag}" + Environment.NewLine + latestChangeLog;

            var repositoryInfo = GetGitHubRepositoryInfo(GitRepository);
            var nuGetPackages = OutputDirectory.GlobFiles("*.nupkg").Select(f => f.ToString()).ToArray();
            Assert.NotEmpty(nuGetPackages);

            await PublishRelease(x => x
                    .SetArtifactPaths(nuGetPackages)
                    .SetCommitSha(GitVersion.Sha)
                    .SetReleaseNotes(completeChangeLog)
                    .SetRepositoryName(repositoryInfo.repositoryName)
                    .SetRepositoryOwner(repositoryInfo.gitHubOwner)
                    .SetTag(releaseTag)
                    .SetToken(GitHubAuthenticationToken));
        });

    void PrependFrameworkToTestresults()
    {
        var testResults = OutputDirectory.GlobFiles("*testresults*.xml").ToList();
        Serilog.Log.Debug($"Found {testResults.Count} test result files on which to append the framework.");
        foreach (var testResultFile in testResults)
        {
            var frameworkName = GetFrameworkNameFromFilename(testResultFile);
            var xDoc = XDocument.Load(testResultFile);

            foreach (var testType in ((IEnumerable)xDoc.XPathEvaluate("//test/@type")).OfType<XAttribute>())
            {
                testType.Value = frameworkName + "+" + testType.Value;
            }

            foreach (var testName in ((IEnumerable)xDoc.XPathEvaluate("//test/@name")).OfType<XAttribute>())
            {
                testName.Value = frameworkName + "+" + testName.Value;
            }

            xDoc.Save(testResultFile);
        }

        // Merge all the results to a single file
        // The "run-time" attributes of the single assemblies is ensured to be unique for each single assembly by this test,
        // since in Jenkins, the format is internally converted to JUnit. Aterwards, results with the same timestamps are
        // ignored. See here for how the code is translated to JUnit format by the Jenkins plugin:
        // https://github.com/jenkinsci/xunit-plugin/blob/d970c50a0501f59b303cffbfb9230ba977ce2d5a/src/main/resources/org/jenkinsci/plugins/xunit/types/xunitdotnet-2.0-to-junit.xsl#L75-L79
        Serilog.Log.Debug("Updating \"run-time\" attributes in assembly entries to prevent Jenkins to treat them as duplicates");
        var firstXdoc = XDocument.Load(testResults[0]);
        var runtime = DateTime.Now;
        var firstAssemblyNodes = firstXdoc.Root.Elements().Where(e => e.Name.LocalName == "assembly");
        foreach (var assemblyNode in firstAssemblyNodes)
        {
            assemblyNode.SetAttributeValue("run-time", $"{runtime:HH:mm:ss}");
            runtime = runtime.AddSeconds(1);
        }
        for (var i = 1; i < testResults.Count; i++)
        {
            var xDoc = XDocument.Load(testResults[i]);
            var assemblyNodes = xDoc.Root.Elements().Where(e => e.Name.LocalName == "assembly");
            foreach (var assemblyNode in assemblyNodes)
            {
                assemblyNode.SetAttributeValue("run-time", $"{runtime:HH:mm:ss}");
                runtime = runtime.AddSeconds(1);
            }
            firstXdoc.Root.Add(assemblyNodes);
        }

        firstXdoc.Save(OutputDirectory / "testresults.xml");
        testResults.ForEach(d => d.DeleteFile());
    }

    string GetFrameworkNameFromFilename(string filename)
    {
        var name = Path.GetFileName(filename);
        name = name.Substring(0, name.Length - ".xml".Length);
        var startIndex = name.LastIndexOf('-');
        name = name.Substring(startIndex + 1);
        return name;
    }

    Target NgLibraryTest => _ => _
        .Executes(() =>
        {
            var ngAppDir = SourceDirectory / "ng-lightquery";
            (ngAppDir / "dist").DeleteDirectory();
            (ngAppDir / "coverage").DeleteDirectory();
            (ngAppDir / "karma-results.xml").DeleteFile();

            Npm("ci", ngAppDir);
            Npm("run test:ci", ngAppDir);
        });

    Target NgLibraryPublish => _ => _
        .OnlyWhenDynamic(() => Jenkins.Instance == null
            || Jenkins.Instance.ChangeId == null)
        .Executes(() =>
        {
            var ngAppDir = SourceDirectory / "ng-lightquery";
            var ngLibraryDir = ngAppDir / "dist" / "ng-lightquery";
            (ngAppDir / "dist").DeleteDirectory();

            Npm("ci", ngAppDir);

            Npm("run buildLibrary", ngAppDir);

            Npm($"version {GitVersion.NuGetVersion}", ngLibraryDir);
            var srcReadmePath = RootDirectory / "README.md";
            var destReadmePath = ngLibraryDir / "README.md";
            if (File.Exists(destReadmePath))
            {
                File.Delete(destReadmePath);
            }
            File.Copy(srcReadmePath, destReadmePath);

            var npmTag = GitVersion.BranchName.Equals("master") || GitVersion.BranchName.Equals("origin/master")
            ? "latest"
            : "next";

            Npm($"publish --tag={npmTag}", ngLibraryDir);
        });
}
