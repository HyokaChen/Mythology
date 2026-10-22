# Builds the Mythology Desktop Windows installer (Inno Setup 6) from a self-contained
# win-x64 publish payload, and emits dist/Mythology-Desktop-<V>-win-x64-setup.exe (+ .sha256).
#
# Usage:  powershell -File installer/build-windows.ps1 -Version 1.2.3 [-Rid win-x64]
#                                                             [-PublishDir <dir>] [-SkipInstaller]
#
# NOTE: keep this file pure ASCII. Windows PowerShell 5.1 decodes BOM-less files with the
# system ANSI codepage (GBK here), which turns non-ASCII literals into garbage and produces
# very confusing failures. All Chinese text lives in MythologyDesktop.iss (UTF-8 with BOM).

[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$Rid = 'win-x64',
    [string]$PublishDir = '',
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'

function Write-Log { param([string]$Message) Write-Host "==> $Message" }

# Runs a native tool and returns exit code + combined output.
# Two traps this avoids:
#  1) `& dotnet ... 2>&1` with $ErrorActionPreference='Stop' turns any stray stderr line
#     (NuGet restore notices are routine) into a terminating NativeCommandError.
#  2) Start-Process -PassThru can hand back a null ExitCode; Process + WaitForExit cannot lie.
# Streams are drained concurrently so the 4KB pipe buffer can't deadlock a chatty build.
function Invoke-Native {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$WorkingDirectory
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = (($Arguments | ForEach-Object {
        if ($_ -match '\s') { '"' + $_ + '"' } else { $_ }
    }) -join ' ')
    if ($WorkingDirectory) { $psi.WorkingDirectory = $WorkingDirectory }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    try {
        $stdout = $proc.StandardOutput.ReadToEndAsync()
        $stderr = $proc.StandardError.ReadToEndAsync()
        $proc.WaitForExit()
        $stdout.Wait(30000) | Out-Null
        $stderr.Wait(30000) | Out-Null
        return [PSCustomObject]@{
            ExitCode = $proc.ExitCode
            Output   = ($stdout.Result + [Environment]::NewLine + $stderr.Result)
        }
    }
    finally {
        $proc.Dispose()
    }
}

# packaging.conf is the single source of truth for installer identity and is also `source`d by
# the shell scripts, so values may be quoted. Parse KEY=VALUE and strip one layer of quotes.
function Import-PackagingConfig {
    param([string]$Path)
    $map = @{}
    foreach ($line in Get-Content -LiteralPath $Path -Encoding UTF8) {
        $text = $line.Trim()
        if ($text -eq '' -or $text.StartsWith('#')) { continue }
        $idx = $text.IndexOf('=')
        if ($idx -lt 1) { continue }
        $key = $text.Substring(0, $idx).Trim()
        $val = $text.Substring($idx + 1).Trim()
        if ($val.Length -ge 2 -and $val.StartsWith('"') -and $val.EndsWith('"')) {
            $val = $val.Substring(1, $val.Length - 2)
        }
        $map[$key] = $val
    }
    return $map
}

function Resolve-Iscc {
    $candidates = @(
        $env:ISCC_PATH,
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { return $cmd.Source }
    return $null
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $scriptDir '..')).Path
$confPath = Join-Path $scriptDir 'packaging.conf'
if (-not (Test-Path -LiteralPath $confPath -PathType Leaf)) { throw "missing packaging.conf: $confPath" }
$conf = Import-PackagingConfig -Path $confPath

if ([string]::IsNullOrWhiteSpace($Version)) {
    if ($env:MYTHOLOGY_VERSION) {
        $Version = $env:MYTHOLOGY_VERSION
    }
    else {
        $describe = ''
        try { $describe = (& git -C $repoRoot describe --tags --match 'v*' --abbrev=0 2>$null) -join '' }
        catch { $describe = '' }
        $Version = $describe -replace '^v', ''
    }
}
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$') {
    throw "version must be X.Y.Z or X.Y.Z-prerelease (no v prefix, no +build metadata), got: '$Version'"
}
# Inno 的 AppVersion 可带预发布后缀；但 VersionInfoVersion 必须 4 段纯数字，
# 预发布版（0.0.2-alpha.1）截掉后缀补 .0 → 0.0.2.0。
$baseVersion = ($Version -replace '-[0-9A-Za-z.-]+$', '')
$versionNumeric = "$baseVersion.0"
if ($Rid -ne 'win-x64') { throw "this script only builds win-x64, got: $Rid" }

$appName = $conf['APP_NAME']
$distDir = Join-Path $repoRoot 'dist'
if ([string]::IsNullOrWhiteSpace($PublishDir)) { $PublishDir = Join-Path $distDir "publish\desktop-$Rid" }
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

if (-not $SkipInstaller) {
    Write-Log "dotnet publish src/Desktop/Desktop.csproj -r $Rid -> $PublishDir"
    $publishArgs = @(
        'publish', (Join-Path $repoRoot 'src\Desktop\Desktop.csproj'),
        '-c', 'Release', '-r', $Rid, '--self-contained', 'true',
        ('-p:Version=' + $Version), '-o', $PublishDir
    )
    $publish = Invoke-Native -FilePath 'dotnet' -Arguments $publishArgs -WorkingDirectory $repoRoot
    if ($publish.ExitCode -ne 0) {
        Write-Host $publish.Output
        throw "dotnet publish failed with exit code $($publish.ExitCode)"
    }
}

$exePath = Join-Path $PublishDir "$appName.exe"
$payloadChecks = [ordered]@{
    $exePath                                                                     = 'main executable'
    (Join-Path $PublishDir 'plugins\novel-writing-tools\Plugins.NovelTools.dll') = 'built-in plugin'
    (Join-Path $PublishDir 'Resources\dict.txt')                                 = 'jieba dictionary'
    (Join-Path $PublishDir 'appsettings.json')                                   = 'appsettings.json'
}
foreach ($pair in $payloadChecks.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $pair.Key -PathType Leaf)) {
        throw ("publish payload is missing the {0}: {1}" -f $pair.Value, $pair.Key)
    }
}
Get-ChildItem -LiteralPath $PublishDir -Filter '*.pdb' -Recurse | Remove-Item -Force
Write-Log 'publish payload verified'

if ($SkipInstaller) {
    Write-Log '-SkipInstaller given: payload only, no setup.exe produced'
    exit 0
}

$iscc = Resolve-Iscc
if (-not $iscc) {
    throw 'Inno Setup 6 (ISCC.exe) not found. Install it (choco install innosetup) or set ISCC_PATH.'
}
Write-Log "ISCC: $iscc"

$issPath = Join-Path $scriptDir 'windows\MythologyDesktop.iss'
if (-not (Test-Path -LiteralPath $issPath -PathType Leaf)) { throw "missing script: $issPath" }
$setupIcon = Join-Path $repoRoot 'src\Desktop\Assets\app.ico'
if (-not (Test-Path -LiteralPath $setupIcon -PathType Leaf)) { throw "missing icon: $setupIcon" }

$isccArgs = @(
    ('/DVersion=' + $Version),
    ('/DVersionNumeric=' + $versionNumeric),
    ('/DPayloadDir=' + $PublishDir),
    ('/DOutputDir=' + $distDir),
    ('/DRepoRoot=' + $repoRoot),
    ('/DSetupIcon=' + $setupIcon),
    $issPath
)
$inno = Invoke-Native -FilePath $iscc -Arguments $isccArgs -WorkingDirectory $repoRoot
Write-Host $inno.Output
if ($inno.ExitCode -ne 0) { throw "ISCC failed with exit code $($inno.ExitCode)" }

$setupPath = Join-Path $distDir "Mythology-Desktop-$Version-$Rid-setup.exe"
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "ISCC reported success but $setupPath does not exist"
}

$hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
$hashLine = "$hash  $(Split-Path -Leaf $setupPath)"
$hashPath = "$setupPath.sha256"
# LF, ASCII, no BOM, two spaces: the release job verifies with `sha256sum -c` on Linux.
[IO.File]::WriteAllText($hashPath, $hashLine + "`n", (New-Object System.Text.UTF8Encoding($false)))
$verify = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($verify -ne $hash) { throw "sha256 self-verification failed for $setupPath" }

$sizeMb = (Get-Item -LiteralPath $setupPath).Length / 1MB
Write-Log ("setup.exe: {0} ({1:N1} MB)" -f $setupPath, $sizeMb)
Write-Log "sha256: $hashLine"
