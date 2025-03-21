using System.Diagnostics.CodeAnalysis;
using System.IO;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Tools.SonarScanner;
using SimpleExec;

class Build : NukeBuild
{
    [Parameter] public readonly string ArtifactoryUsername;
    [Parameter] public readonly string ArtifactoryPassword;
    [Parameter] public readonly string ArtifactoryNugetSourceUrl;

    [Parameter] public readonly string AssemblySemVer;
    [Parameter] public readonly string AssemblySemFileVer;
    [Parameter] public readonly string InformationalVersion;

    [GitRepository] [Required] public readonly GitRepository GitRepository;
    [Solution] public readonly Solution Solution;

    const string ProjectName = "Api";

    Project Project => Solution.GetAllProjects("*").Single(p => ProjectName.Equals(p.Name, StringComparison.Ordinal));

    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    AbsolutePath BinaryArtifactsDirectory => ArtifactsDirectory / "app/bin";

    AbsolutePath InfraArtifactsDirectory => ArtifactsDirectory / "app/infra";

    AbsolutePath DeployArtifactsDirectory => ArtifactsDirectory / "app/deploy";

    AbsolutePath TestResultsDirectory => ArtifactsDirectory / "test-results";

    AbsolutePath IntegrationTestsResultDirectory => ArtifactsDirectory / "integration-test-results";

    AbsolutePath CoverageReportsDirectory => ArtifactsDirectory / "coverage";

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    public readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    IEnumerable<Project> UnitTestProjects => Solution.GetAllProjects("*.Tests.Unit");

    IEnumerable<Project> IntegrationTestProjects => Solution.GetAllProjects("*.Tests.Integration");

    const string GithubContainerRegistryNamespace = "ghcr.io/hpessuoy/solution-template";
    const string DeploymentContainerImageName = "solution-template-deployment";

    readonly string _deploymentImageTag = Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER") ??
                                          "GITHUB_RUN_NUMBER-not-available";

    Target ConfigureNuget => tgt => tgt
        .OnlyWhenStatic(() => !string.IsNullOrWhiteSpace(ArtifactoryNugetSourceUrl)
                              && !string.IsNullOrWhiteSpace(ArtifactoryUsername)
                              && !string.IsNullOrWhiteSpace(ArtifactoryPassword))
        .Executes(() =>
        {
            try
            {
                DotNetNuGetAddSource(n => n
                    .SetSource($"{ArtifactoryNugetSourceUrl}")
                    .SetName("Artifactory")
                    .SetUsername(ArtifactoryUsername)
                    .SetPassword(ArtifactoryPassword)
                    .EnableStorePasswordInClearText()
                );
            }
            catch
            {
                DotNet($"nuget update source Artifactory " +
                       $"--source {ArtifactoryNugetSourceUrl}/ " +
                       $"--username {ArtifactoryUsername} " +
                       $"--password {ArtifactoryPassword} " +
                       "--store-password-in-clear-text");
            }
        });

    Target Clean => d => d
        .Executes(() =>
        {
            BinaryArtifactsDirectory.CreateOrCleanDirectory();
            TestResultsDirectory.CreateOrCleanDirectory();
            IntegrationTestsResultDirectory.CreateOrCleanDirectory();
            CoverageReportsDirectory.CreateOrCleanDirectory();

            DotNetClean(settings => settings.SetProject(Solution)
                .SetConfiguration(Configuration)
                .SetVerbosity(DotNetVerbosity.quiet));

            // Solution.Directory.GlobDirectories("*/bin", "*/obj").DeleteDirectories();
        });

    Target Restore => d => d
        .DependsOn(ConfigureNuget)
        .DependsOn(Clean)
        .Executes(() =>
        {
            DotNetRestore(configurator => configurator
                .SetVerbosity(DotNetVerbosity.quiet));
        });

    [SuppressMessage("Style", "IDE0051:Remove unused private member", Justification = "Used via reflection")]
    Target Lint => d => d
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(configurator => configurator
                .SetConfiguration(Configuration)
                .SetNoRestore(true)
                .SetVerbosity(DotNetVerbosity.quiet)
                .SetRepositoryUrl(GitRepository.HttpsUrl)
                .AddWarningsAsErrors()
            );
        });

    Target Compile => d => d
        .DependsOn(Restore)
        .After(SonarStartCodeAnalysis)
        .Executes(() =>
        {
            DotNetBuild(configurator => configurator
                .SetConfiguration(Configuration)
                .SetNoRestore(true)
                .SetVerbosity(DotNetVerbosity.quiet)
                .SetRepositoryUrl(GitRepository.HttpsUrl)
            );
        });

    // We depend on Compile, but we don't specify it to avoid compiling twice as the CI runs Compile then UnitTests
    Target UnitTests => d => d
        .After(Compile)
        .Executes(() =>
        {
            DotNetTest(settings => settings.SetConfiguration(Configuration)
                .SetVerbosity(DotNetVerbosity.quiet)
                .SetResultsDirectory(TestResultsDirectory)
                .EnableCollectCoverage()
                .SetCoverletOutputFormat(CoverletOutputFormat.opencover)
                .SetLoggers("trx", "liquid.md")
                .CombineWith(UnitTestProjects, (settings, project) => settings
                    .SetProjectFile(project)
                    .SetCoverletOutput(CoverageReportsDirectory / $"{project.Name}.xml")));
        });

    Target IntegrationTests => d => d
        .DependsOn(Compile)
        .After(UnitTests)
        .Executes(() =>
        {
            DotNetTest(settings => settings.SetConfiguration(Configuration)
                .SetVerbosity(DotNetVerbosity.quiet)
                .SetResultsDirectory(IntegrationTestsResultDirectory)
                .EnableCollectCoverage()
                .SetCoverletOutputFormat(CoverletOutputFormat.opencover)
                .SetLoggers("trx", "liquid.md")
                .CombineWith(IntegrationTestProjects, (settings, project) => settings
                    .SetProjectFile(project)
                    .SetCoverletOutput(CoverageReportsDirectory / $"{project.Name}.xml")));
        });

    Target Publish => d => d
        .DependsOn(Compile)
        .After(UnitTests, IntegrationTests)
        .Executes(() =>
        {
            // .SetRuntime("linux-x64")  // Removing this for now as it's causing issues with the publishing: error : Manifest file not found
            // .EnablePublishSingleFile()  // Removing this for now as it's causing issues with the publishing: error : Manifest file not found
            DotNetPublish(settings => settings.SetProject(Project)
                .SetConfiguration(Configuration)
                .SetNoBuild(true)
                .SetOutput(BinaryArtifactsDirectory)
                .SetVerbosity(DotNetVerbosity.quiet)
                .SetRepositoryUrl(GitRepository.HttpsUrl)
                .SetAssemblyVersion(AssemblySemVer)
                .SetFileVersion(AssemblySemFileVer)
                .SetInformationalVersion(InformationalVersion));
        });

    Target PublishInfra => d => d
        .After(Publish)
        .Executes(() =>
        {
            // Publish Infrastructure
            Directory.CreateDirectory(InfraArtifactsDirectory);
        });

    Target PublishDeploy => d => d
        .After(PublishInfra)
        .Executes(() =>
        {
            // Publish Deployment
            Directory.CreateDirectory(DeployArtifactsDirectory);
        });

    Target BuildContainer => d => d
        .Before(PushDeploymentContainer)
        .After(Publish, PublishInfra, PublishDeploy)
        .Executes(() =>
        {
            DockerTasks.DockerBuild(settings => settings
                .SetFile(RootDirectory / "publish.dockerfile")
                .SetPath(RootDirectory)
                .SetTag(DeploymentContainerImageName));

            Command.Run("docker",
                $"tag {DeploymentContainerImageName}:latest {GithubContainerRegistryNamespace}/{DeploymentContainerImageName}:{_deploymentImageTag}");
        });

    Target PushDeploymentContainer => d => d
        .After(BuildContainer)
        .Executes(() =>
        {
            Command.Run("docker",
                $"push {GithubContainerRegistryNamespace}/{DeploymentContainerImageName}:{_deploymentImageTag}");
        });

    Target Default => d => d
        .DependsOn(UnitTests, IntegrationTests, Publish, PublishInfra, PublishDeploy, BuildContainer);

    [Parameter] [Secret] public readonly string SonarToken;
    [Parameter] [Secret] public readonly string SonarProjectKey;
    [Parameter] [Secret] public readonly string SonarOrganization;
    [Parameter] public readonly string SonarHostUrl;
    [Parameter] public readonly string ShortSha;
    
    const string SonarQubeScannerFramework = "net9.0";
    
    Target SonarStartCodeAnalysis => d => d
        .Before(Compile)
        .Before(SonarEndCodeAnalysis)
        .OnlyWhenStatic(() => !string.IsNullOrWhiteSpace(SonarToken))
        .Executes(() =>
        {
            
            SonarScannerTasks.SonarScannerBegin(settings =>
            {
                settings = settings
                    .SetServer(SonarHostUrl)
                    .SetToken(SonarToken)
                    .SetSourceEncoding("UTF-8")
                    .SetFramework(SonarQubeScannerFramework)
                    .SetOrganization(SonarOrganization)
                    .SetProjectKey(SonarProjectKey)
                    .SetVSTestReports(TestResultsDirectory / "*.trx")
                    .AddOpenCoverPaths(CoverageReportsDirectory / "*.xml");
                
                return settings;
            });
        });
//         .Executes(() =>
//         {
//             Command.Run("dotnet", "tool install --global dotnet-sonarscanner");
//             Command.Run("./.sonar/scanner/dotnet-sonarscanner",
//                 $"""
//                 begin \
//                 /k:"{SonarProjectKey}" \
//                 /v:"{ShortSha}" \
//                 /d:sonar.token="{SonarToken}" \
//                 /d:sonar.host.url="{SonarHostUrl}" \
//                 /d:sonar.cs.vstest.reportsPaths="{TestResultsDirectory}/**/*.trx" \
//                 /d:sonar.cs.vscoveragexml.reportsPaths="{CoverageReportsDirectory}/coverage/**/*.xml"
//                 """);
//         });
    
    Target SonarEndCodeAnalysis => d => d
        .After(Compile)
        .After(SonarStartCodeAnalysis)
        .OnlyWhenStatic(() => !string.IsNullOrWhiteSpace(SonarToken))
        .Executes(() =>
        {
            SonarScannerTasks.SonarScannerEnd(settings =>
            {
                settings = settings
                    .SetToken(SonarToken)
                    .SetFramework(SonarQubeScannerFramework);

                return settings;
            });
        });
//         .Executes(() =>
//         {
//             Command.Run("./.sonar/scanner/dotnet-sonarscanner",
//                 $"""
//                  end \
//                  /d:sonar.token="{SonarToken}" \
//                  """);
//         });

    public static int Main() => Execute<Build>(x => x.Default);
}
