param(
  [string]$RepairPath = (Join-Path $PSScriptRoot 'data/round-pair-repair'),
  [string]$TokenFile = (Join-Path $PSScriptRoot 'data/pappaetstats.key'),
  [string]$AdminToken = $env:PAPPAETSTATS__ADMIN__TOKEN,
  [string]$AdminTokenFile,
  [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$manifest = [IO.File]::ReadAllText((Join-Path $RepairPath 'manifest.json')) | ConvertFrom-Json
if ($manifest.baseUrl -cne 'https://et.aukko.net') { throw 'Unexpected repair API host' }

# This recovery targets two existing matches; it must not create replacement matches.
$expectedMatches = @{
  b905f2ad779146b38aace0c583dd6032 = 'c1234885-f0bd-4b98-91e8-a19bc5c9626e'
  '215a9c1890454ada876c8e81963a2a58' = '60e399b4-66c7-4768-8e5b-e99a0cbfec5c'
}
$changes = @($manifest.changes)
if ($changes.Count -ne 2 -or @($changes.newMatchId | Select-Object -Unique).Count -ne 2) {
  throw 'Expected exactly two distinct repaired matches'
}
$payloads = @()
foreach ($change in $changes) {
  if (-not $expectedMatches.ContainsKey($change.newMatchId) -or
      $change.file -notmatch '^server[12]/posted_[0-9]+_[0-9]+\.json$') {
    throw 'Unexpected match ID or payload path'
  }
  $path = Join-Path (Join-Path $RepairPath 'payloads') $change.file
  if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $change.correctedSha256) {
    throw "Corrected payload hash mismatch: $($change.file)"
  }
  $json = [IO.File]::ReadAllText($path)
  $dto = $json | ConvertFrom-Json
  if ($dto.round -ne 2 -or $dto.mapname -cne 'sw_goldrush_te' -or $dto.matchID -cne $change.newMatchId) {
    throw 'Unexpected corrected payload contents'
  }
  $payloads += [pscustomobject]@{ Change = $change; Json = $json }
}
if ($DryRun) {
  foreach ($payload in $payloads) {
    Write-Host "Would replace round 2 for $($payload.Change.newMatchId) using $($payload.Change.file)"
  }
  Write-Host 'Would then rebuild skill ratings using the admin recalculation endpoint.'
  return
}

$token = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $TokenFile).Path).Trim()
if ($AdminTokenFile) { $AdminToken = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $AdminTokenFile).Path).Trim() }
if ([string]::IsNullOrWhiteSpace($token) -or [string]::IsNullOrWhiteSpace($AdminToken)) {
  throw 'Both ingest and admin authentication are required to complete this repair'
}
$ingestHeaders = @{ Authorization = "Bearer $token" }
$adminHeaders = @{ Authorization = "Bearer $AdminToken" }
$receiptPath = Join-Path $RepairPath 'post-receipts.json'
$receipts = @()
if (Test-Path -LiteralPath $receiptPath) {
  $receipts = @(Get-Content -Raw -LiteralPath $receiptPath | ConvertFrom-Json)
}
function Save-Receipts {
  [IO.File]::WriteAllText($receiptPath, (ConvertTo-Json -InputObject @($script:receipts) -Depth 20), $utf8)
}

# Check admin authentication against a nonexistent match without altering any data.
$preflight = Invoke-RestMethod -Method Post -Uri ($manifest.baseUrl + '/api/admin/matches/recalculate-winners') `
  -Headers $adminHeaders -ContentType 'application/json' `
  -Body '{"matchId":"00000000-0000-0000-0000-000000000000"}' -TimeoutSec 30
if ($preflight.processed -ne 0 -or $preflight.updated -ne 0) { throw 'Unexpected admin preflight result' }
foreach ($payload in $payloads) {
  $matchDbId = $expectedMatches[$payload.Change.newMatchId]
  $page = Invoke-WebRequest -UseBasicParsing -Uri ($manifest.baseUrl + '/matches/' + $matchDbId) -TimeoutSec 30
  if (-not $page.Content.Contains($payload.Change.newMatchId)) { throw "Existing match not found: $matchDbId" }
}

foreach ($payload in $payloads) {
  $id = $payload.Change.newMatchId
  $previous = @($receipts | Where-Object { $_.matchId -eq $id })
  if ($previous.Count -gt 0) {
    if ($previous[-1].status -ne 'completed') {
      throw "A previous POST may have reached the API for $id. Verify it before attempting another POST."
    }
    Write-Host "Already posted $id; skipping to avoid applying ratings twice."
    continue
  }
  $receipt = [pscustomobject]@{
    matchId = $id
    file = $payload.Change.file
    startedAtUtc = [DateTime]::UtcNow.ToString('o')
    status = 'started'
    response = $null
  }
  $receipts += $receipt
  Save-Receipts
  # No automatic retries: round 2 ingestion applies ratings on every request.
  $response = Invoke-RestMethod -Method Post -Uri ($manifest.baseUrl + '/api/matches') `
    -Headers $ingestHeaders -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($payload.Json)) -TimeoutSec 60
  if ($response.matchId -cne $id -or $response.round -ne 2 -or
      $response.matchDbId -cne $expectedMatches[$id]) { throw 'Unexpected ingest response; inspect receipts before retrying' }
  $receipt.status = 'completed'
  $receipt.response = $response
  Save-Receipts
  Write-Host "Replaced round 2: $($manifest.baseUrl)/matches/$($response.matchDbId)"
}

$ratingsPath = Join-Path $RepairPath 'ratings-recalculation.json'
if (-not (Test-Path -LiteralPath $ratingsPath)) {
  $ratings = Invoke-RestMethod -Method Post -Uri ($manifest.baseUrl + '/api/admin/skillratings/recalculate') `
    -Headers $adminHeaders -TimeoutSec 60
  [IO.File]::WriteAllText($ratingsPath, ($ratings | ConvertTo-Json -Depth 20), $utf8)
  Write-Host "Rebuilt skill ratings: $($ratings.processedMatches) matches processed."
} else {
  Write-Host 'Rating recalculation already completed.'
}
