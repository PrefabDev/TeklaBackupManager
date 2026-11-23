[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'TeklaBackupManager.csproj'
$output = Join-Path $PSScriptRoot 'artifacts/win-x64'
& dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
if (-not (Test-Path -LiteralPath (Join-Path $output 'TeklaBackupManager.exe'))) { throw 'Executable missing' }
