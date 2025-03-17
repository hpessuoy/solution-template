$ErrorActionPreference = "Stop"

function Show-Message
{
    param ([string]$message)

    # Get current time formatted as HH:mm:ss
    $time = Get-Date -Format "HH:mm:ss"

    # Format the log message
    $logMessage = "$time [INF] > $message"

    # Print the log message with coloration
    Write-Host "$time" -ForegroundColor Cyan -NoNewline
    Write-Host " [INF]" -ForegroundColor Blue -NoNewline
    Write-Host " > $message" -ForegroundColor Green
    Write-Host ""
}

if ($args[0] -eq "local")
{
    Show-Message "Building Locally"
    dotnet run --project build/Build.csproj -c Release -- $args[1..($args.Count)]
    exit 0;
}

Show-Message "Building in docker (use './build.ps1 local' to build without using docker)"

$GITHUB_TOKEN = $Env:GITHUB_TOKEN
$GITHUB_RUN_NUMBER = $Env:GITHUB_RUN_NUMBER

if ($null -eq $GITHUB_TOKEN -or $GITHUB_TOKEN -eq "")
{
    Write-Error "GITHUB_TOKEN environment variable empty or missing."
}

if ($null -eq $GITHUB_RUN_NUMBER -or $GITHUB_RUN_NUMBER -eq "")
{
    Write-Warning "GITHUB_RUN_NUMBER environment variable empty or missing."
}

$tag = "solution-template-build"

# Build the build environment image.
docker build `
 --build-arg GITHUB_TOKEN=$GITHUB_TOKEN `
 --build-arg GITHUB_RUN_NUMBER=$GITHUB_RUN_NUMBER `
 -f build.dockerfile `
 --tag $tag.

# Build inside build environment
docker run --rm --name $tag `
 -v /var/run/docker.sock:/var/run/docker.sock `
 -v $PWD/artifacts:/repo/artifacts `
 -v $PWD/temp:/repo/temp `
 --network host `
 -e NUGET_PACKAGES=/repo/temp/nuget-packages `
 $tag `
 dotnet run --project build/Build.csproj -c Release -- $args
 