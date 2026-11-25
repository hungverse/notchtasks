<#
.SYNOPSIS
Prints the version-win.json entry for a built installer.

.PARAMETER Installer
The setup exe built by build.ps1.

.PARAMETER Url
The https URL the setup will be published at, e.g. https://notchtasks.web.app/download/NotchTasks-3.0.0-win-x64-setup.exe

.PARAMETER Notes
Optional JSON file of release notes: { "<lang>": "text", ... }. Default: { "en": "NotchTasks <version>." }

.PARAMETER OutFile
Optional file to write the entry to, as UTF-8 without a byte order mark. Use it instead of `>` or Out-File: Windows PowerShell 5.1
writes UTF-16 or a BOM there, and the update clients then read no update at all.

.EXAMPLE
.\manifest.ps1 -Installer .\out\NotchTasks-3.0.0-win-x64-setup.exe -Url https://notchtasks.web.app/download/NotchTasks-3.0.0-win-x64-setup.exe -OutFile .\out\version-win.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Installer,
    [Parameter(Mandatory)][string]$Url,
    [string]$Notes,
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Installer -PathType Leaf)) {
    throw "Installer not found: $Installer"
}
if ($Url -notmatch '^https://\S+$') {
    throw "Url must be an https URL, got '$Url'"
}
$installerItem = Get-Item -LiteralPath $Installer
if ((Split-Path -Leaf $Url) -ne $installerItem.Name) {
    Write-Warning "The Url's file name is not the installer's name ($($installerItem.Name))."
}

$productVersion = $installerItem.VersionInfo.ProductVersion
if (-not $productVersion) {
    throw "The installer has no product version: $Installer"
}
$v = [version]$productVersion
$patch = [Math]::Max($v.Build, 0)
$version = "$($v.Major).$($v.Minor).$patch"
# Same formula as UpdateRules.BuildOf: major*10000 + minor*100 + patch.
$build = $v.Major * 10000 + $v.Minor * 100 + $patch

$sha256 = (Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash.ToLowerInvariant()

if ($Notes) {
    if (-not (Test-Path -LiteralPath $Notes -PathType Leaf)) {
        throw "Notes file not found: $Notes"
    }
    $notesObject = Get-Content -Raw -LiteralPath $Notes | ConvertFrom-Json
} else {
    $notesObject = [pscustomobject]@{ en = "NotchTasks $version." }
}

$manifest = [ordered]@{
    version = $version
    build   = $build
    url     = $Url
    sha256  = $sha256
    minOS   = '10.0.19045'
    notes   = $notesObject
}

$json = $manifest | ConvertTo-Json -Depth 5
if (-not $OutFile) {
    $json
    return
}
$outPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutFile)
# UTF-8 without a BOM: the update clients read the feed as UTF-8 text, and a BOM would look like no update at all.
[IO.File]::WriteAllText($outPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Host "Wrote $outPath"
