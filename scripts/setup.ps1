$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path $PSScriptRoot -Parent
$toolsDirectory = Join-Path $projectDirectory '.tools'
New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
$installerPath = Join-Path $toolsDirectory 'dotnet-install.ps1'
Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installerPath
& $installerPath -Channel 10.0 -InstallDir (Join-Path $toolsDirectory 'dotnet') -NoPath
if (-not (Test-Path (Join-Path $toolsDirectory 'dotnet/dotnet.exe'))) { throw 'SDK installation did not complete.' }
Write-Output 'Local SDK ready. Run: ./scripts/build.ps1 -Publish -Verify'
