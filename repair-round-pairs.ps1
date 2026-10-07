param(
  [string]$DataPath = (Join-Path $PSScriptRoot 'data'),
  [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$dataRoot = (Resolve-Path -LiteralPath $DataPath).Path
$repairRoot = Join-Path $dataRoot 'round-pair-repair'
$manifestPath = Join-Path $repairRoot 'manifest.json'

# Pair only consecutive rounds from the same original server directory.
# Do not infer a server from IP/port: both affected servers advertised 27960.
$pairs = @()
$changes = @()
$usedIds = @{}
foreach ($server in 'server1', 'server2') {
  $rounds = @(Get-ChildItem -LiteralPath (Join-Path $dataRoot $server) -Filter '*.json' -File |
    ForEach-Object {
      $json = [IO.File]::ReadAllText($_.FullName)
      [pscustomobject]@{ File = $_; Json = $json; Data = ($json | ConvertFrom-Json) }
    } | Sort-Object { $_.Data.roundStartUnix })
  if ($rounds.Count % 2 -ne 0) { throw "Unpaired round in $server" }

  for ($i = 0; $i -lt $rounds.Count; $i += 2) {
    $a = $rounds[$i]
    $b = $rounds[$i + 1]
    $r1 = $a.Data
    $r2 = $b.Data
    if ($r1.round -ne 1 -or $r2.round -ne 2) { throw "Unexpected round order in $server" }
    foreach ($field in 'mapname', 'servername', 'serverIp', 'serverPort', 'config') {
      if ($r1.$field -cne $r2.$field) { throw "$field differs within $server pair: $($a.File.Name)" }
    }
    if ($r1.roundEndUnix -lt $r1.roundStartUnix -or
        $r2.roundEndUnix -lt $r2.roundStartUnix -or
        $r2.roundStartUnix -lt $r1.roundEndUnix -or
        $r2.roundStartUnix - $r1.roundEndUnix -gt 1800) {
      throw "Invalid pair timestamps: $($a.File.Name)"
    }
    if ([string]::IsNullOrWhiteSpace($r1.matchID) -or $usedIds.ContainsKey($r1.matchID)) {
      throw "Missing or reused round 1 matchID: $($r1.matchID)"
    }
    $usedIds[$r1.matchID] = $true
    $players1 = @($r1.players)
    $players2 = @($r2.players)
    foreach ($players in @($players1, $players2)) {
      if (@($players | Where-Object { [string]::IsNullOrWhiteSpace($_.guid) -or $_.team -notin 1, 2 }).Count -gt 0 -or
          @($players.guid | Select-Object -Unique).Count -ne $players.Count) {
        throw 'Invalid or duplicate player GUID/team'
      }
    }
    $shared = @($players1 | Where-Object { $players2.guid -contains $_.guid })
    if ($shared.Count -lt [Math]::Max($players1.Count, $players2.Count) * 0.8) {
      throw "Insufficient player overlap in $server/$($r1.mapname)"
    }
    foreach ($player in $shared) {
      $other = $players2 | Where-Object { $_.guid -eq $player.guid }
      if ($player.team + $other.team -ne 3) { throw "Player did not swap teams: $($player.guid)" }
    }
    $pair = [ordered]@{
      server = $server
      map = $r1.mapname
      matchId = $r1.matchID
      round1File = "$server/$($a.File.Name)"
      round2File = "$server/$($b.File.Name)"
      round1Players = $players1.Count
      round2Players = $players2.Count
      sharedPlayers = $shared.Count
      addedPlayers = @($players2 | Where-Object { $players1.guid -notcontains $_.guid } |
        ForEach-Object { $_.name -replace '\^.', '' })
      gapSeconds = $r2.roundStartUnix - $r1.roundEndUnix
      needsRepair = $r1.matchID -cne $r2.matchID
    }
    $pairs += [pscustomobject]$pair
    if ($pair.needsRepair) {
      # Replace only the ID string, retaining every stat and the original JSON formatting.
      $idPattern = '("matchID"\s*:\s*")' + [regex]::Escape($r2.matchID) + '(")'
      if ([regex]::Matches($b.Json, $idPattern).Count -ne 1) { throw 'Expected exactly one matchID' }
      $corrected = [regex]::Replace($b.Json, $idPattern, '${1}' + $r1.matchID + '${2}')
      $parsed = $corrected | ConvertFrom-Json
      if ($parsed.matchID -cne $r1.matchID) { throw 'ID replacement failed' }
      $parsed.matchID = $r2.matchID
      if (($parsed | ConvertTo-Json -Depth 100 -Compress) -cne ($r2 | ConvertTo-Json -Depth 100 -Compress)) {
        throw 'Repair unexpectedly changed data beyond matchID'
      }
      $changes += [pscustomobject]@{
        server = $server
        file = $pair.round2File
        oldMatchId = $r2.matchID
        newMatchId = $r1.matchID
        originalSha256 = (Get-FileHash -LiteralPath $b.File.FullName -Algorithm SHA256).Hash
        correctedJson = $corrected
        sourcePath = $b.File.FullName
      }
    }
  }
}

$pairs | Format-Table server, map, round1Players, round2Players, sharedPlayers, needsRepair
Write-Host "Validated $($pairs.Count) same-server pairs; $($changes.Count) round 2 IDs need repair."
if (-not $Apply) { return }
if ($changes.Count -eq 0) { Write-Host 'No source changes needed.'; return }
if (Test-Path -LiteralPath $manifestPath) { throw "Existing repair manifest: $manifestPath" }

$null = New-Item -ItemType Directory -Path $repairRoot -Force
$manifestChanges = @()
foreach ($change in $changes) {
  $backupPath = Join-Path (Join-Path $repairRoot 'originals') $change.file
  $payloadPath = Join-Path (Join-Path $repairRoot 'payloads') $change.file
  $null = New-Item -ItemType Directory -Path (Split-Path $backupPath), (Split-Path $payloadPath) -Force
  Copy-Item -LiteralPath $change.sourcePath -Destination $backupPath
  [IO.File]::WriteAllText($payloadPath, $change.correctedJson, $utf8)
  $manifestChanges += [pscustomobject]@{
    server = $change.server
    file = $change.file
    oldMatchId = $change.oldMatchId
    newMatchId = $change.newMatchId
    originalSha256 = $change.originalSha256
    correctedSha256 = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash
  }
}
$manifest = [ordered]@{
  preparedAtUtc = [DateTime]::UtcNow.ToString('o')
  baseUrl = 'https://et.aukko.net'
  pairs = $pairs
  changes = $manifestChanges
}
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 20), $utf8)
foreach ($change in $changes) {
  [IO.File]::WriteAllText($change.sourcePath, $change.correctedJson, $utf8)
  Write-Host "Repaired $($change.file): $($change.oldMatchId) -> $($change.newMatchId)"
}
Write-Host "Originals, corrected payloads and audit manifest saved in $repairRoot"
