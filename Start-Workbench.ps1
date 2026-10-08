param([int]$Port = 5187)
$ErrorActionPreference = 'Stop'
if ($Port -lt 1024 -or $Port -gt 65535) { throw 'Use a port between 1024 and 65535.' }
Set-Location -LiteralPath (Join-Path $PSScriptRoot 'src\Workbench')
$env:WorkbenchUrl = "http://127.0.0.1:$Port"
Write-Host "Local dashboard: $env:WorkbenchUrl"
Write-Host 'Default provider: deterministic demo. Live Copilot is experimental and optional.'
dotnet run --configuration Release
if ($LASTEXITCODE -ne 0) { throw "Workbench exited with $LASTEXITCODE." }
