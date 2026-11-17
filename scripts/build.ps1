param([string]$OutputDirectory = '', [string]$RuntimeVersion = '8.0.32')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts/win-x64' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
& dotnet publish (Join-Path $root 'ListeningSubtitles.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:ContinuousIntegrationBuild=true "-p:RuntimeFrameworkVersion=$RuntimeVersion" -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'ListeningSubtitles publish failed.' }
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $root 'README.en.md') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $OutputDirectory -Recurse -Force
Write-Output (Join-Path $OutputDirectory 'ListeningSubtitles.exe')
