param([string]$OutputPath = (Join-Path $PSScriptRoot 'SchwabAgenticWorkbench-Submission.zip'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PSScriptRoot)
if (Test-Path -LiteralPath $OutputPath) { throw 'Output already exists; choose a new filename.' }
Add-Type -AssemblyName System.IO.Compression
$archive = [IO.Compression.ZipFile]::Open($OutputPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    $files = Get-ChildItem -LiteralPath $root -File -Recurse |
        Where-Object {
            $relative = [IO.Path]::GetRelativePath($root, $_.FullName)
            $relative -notmatch '(^|[\\/])(\.git|bin|obj|data|node_modules|\.vs)([\\/]|$)' -and
            $_.Extension -notin @('.zip','.db','.log') -and $_.Name -ne '.env'
        }
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\','/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative)
    }
} finally { $archive.Dispose() }
Write-Host "Local source package created: $OutputPath"
Write-Host 'Excluded data, credentials, caches, generated binaries and Git metadata. Nothing was uploaded.'
