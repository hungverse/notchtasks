<#
.SYNOPSIS
Builds the per-user NotchTasks installer: NotchTasks-<version>-win-x64-setup.exe.

.DESCRIPTION
Publishes the app, the sensors helper and the hook (self-contained, win-x64) into a stage folder, then compiles
NotchTasks.iss with Inno Setup. The hook is published as a Native AOT exe when the MSVC linker is available; otherwise
it is published self-contained and a warning is printed. Any failing step stops the build.

.PARAMETER Version
Version to build. Default: the <Version> in windows/src/NotchTasks/NotchTasks.csproj.

.PARAMETER Out
Output folder. Default: windows/installer/out (git-ignored). The stage folder is <Out>\stage.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Out
)

$ErrorActionPreference = 'Stop'

$installerDir = $PSScriptRoot
$windowsDir = Split-Path -Parent $installerDir
$appProj = Join-Path $windowsDir 'src\NotchTasks\NotchTasks.csproj'
$sensorsProj = Join-Path $windowsDir 'src\NotchTasksSensors\NotchTasksSensors.csproj'
$hookProj = Join-Path $windowsDir 'src\NotchTasksHook\NotchTasksHook.csproj'
$iss = Join-Path $installerDir 'NotchTasks.iss'

if (-not $Version) {
    $csprojText = Get-Content -Raw -LiteralPath $appProj
    if ($csprojText -notmatch '<Version>([^<]+)</Version>') {
        throw "No <Version> in $appProj"
    }
    $Version = $Matches[1].Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must be major.minor.patch, got '$Version'"
}

if (-not $Out) { $Out = Join-Path $installerDir 'out' }
$Out = [IO.Path]::GetFullPath($Out)
$stage = Join-Path $Out 'stage'
# Every publish gets the same version, so the app, the helper and the hook agree with the setup and the feed entry.
$versionProperty = "-p:Version=$Version"

function Invoke-Dotnet {
    param([string[]]$Arguments)
    Write-Host "> dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE"
    }
}

# The app's own version (what the update check compares) must be the one the setup and the feed carry: -Version is passed to each publish
# as -p:Version, and this checks the staged NotchTasks.dll really has it.
function Assert-StagedVersion {
    param([string]$Dll, [string]$Expected)
    if (-not (Test-Path -LiteralPath $Dll -PathType Leaf)) {
        throw "Stage is missing NotchTasks.dll: $Dll"
    }
    $info = (Get-Item -LiteralPath $Dll).VersionInfo
    # The file version is 4-part (3.0.1.0); the product version may carry a source suffix (3.0.1+abc123).
    $fileVersion = $info.FileVersion
    $fileMajorMinorPatch = if ($fileVersion -match '^(\d+\.\d+\.\d+)') { $Matches[1] } else { $fileVersion }
    $productVersion = ($info.ProductVersion -split '\+')[0]
    if ($fileMajorMinorPatch -ne $Expected -or $productVersion -ne $Expected) {
        throw "NotchTasks.dll is version '$fileVersion' / '$($info.ProductVersion)', expected $Expected. Check -p:Version in build.ps1."
    }
    Write-Host "App version in NotchTasks.dll: $Expected"
}

function Find-Iscc {
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw 'ISCC.exe not found: install Inno Setup 6 or add it to PATH.'
}

Write-Host "NotchTasks $Version -> $Out"
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# 1. The app (framework-only parts come from its own publish; the helper files it publishes are replaced below).
Invoke-Dotnet @('publish', $appProj, '-c', 'Release', '-r', 'win-x64', '--self-contained', $versionProperty, '-o', $stage)

# 2. The sensors helper, self-contained, into the same stage.
Invoke-Dotnet @('publish', $sensorsProj, '-c', 'Release', '-r', 'win-x64', '--self-contained', $versionProperty, '-o', $stage)

# 3. The hook: Native AOT when the MSVC linker is present, else self-contained.
$hookAot = Join-Path ([IO.Path]::GetTempPath()) ("notchtasks-hook-aot-" + [guid]::NewGuid().ToString('N'))
$aotOk = $false
try {
    Invoke-Dotnet @('publish', $hookProj, '-c', 'Release', '-r', 'win-x64', '-p:UseAot=true', $versionProperty, '-o', $hookAot)
    $aotOk = Test-Path -LiteralPath (Join-Path $hookAot 'NotchTasksHook.exe') -PathType Leaf
} catch {
    Write-Warning "Native AOT publish of the hook failed: $($_.Exception.Message)"
}

if ($aotOk) {
    Copy-Item -LiteralPath (Join-Path $hookAot 'NotchTasksHook.exe') -Destination $stage -Force
    foreach ($leftover in 'NotchTasksHook.dll', 'NotchTasksHook.deps.json', 'NotchTasksHook.runtimeconfig.json') {
        $path = Join-Path $stage $leftover
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    Write-Host 'Hook: Native AOT.'
} else {
    Write-Warning 'Native AOT unavailable (no MSVC linker?): the hook is published self-contained instead.'
    Invoke-Dotnet @('publish', $hookProj, '-c', 'Release', '-r', 'win-x64', '--self-contained', $versionProperty, '-o', $stage)
    Write-Host 'Hook: self-contained.'
}
if (Test-Path -LiteralPath $hookAot) { Remove-Item -LiteralPath $hookAot -Recurse -Force }

# 3b. The Fluent UI System Icons licence (MIT). Its fonts are embedded in NotchTasks.dll, not shipped as files, so the notice goes beside
# the app under licenses\ and the installer copies the stage recursively.
$fluentLicense = Join-Path $windowsDir 'src\NotchTasks\Resources\Fonts\LICENSE'
$licenses = Join-Path $stage 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Copy-Item -LiteralPath $fluentLicense -Destination (Join-Path $licenses 'FluentSystemIcons-LICENSE.txt') -Force

if (-not (Test-Path -LiteralPath (Join-Path $stage 'NotchTasks.exe') -PathType Leaf)) {
    throw "Stage is missing NotchTasks.exe: $stage"
}
if (-not (Test-Path -LiteralPath (Join-Path $licenses 'FluentSystemIcons-LICENSE.txt') -PathType Leaf)) {
    throw "Stage is missing the Fluent UI System Icons licence: $licenses"
}
Assert-StagedVersion -Dll (Join-Path $stage 'NotchTasks.dll') -Expected $Version

# 4. The installer.
$iscc = Find-Iscc
Write-Host "> $iscc"
& $iscc "/DAppVersion=$Version" "/DStage=$stage" "/O$Out" $iss
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed with exit code $LASTEXITCODE"
}

$setupName = "NotchTasks-$Version-win-x64-setup.exe"
$setup = Join-Path $Out $setupName
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) {
    throw "Expected installer not found: $setup"
}
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $setup).Length

Write-Host ''
Write-Host "Setup:  $setup"
Write-Host "Size:   $size bytes"
Write-Host "SHA256: $hash"
