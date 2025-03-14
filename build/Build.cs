using System.IO;
using Nuke.Common.Tools.Docker;
using SimpleExec;

class Build : NukeBuild
{
    [Parameter] readonly string ArtifactoryUsername;
    [Parameter] readonly string ArtifactoryPassword;
    [Parameter] readonly string ArtifactoryNugetSourceUrl;

    [Parameter] readonly string AssemblySemVer;
    [Parameter] readonly string AssemblySemFileVer;
    [Parameter] readonly string InformationalVersion;

    [GitRepository] [Required] readonly GitRepository GitRepository;
    [Solution] readonly Solution Solution;

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
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    IEnumerable<Project> UnitTestProjects => Solution.GetAllProjects("*.Tests.Unit");
    IEnumerable<Project> IntegrationTestProjects => Solution.GetAllProjects("*.Tests.Integration");

    const string GithubContainerRegistryNamespace = "ghcr.io/youcefmzg/solution-template";
    const string DeploymentContainerImageName = "solution-template-deployment";

    readonly string DeploymentImageTag = Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER") ??
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
            // TODO: setup serilog properly
            // Serilog.Log.Information($"Cleaning bin artifact");
            BinaryArtifactsDirectory.CreateOrCleanDirectory();
            TestResultsDirectory.CreateOrCleanDirectory();
            IntegrationTestsResultDirectory.CreateOrCleanDirectory();
            CoverageReportsDirectory.CreateOrCleanDirectory();

            DotNetClean(settings => settings.SetProject(Solution)
                .SetConfiguration(Configuration)
                .SetVerbosity(DotNetVerbosity.quiet));

            // Solution.Directory.GlobDirectories("*/bin", "*/obj").DeleteDirectories();
            // Serilog.Log.Information($"Cleaning bin and obj directories");
        });

    Target Restore => d => d
        .DependsOn(ConfigureNuget)
        .DependsOn(Clean)
        .Executes(() =>
        {
            DotNetRestore(configurator => configurator
                .SetVerbosity(DotNetVerbosity.quiet));
        });

    Target Compile => d => d
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(configurator => configurator
                .SetConfiguration(Configuration)
                .SetNoRestore(true)
                .SetVerbosity(DotNetVerbosity.quiet)
                .SetRepositoryUrl(GitRepository.HttpsUrl)
            );
        });

    Target UnitTests => d => d
        .DependsOn(Compile)
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
            DotNetPublish(settings => settings.SetProject(Project)
                .SetConfiguration(Configuration)
                // .SetRuntime("linux-x64")  // Removing this for now as it's causing issues with the publishing: error : Manifest file not found
                .SetNoBuild(true)
                .SetOutput(BinaryArtifactsDirectory)
                // .EnablePublishSingleFile()  // Removing this for now as it's causing issues with the publishing: error : Manifest file not found
                .SetVerbosity(DotNetVerbosity.quiet)
                .SetRepositoryUrl(GitRepository.HttpsUrl)
                .SetAssemblyVersion(AssemblySemVer)
                .SetFileVersion(AssemblySemFileVer)
                .SetInformationalVersion(InformationalVersion));
        });

    Target PublishInfra => d => d
        .Unlisted()
        .After(Publish)
        .Executes(() =>
        {
            // Publish Infrastructure
            Directory.CreateDirectory(InfraArtifactsDirectory);
        });

    Target PublishDeploy => d => d
        .Unlisted()
        .After(PublishInfra)
        .Executes(() =>
        {
            // Publish Deployment
            Directory.CreateDirectory(DeployArtifactsDirectory);
        });

    Target BuildContainer => d => d
        .Unlisted()
        .After(Publish, PublishInfra, PublishDeploy)
        .Executes(() =>
        {
            DockerTasks.DockerBuild(settings => settings
                .SetFile(RootDirectory / "publish.dockerfile")
                .SetPath(RootDirectory)
                .SetTag(DeploymentContainerImageName));

            Command.Run("docker",
                $"tag {DeploymentContainerImageName}:latest {GithubContainerRegistryNamespace}/{DeploymentContainerImageName}:{DeploymentImageTag}");
        });

    Target PushDeploymentContainer => d => d
        .After(BuildContainer)
        .Executes(() =>
        {
            Command.Run("docker", $"push {GithubContainerRegistryNamespace}/{DeploymentContainerImageName}:{DeploymentImageTag}");
        });

    Target Default => d => d
        .DependsOn(UnitTests, IntegrationTests, Publish, PublishInfra, PublishDeploy, BuildContainer);

    public static int Main() => Execute<Build>(x => x.Default);
}
