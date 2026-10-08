param([switch]$Live)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
dotnet build 'tests\Workbench.Checks\Workbench.Checks.csproj' --configuration Release --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if ($Live) {
    Write-Warning 'This explicitly spends your existing Copilot allowance and simulates approvals for the bounded test candidate.'
    dotnet run --no-build --project 'tests\Workbench.Checks\Workbench.Checks.csproj' --configuration Release -- --live
} else {
    dotnet run --no-build --project 'tests\Workbench.Checks\Workbench.Checks.csproj' --configuration Release
}
if ($LASTEXITCODE -ne 0) { throw 'Acceptance checks failed; inspect the printed evidence directory and data\checks-* logs.' }
