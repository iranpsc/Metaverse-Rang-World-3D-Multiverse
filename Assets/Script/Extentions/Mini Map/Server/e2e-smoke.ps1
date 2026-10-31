#requires -version 5.1
<#
.SYNOPSIS
  E2E smoke test for the meta-environment JSON server (minimap + daynight API).
.DESCRIPTION
  1. Checks {BaseUrl}/health; starts `node server.js` itself when nothing answers.
  2. Runs every Phase-B curl case (minimap PUT/GET/404/traversal, daynight owner
     PUT/GET, strict REQUIRE_OWNER 403/200 round on a second port).
  3. Prints a PASS/FAIL table and exits 1 when any case failed.
  Only what it needs is written to $env:TEMP (two small JSON bodies); the case
  data lives under environments/e2e_room and is safe to keep or delete.
.PARAMETER BaseUrl
  Main server root. Default http://127.0.0.1:8788 (the documented Unity BaseUrl).
.PARAMETER StrictPort
  Second port used for the REQUIRE_OWNER=true server (default 8789).
.PARAMETER KeepServerRunning
  When the script itself had to start the main server, leave it running after
  the test (handy right before a Unity play-mode session).
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File e2e-smoke.ps1
  npm run test:e2e
#>
param(
    [string]$BaseUrl = 'http://127.0.0.1:8788',
    [int]$StrictPort = 8789,
    [switch]$KeepServerRunning
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$results = New-Object System.Collections.Generic.List[object]

function Invoke-CurlText {
    param([string[]]$CurlArgs)
    $raw = (& curl.exe -s @CurlArgs -w "`n@@@STATUS:%{http_code}") -join "`n"
    $idx = $raw.LastIndexOf('@@@STATUS:')
    if ($idx -lt 0) {
        return [pscustomobject]@{ Status = 0; Body = ('CURL-FAILED: ' + (($raw -join ' ') -replace '\s+', ' ')) }
    }
    $status = 0
    [void][int]::TryParse($raw.Substring($idx + 10).Trim(), [ref]$status)
    $body = $raw.Substring(0, $idx).Trim()
    [pscustomobject]@{ Status = $status; Body = $body }
}

function Test-Case {
    param(
        [string]$Name, [string]$Method, [string]$Path,
        [string[]]$CurlArgs, [string[]]$ExpectStatus, [string[]]$ExpectBody
    )
    $r = Invoke-CurlText $CurlArgs
    $fails = @()
    if ($ExpectStatus.Count -gt 0 -and $ExpectStatus -notcontains [string]$r.Status) {
        $fails += "status=$($r.Status) (expected $($ExpectStatus -join '|'))"
    }
    foreach ($needle in $ExpectBody) {
        if ($r.Body -notmatch [regex]::Escape($needle)) { $fails += "body missing '$needle'" }
    }
    $results.Add([pscustomobject]@{
        Name   = $Name
        Call   = "$Method $Path"
        Result = if ($fails.Count -eq 0) { 'PASS' } else { 'FAIL' }
        Status = $r.Status
        Note   = if ($fails.Count -eq 0) { ($r.Body -replace '\s+', ' ') } else { ($fails -join '; ') }
    })
}

# ------------------------------------------------------------------ bodies
$bodyMm = Join-Path $env:TEMP 'e2e_mm_put.json'
$bodyDn = Join-Path $env:TEMP 'e2e_dn_put.json'
Set-Content -Path $bodyMm -Value '{"version":1,"citizenId":"ignored","radarEnabled":true,"zoomOrOrthoSize":28,"offsetY":60}'
Set-Content -Path $bodyDn -Value '{"version":1,"time01":0.42,"cycleRunning":true,"rotationSpeed":1.5}'
$jsonCt   = 'Content-Type: application/json'
$ownerHd  = 'X-Is-Environment-Owner: true'
$mmUrl    = "$BaseUrl/environments/e2e_room/minimap/e2e_user.json"
$mmMiss   = "$BaseUrl/environments/e2e_room/minimap/missing_user.json"
$dnUrl    = "$BaseUrl/environments/e2e_room/daynight.json"
$mmTrav   = "$BaseUrl/environments/..%2F..%2Fevil/minimap/hack.json"

# --------------------------------------------------- server up (or start it)
$mainProc = $null

function Test-Health([int]$Port) {
    $r = Invoke-CurlText @("http://127.0.0.1:$Port/health")
    return $r.Status -eq 200 -and $r.Body -match '"ok"\s*:\s*true'
}

$mainPort = if ($BaseUrl -match ':(\d+)') { [int]$matches[1] } else { 80 }
$startedHere = -not (Test-Health $mainPort)
if ($startedHere) {
    Write-Host "No server on port $mainPort - starting node server.js ..."
    $env:PORT = [string]$mainPort; $env:DATA_ROOT = './data'
    $script:mainProc = Start-Process -FilePath node -ArgumentList 'server.js' -WorkingDirectory $root -PassThru -WindowStyle Hidden
    $up = $false
    for ($i = 0; $i -lt 30; $i++) { if (Test-Health $mainPort) { $up = $true; break }; Start-Sleep -Milliseconds 300 }
    if (-not $up) {
        Write-Host 'FAIL: server did not come up'; Stop-Process -Id $script:mainProc.Id -Force -ErrorAction SilentlyContinue
        exit 1
    }
}

try {
    $health = Invoke-CurlText @("$BaseUrl/health")
    Test-Case 'A1 health ok'                       'GET'  '/health'      @("$BaseUrl/health") @('200') @('"ok":true')

    # --- minimap ------------------------------------------------------------
    Test-Case 'B1 PUT minimap (URL id wins)'       'PUT'  '/environments/e2e_room/minimap/e2e_user.json'          (@('-X', 'PUT', $mmUrl, '-H', $jsonCt, '--data', "@$bodyMm")) @('200') @('"citizenId":"e2e_user"', '"zoomOrOrthoSize":28')
    Test-Case 'B2 GET minimap round-trip'          'GET'  '/environments/e2e_room/minimap/e2e_user.json'          @($mmUrl) @('200') @('"radarEnabled":true', '"zoomOrOrthoSize":28')
    Test-Case 'B3 GET other citizen -> 404'        'GET'  '/environments/e2e_room/minimap/missing_user.json'      @($mmMiss) @('404') @('not_found')
    Test-Case 'B4 PUT traversal -> 400'            'PUT'  '/environments/../../evil/minimap/hack.json'            (@('--path-as-is', '-X', 'PUT', $mmTrav, '-H', $jsonCt, '--data', "@$bodyMm")) @('400') @('invalid_id')

    # --- daynight -----------------------------------------------------------
    Test-Case 'B5 PUT daynight owner header'       'PUT'  '/environments/e2e_room/daynight.json'                  (@('-X', 'PUT', $dnUrl, '-H', $jsonCt, '-H', $ownerHd, '--data', "@$bodyDn")) @('200') @('"environmentId":"e2e_room"', '"rotationSpeed":1.5')
    Test-Case 'B6 GET daynight round-trip'         'GET'  '/environments/e2e_room/daynight.json'                  @($dnUrl) @('200') @('"time01":0.42', '"cycleRunning":true')

    # --- strict mode (own server on $StrictPort) ----------------------------
    Test-Health $StrictPort | Out-Null
    $env:PORT = [string]$StrictPort; $env:REQUIRE_OWNER = 'true'
    $strictProc = Start-Process -FilePath node -ArgumentList 'server.js' -WorkingDirectory $root -PassThru -WindowStyle Hidden
    try {
        $up = $false
        for ($i = 0; $i -lt 30; $i++) { if (Test-Health $StrictPort) { $up = $true; break }; Start-Sleep -Milliseconds 300 }
        if (-not $up) {
            $results.Add([pscustomobject]@{ Name = 'B7 strict server'; Call = "start :$StrictPort REQUIRE_OWNER"; Result = 'FAIL'; Status = 0; Note = 'did not start' })
        } else {
            $sDn = "http://127.0.0.1:$StrictPort/environments/e2e_room/daynight.json"
            $sMm = "http://127.0.0.1:$StrictPort/environments/e2e_room/minimap/e2e_user.json"
            Test-Case 'B7a strict PUT no header -> 403'  'PUT'  "strict:$StrictPort .../daynight.json (no header)" (@('-X', 'PUT', $sDn, '-H', $jsonCt, '--data', "@$bodyDn")) @('403') @('not_environment_owner')
            Test-Case 'B7b strict PUT with header -> 200' 'PUT'  "strict:$StrictPort .../daynight.json (owner)"      (@('-X', 'PUT', $sDn, '-H', $jsonCt, '-H', $ownerHd, '--data', "@$bodyDn")) @('200') @('"time01":0.42')
            Test-Case 'B7c strict PUT minimap (no need)'  'PUT'  "strict:$StrictPort .../minimap/e2e_user.json"      (@('-X', 'PUT', $sMm, '-H', $jsonCt, '--data', "@$bodyMm")) @('200') @('"citizenId":"e2e_user"')
        }
    } finally {
        Stop-Process -Id $strictProc.Id -Force -ErrorAction SilentlyContinue
    }

    $fails = @($results | Where-Object { $_.Result -eq 'FAIL' })
    Write-Host ''
    $results | Format-Table Name, Call, Status, Result -AutoSize | Out-String | ForEach-Object { Write-Host $_ }
    foreach ($r in $results) {
        Write-Host ("  {0,-4} {1}" -f $r.Result, $r.Name)
        if ($r.Result -eq 'FAIL') { Write-Host ("       {0}" -f $r.Note) -ForegroundColor Red }
    }
    Write-Host ''
    Write-Host ("SUMMARY: {0} passed, {1} failed (files under {{DATA_ROOT}}/environments/e2e_room)." -f ($results.Count - $fails.Count), $fails.Count)
    if ($fails.Count -gt 0) { exit 1 }
    exit 0
} finally {
    if ($startedHere -and $script:mainProc -ne $null -and -not $KeepServerRunning) {
        Stop-Process -Id $script:mainProc.Id -Force -ErrorAction SilentlyContinue
        Write-Host "(main server started by this script was stopped; rerun with -KeepServerRunning to keep it up for Unity tests)"
    }
    Remove-Item $bodyMm, $bodyDn -Force -ErrorAction SilentlyContinue
}
