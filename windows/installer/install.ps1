# Installs NotchTasks for the current user and opens it:  irm https://notchtasks.web.app/install.ps1 | iex
# Reads the update feed for the installer's address and SHA-256, checks the download against it, installs silently.
# Publish this file as https://notchtasks.web.app/install.ps1 (see README.md, Release).
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'  # Invoke-WebRequest is many times slower with its progress bar

& {
  if ([Environment]::OSVersion.Platform -ne 'Win32NT' -or [Environment]::OSVersion.Version.Major -lt 10) {
    throw 'NotchTasks needs Windows 10 or 11.'
  }
  if (-not [Environment]::Is64BitOperatingSystem) { throw 'NotchTasks needs 64-bit Windows.' }

  $feed = Invoke-RestMethod 'https://notchtasks.web.app/version-win.json' -Headers @{ 'Cache-Control' = 'no-cache' }
  if ($feed.url -notlike 'https://notchtasks.web.app/*') { throw 'Unexpected installer address in the update feed.' }

  $setup = Join-Path $env:TEMP "NotchTasks-$($feed.version)-setup.exe"
  Write-Host "Downloading NotchTasks $($feed.version)..."
  Invoke-WebRequest $feed.url -OutFile $setup -UseBasicParsing
  $hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
  if ($hash -ne $feed.sha256.ToLower()) { Remove-Item $setup -Force; throw 'The download does not match the published checksum.' }

  # a running copy is replaced: close it first so the new one is the one that opens
  Get-Process NotchTasks -ErrorAction SilentlyContinue | Stop-Process -Force
  Write-Host 'Installing...'
  Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCANCEL' -Wait
  Remove-Item $setup -Force -ErrorAction SilentlyContinue

  $exe = Join-Path $env:LOCALAPPDATA 'Programs\NotchTasks\NotchTasks.exe'
  if (-not (Test-Path $exe)) { throw "Setup finished but $exe is missing." }
  # the setup starts the app itself; start it only if it is not running yet
  Start-Sleep -Seconds 2
  if (-not (Get-Process NotchTasks -ErrorAction SilentlyContinue)) { Start-Process $exe }
  Write-Host 'NotchTasks is installed. Look at the top of your screen.'
}
