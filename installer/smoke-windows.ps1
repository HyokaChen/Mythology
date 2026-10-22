# Verifies a built installer the same way the CI windows job does:
# sha256 sidecar -> silent per-user install -> payload assertions -> silent uninstall -> leftovers.
#
# Usage: powershell -File installer/smoke-windows.ps1 -Version 1.2.3 [-Rid win-x64]
#
# Keep this file pure ASCII: Windows PowerShell 5.1 decodes BOM-less scripts with the system
# ANSI codepage, which mangles non-ASCII literals (and can break parsing outright).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Rid = 'win-x64'
)

$ErrorActionPreference = 'Stop'

function Write-Log { param([string]$Message) Write-Host "==> $Message" }

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $scriptDir '..')).Path
$distDir = Join-Path $repoRoot 'dist'
$setupPath = Join-Path $distDir "Mythology-Desktop-$Version-$Rid-setup.exe"
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) { throw "installer not found: $setupPath" }

# The sidecar is what users check the download against, so verify it before installing.
$hashPath = "$setupPath.sha256"
if (-not (Test-Path -LiteralPath $hashPath -PathType Leaf)) { throw "checksum file not found: $hashPath" }
$parts = ((Get-Content -LiteralPath $hashPath -Raw).Trim() -split '\s\s', 2)
if ($parts.Count -ne 2) { throw "unexpected checksum format in $hashPath" }
$actualHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $parts[0]) {
    throw ("checksum mismatch for {0}: sidecar={1} actual={2}" -f (Split-Path -Leaf $setupPath), $parts[0], $actualHash)
}
Write-Log "sha256 sidecar verified: $actualHash"

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Mythology Desktop'
if (Test-Path -LiteralPath $installDir) { throw "refusing to smoke over a pre-existing install: $installDir" }

# /CURRENTUSER works because the .iss sets PrivilegesRequiredOverridesAllowed=commandline.
Write-Log 'silent per-user install'
$install = Start-Process -FilePath $setupPath `
    -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER' -Wait -PassThru
if ($install.ExitCode -ne 0) { throw "installer exit code $($install.ExitCode)" }

# Same payload contract build-windows.ps1 asserts before packing.
$required = @(
    'MythDesktop.exe',
    'appsettings.json',
    'plugins\novel-writing-tools\Plugins.NovelTools.dll',
    'plugins\context-continuation\plugin.json',
    'Resources\dict.txt',
    'README.md'
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $installDir $relative))) {
        throw "installed payload is missing $relative"
    }
}
$pluginDir = Join-Path $installDir 'plugins'
$pluginCount = @(Get-ChildItem -LiteralPath $pluginDir -Directory).Count
if ($pluginCount -lt 6) { throw "expected 6 built-in plugin directories, found $pluginCount" }
Write-Log "installed payload verified ($pluginCount plugin directories)"

$desktopIcon = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Mythology Desktop.lnk'
Write-Log ("desktop shortcut created: " + (Test-Path -LiteralPath $desktopIcon))

Write-Log 'silent uninstall'
$uninstaller = Join-Path $installDir 'unins000.exe'
if (-not (Test-Path -LiteralPath $uninstaller)) { throw "uninstaller not written: $uninstaller" }
$uninstall = Start-Process -FilePath $uninstaller `
    -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
if ($uninstall.ExitCode -ne 0) { throw "uninstaller exit code $($uninstall.ExitCode)" }
if (Test-Path -LiteralPath $installDir) { throw "install dir survived uninstall: $installDir" }
if (Test-Path -LiteralPath $desktopIcon) { throw "desktop shortcut survived uninstall" }

Write-Log 'installer smoke passed'
