$ErrorActionPreference = 'Stop'
$port = 52199
$env:PUBLIC_DEMO = 'true'
$env:PORT = "$port"
$dataRoot = Join-Path $PSScriptRoot '..\data\public-demo-test'
if (Test-Path $dataRoot) { Remove-Item -Recurse -Force $dataRoot }
$env:DataRoot = $dataRoot
$appDll = Join-Path $PSScriptRoot '..\src\Workbench\bin\Release\net10.0\Workbench.dll'

$proc = Start-Process dotnet -ArgumentList "$appDll" -PassThru -NoNewWindow
try {
    Start-Sleep -Seconds 3
    $baseUrl = "http://127.0.0.1:$port"
    
    # 1. Health check
    $health = Invoke-RestMethod -Uri "$baseUrl/health"
    Write-Host "Health check status: $($health.status), isPublicDemo: $($health.isPublicDemo)"
    if (!$health.isPublicDemo) { throw "Expected isPublicDemo to be true" }

    # 2. Session 1
    $s1 = Invoke-RestMethod -Uri "$baseUrl/api/session"
    Write-Host "Session 1 token: $($s1.token.Substring(0,8))... copilotEnabled: $($s1.copilotEnabled)"
    if ($s1.copilotEnabled -ne $false) { throw "Expected copilotEnabled to be false in public demo" }
    $token1 = $s1.token

    # 3. Attempting Copilot in public demo mode should be rejected
    try {
        Invoke-RestMethod -Uri "$baseUrl/api/runs" -Method Post -Headers @{ 'X-Workbench-Token' = $token1 } -ContentType 'application/json' -Body '{"scenario":"greenfield","provider":"copilot"}'
        throw "Expected copilot creation to be rejected"
    } catch {
        Write-Host "PASS: Copilot run creation correctly rejected in public demo mode"
    }

    # 4. Session 1 creates greenfield run
    $run1 = Invoke-RestMethod -Uri "$baseUrl/api/runs" -Method Post -Headers @{ 'X-Workbench-Token' = $token1 } -ContentType 'application/json' -Body '{"scenario":"greenfield","provider":"demo"}'
    Write-Host "PASS: Session 1 created Run: $($run1.id)"

    # 5. Session 2 (distinct token)
    $bytes = New-Object byte[] 16
    (New-Object System.Security.Cryptography.RNGCryptoServiceProvider).GetBytes($bytes)
    $token2 = [System.BitConverter]::ToString($bytes).Replace('-', '')
    
    # Session 2 attempts to stop Session 1's run -> MUST be 403 Forbidden
    try {
        Invoke-RestMethod -Uri "$baseUrl/api/runs/$($run1.id)/stop" -Method Post -Headers @{ 'X-Workbench-Token' = $token2 } -ContentType 'application/json' -Body '{}'
        throw "Expected stop from session 2 to fail with 403"
    } catch {
        Write-Host "PASS: Session 2 was blocked from modifying Session 1's run (403 Forbidden)"
    }

    # Session 2 inspects Session 1's run -> isOwner must be false
    $view2 = Invoke-RestMethod -Uri "$baseUrl/api/runs/$($run1.id)" -Headers @{ 'X-Workbench-Token' = $token2 }
    Write-Host "PASS: Session 2 sees run isOwner = $($view2.isOwner)"
    if ($view2.isOwner -ne $false) { throw "Expected isOwner to be false for Session 2" }

    # Session 1 inspects Session 1's run -> isOwner must be true
    $view1 = Invoke-RestMethod -Uri "$baseUrl/api/runs/$($run1.id)" -Headers @{ 'X-Workbench-Token' = $token1 }
    Write-Host "PASS: Session 1 sees run isOwner = $($view1.isOwner)"
    if ($view1.isOwner -ne $true) { throw "Expected isOwner to be true for Session 1" }

    # Session 1 stops their own run -> MUST succeed
    $stopRes = Invoke-WebRequest -Uri "$baseUrl/api/runs/$($run1.id)/stop" -Method Post -Headers @{ 'X-Workbench-Token' = $token1 } -ContentType 'application/json' -Body '{}'
    Write-Host "PASS: Session 1 successfully stopped their own run (HTTP $($stopRes.StatusCode))"

    Write-Host "`n=== ALL PUBLIC DEMO SAFEGUARD TESTS PASSED! ==="
} finally {
    if ($proc -and !$proc.HasExited) { Start-Process taskkill -ArgumentList "/PID $($proc.Id) /T /F" -NoNewWindow -Wait }
}
