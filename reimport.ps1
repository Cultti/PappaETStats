param(
  [Parameter(Mandatory = $false)]
  [string]$BaseUrl = "https://et.aukko.net/api/",

  [Parameter(Mandatory = $false)]
  [string]$Token = "1234567890",

  [Parameter(Mandatory = $false)]
  [string]$DataPath = ".\\data",

  [Parameter(Mandatory = $false)]
  [switch]$DryRun,

  [Parameter(Mandatory = $false)]
  [int]$MaxRetries = 3,

  [Parameter(Mandatory = $false)]
  [int]$RetryDelaySeconds = 2
)

$ErrorActionPreference = 'Stop'

function Normalize-BaseUrl([string]$url) {
  if ([string]::IsNullOrWhiteSpace($url)) { throw "BaseUrl is required" }
  if (-not $url.EndsWith('/')) { return $url + '/' }
  return $url
}

$BaseUrl = Normalize-BaseUrl $BaseUrl
$endpoint = $BaseUrl + 'matches'

if (-not (Test-Path -LiteralPath $DataPath)) {
  throw "DataPath not found: $DataPath"
}

$headers = @{
  Authorization = "Bearer $Token"
}

$files = @()
if ((Get-Item -LiteralPath $DataPath).PSIsContainer) {
  $files = Get-ChildItem -LiteralPath $DataPath -Filter '*.json' -File | Sort-Object Name
} else {
  $files = @((Get-Item -LiteralPath $DataPath))
}

if ($files.Count -eq 0) {
  throw "No .json files found in: $DataPath"
}

Write-Host "Posting $($files.Count) file(s) to $endpoint" -ForegroundColor Cyan

$ok = 0
$fail = 0

foreach ($file in $files) {
  $name = $file.Name
  $json = [System.IO.File]::ReadAllText($file.FullName)

  if ($DryRun) {
    Write-Host "[DRYRUN] Would POST: $name" -ForegroundColor Yellow
    continue
  }

  $attempt = 0
  $posted = $false

  while (-not $posted -and $attempt -lt $MaxRetries) {
    $attempt++
    try {
      $null = Invoke-RestMethod -Method Post -Uri $endpoint -Headers $headers -ContentType 'application/json' -Body $json
      Write-Host "[OK] $name" -ForegroundColor Green
      $ok++
      $posted = $true
    }
    catch {
      $msg = $_.Exception.Message
      if ($attempt -lt $MaxRetries) {
        Write-Host "[RETRY $attempt/$MaxRetries] $name -> $msg" -ForegroundColor DarkYellow
        Start-Sleep -Seconds $RetryDelaySeconds
      } else {
        Write-Host "[FAIL] $name -> $msg" -ForegroundColor Red
        $fail++
      }
    }
  }
}

Write-Host "Done. OK=$ok FAIL=$fail" -ForegroundColor Cyan
if ($fail -gt 0) { exit 1 }
