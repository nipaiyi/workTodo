param([switch]$Publish, [switch]$Verify, [switch]$SkipRestore)

$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $projectDirectory
$localDotnet = Join-Path $projectDirectory '.tools/dotnet/dotnet.exe'
$dotnetCommand = if (Test-Path $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $projectDirectory '.tools/cli'
$env:NUGET_PACKAGES = Join-Path $projectDirectory '.tools/packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'
$env:DOTNET_ROOT = Split-Path $dotnetCommand -Parent

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & $dotnetCommand @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}

if (-not $SkipRestore) {
    Invoke-Dotnet -Arguments @('restore', 'DeskTodo.slnx', '--configfile', 'NuGet.Config', '--disable-parallel')
}
Invoke-Dotnet -Arguments @('build', 'DeskTodo.slnx', '-c', 'Release', '--no-restore', '-m:1', '-p:UseSharedCompilation=false')
Invoke-Dotnet -Arguments @('tests/DeskTodo.Tests/bin/Release/net10.0/DeskTodo.Tests.dll', 'artifacts/core-tests')

$appPath = Join-Path $projectDirectory 'src/DeskTodo/bin/Release/net10.0-windows/DeskTodo.exe'
if ($Publish) {
    if (-not $SkipRestore) {
        Invoke-Dotnet -Arguments @('restore', 'src/DeskTodo/DeskTodo.csproj', '-r', 'win-x64', '--configfile', 'NuGet.Config', '--disable-parallel')
    }
    Invoke-Dotnet -Arguments @('publish', 'src/DeskTodo/DeskTodo.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore', '-o', 'dist/DeskTodo', '-m:1', '-p:UseSharedCompilation=false')
    $appPath = Join-Path $projectDirectory 'dist/DeskTodo/DeskTodo.exe'
}
if ($Verify) {
    $testDirectory = Join-Path $projectDirectory ('artifacts/ui-' + [Guid]::NewGuid().ToString('N'))
    $testProcess = Start-Process -FilePath $appPath -ArgumentList '--verify', '--data-dir', ('"' + $testDirectory + '"') -WindowStyle Hidden -PassThru
    if (-not $testProcess.WaitForExit(30000)) { $testProcess.Kill(); throw 'UI verification timed out.' }
    if ($testProcess.ExitCode -ne 0 -or -not (Test-Path (Join-Path $testDirectory 'ui-results.json'))) {
        Get-Content -LiteralPath (Join-Path $testDirectory 'errors.log') -ErrorAction SilentlyContinue
        throw 'UI verification failed.'
    }
    Get-Content -LiteralPath (Join-Path $testDirectory 'ui-results.json')
    Write-Output "UI screenshots: $testDirectory"
}
Write-Output "Application: $appPath"
