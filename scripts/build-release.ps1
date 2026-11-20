<#
.SYNOPSIS
    Builds Rz3 from source: the exe, then (optionally) the installer.

.DESCRIPTION
    One command produces a release. It exists so that "which version is this" is answered by a
    machine and not by memory:

      * the version comes from src/Version.cs only;
      * the exe is compiled with that number stamped into its file version;
      * -Release refuses to run when the working tree is dirty or when git has a tag that
        disagrees, so a published artifact always matches a commit;
      * the installer is generated from the same number (Programs\Rz3\<version>).

.PARAMETER Release
    Release mode: require a clean git tree and, if a tag exists for this version, require that
    HEAD is exactly that tag.

.PARAMETER Installer
    Also compile the Inno Setup installer (installer/Rz3.iss).

.PARAMETER Dist
    Output folder for finished artifacts (default: <repo>\dist).

.EXAMPLE
    .\scripts\build-release.ps1 -Installer
    Builds the exe and the installer of the version in src/Version.cs.

.EXAMPLE
    .\scripts\build-release.ps1 -Release -Installer
    The command used to cut a published version.
#>
[CmdletBinding()]
param(
    [switch]$Release,
    [switch]$Installer,
    [string]$Dist
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$src = Join-Path $repo 'src'
$distDir = if ($Dist) { $Dist } else { Join-Path $repo 'dist' }
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

function Fail([string]$message) {
    Write-Host ''
    Write-Host "BUILD FAILED: $message" -ForegroundColor Red
    exit 1
}

function Step([string]$message) { Write-Host "==> $message" -ForegroundColor Cyan }

# ---------------------------------------------------------------------------------------
# 1. Version: read it from the single source of truth
# ---------------------------------------------------------------------------------------
$versionFile = Join-Path $src 'Version.cs'
if (-not (Test-Path $versionFile)) { Fail "missing $versionFile" }
$versionSource = Get-Content -Raw -Encoding UTF8 $versionFile
$m = [regex]::Match($versionSource, 'public\s+const\s+string\s+Version\s*=\s*"([0-9]+)\.([0-9]+)\.([0-9]+)"')
if (-not $m.Success) { Fail 'src/Version.cs does not contain: public const string Version = "MAJOR.MINOR.PATCH";' }
$version = "$($m.Groups[1].Value).$($m.Groups[2].Value).$($m.Groups[3].Value)"
$numericVersion = "$version.0"
Step "version $version (from src/Version.cs)"

# ---------------------------------------------------------------------------------------
# 2. Release hygiene: a published artifact must correspond to exactly one commit
# ---------------------------------------------------------------------------------------
$git = Get-Command git -ErrorAction SilentlyContinue
$isRepo = $false
if ($git) { $isRepo = (Test-Path (Join-Path $repo '.git')) }
if ($Release -and -not $isRepo) { Fail '-Release needs a git repository (run git init and commit first)' }
if ($Release) {
    if (-not $git) { Fail '-Release needs git on PATH' }
    $dirty = & git -C $repo status --porcelain
    if ($dirty) {
        Fail "the working tree is not clean - commit or stash before cutting a release:`n$($dirty | Out-String)"
    }
    $tagForVersion = "v$version"
    $tagCommit = & git -C $repo rev-list -n 1 $tagForVersion 2>$null
    if ($LASTEXITCODE -eq 0 -and $tagCommit) {
        $head = & git -C $repo rev-parse HEAD
        if ($tagCommit.Trim() -ne $head.Trim()) {
            Fail "tag $tagForVersion exists but does not point at HEAD - the version was already released"
        }
    }
    $allTags = & git -C $repo tag --list 'v*'
    foreach ($t in $allTags) {
        if ($t -ne $tagForVersion -and (& git -C $repo rev-list -n 1 $t) -eq (& git -C $repo rev-parse HEAD)) {
            Fail "HEAD is already tagged $t but Version.cs says $version - bump the version"
        }
    }
    Step "release checks passed (clean tree, tags consistent)"
}
elseif ($git -and $isRepo) {
    $dirty = & git -C $repo status --porcelain
    if ($dirty) { Write-Host "note: working tree is dirty (fine for a local build, refused with -Release)" -ForegroundColor DarkYellow }
}

# ---------------------------------------------------------------------------------------
# 3. Compile the exe
# ---------------------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$exeOut = Join-Path $distDir 'Rz3.exe'
if (Test-Path $exeOut) { Remove-Item $exeOut -Force }

# The .NET Framework compiler has no /version switch, so the file version is supplied the way
# it always was for this compiler: as generated assembly attributes compiled next to the code.
# This file is a build artifact (regenerated on every build), never edited by hand.
$sources = @('Program.cs', 'Mapping.cs', 'RzDevice.cs', 'RateMeter.cs', 'HostEngine.cs', 'TrayIcon.cs', 'Version.cs')
Step "generating version attributes ($numericVersion)"
$attrFile = Join-Path $src 'AssemblyInfo.g.cs'
@"
// <auto-generated by scripts/build-release.ps1 - do not edit, do not commit>
using System.Reflection;
[assembly: AssemblyTitle("Rz3")]
[assembly: AssemblyDescription("Rz3 - Razer Basilisk V3 Pro 35K control without Razer Synapse")]
[assembly: AssemblyProduct("Rz3")]
[assembly: AssemblyCopyright("Open source - see LICENSE")]
[assembly: AssemblyVersion("$numericVersion")]
[assembly: AssemblyFileVersion("$numericVersion")]
[assembly: AssemblyInformationalVersion("$version")]
"@ | Set-Content -Encoding UTF8 $attrFile
$sources += 'AssemblyInfo.g.cs'
foreach ($s in $sources) {
    if (-not (Test-Path (Join-Path $src $s))) { Fail "missing source file: src/$s" }
}
if (-not (Test-Path (Join-Path $src 'Rz3.ico'))) { Fail 'missing src/Rz3.ico' }

Step 'compiling src -> dist/Rz3.exe'
$cscArgs = @(
    '/nologo', '/target:winexe', '/platform:x64', '/optimize+',
    '/codepage:65001',                      # without it non-ASCII literals become mojibake
    '/win32icon:Rz3.ico',
    "/out:$exeOut",
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll'
) + $sources

Push-Location $src
try {
    & $csc @cscArgs 2>&1 | ForEach-Object { if ($_ -notmatch '^\s*$') { Write-Host $_ } }
    if ($LASTEXITCODE -ne 0) { Fail "csc exited with $LASTEXITCODE" }
}
finally { Pop-Location }

$exeSize = (Get-Item $exeOut).Length
$stamped = (Get-Item $exeOut).VersionInfo.FileVersion
Step "built Rz3.exe ($([math]::Round($exeSize / 1KB)) KB, file version $stamped)"
if ($stamped -ne $numericVersion) { Fail "the exe reports version $stamped but src/Version.cs says $version" }

# Rz3.ico sits next to the exe at run time (window/taskbar logo).
Copy-Item (Join-Path $src 'Rz3.ico') (Join-Path $distDir 'Rz3.ico') -Force

# Documents that ship with the installer: the user gets the README and the licence inside the
# installed folder, so the instructions and the legal terms travel with the program.
foreach ($doc in @('README.md', 'LICENSE', 'CHANGELOG.md')) {
    $from = Join-Path $repo $doc
    if (Test-Path $from) { Copy-Item $from (Join-Path $distDir $doc) -Force }
}
$docsFrom = Join-Path $repo 'docs'
if (Test-Path $docsFrom) {
    $docsTo = Join-Path $distDir 'docs'
    New-Item -ItemType Directory -Force -Path $docsTo | Out-Null
    Get-ChildItem $docsFrom -Filter '*.md' | ForEach-Object { Copy-Item $_.FullName $docsTo -Force }
}

# ---------------------------------------------------------------------------------------
# 4. Verify the build before calling it good
# ---------------------------------------------------------------------------------------
Step 'running the logic test suite (--logictest)'
$testDir = Join-Path $env:TEMP ("rz3_build_" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testDir | Out-Null
try {
    Copy-Item $exeOut (Join-Path $testDir 'Rz3.exe') -Force
    Copy-Item (Join-Path $src 'Rz3.ico') (Join-Path $testDir 'Rz3.ico') -Force
    Push-Location $testDir
    try { & (Join-Path $testDir 'Rz3.exe') '--logictest' | Out-Null }
    finally { Pop-Location }
    $resultFile = Join-Path $testDir 'rz3_logictest.txt'
    if (-not (Test-Path $resultFile)) { Fail '--logictest produced no result file' }
    $result = Get-Content -Raw -Encoding UTF8 $resultFile
    $summary = [regex]::Match($result, 'pass=(\d+) fail=(\d+)')
    if (-not $summary.Success) { Fail "--logictest output is unreadable:`n$result" }
    $passed = [int]$summary.Groups[1].Value
    $failed = [int]$summary.Groups[2].Value
    $result -split "`r?`n" | Where-Object { $_ -like 'FAIL*' } | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    if ($failed -ne 0) { Fail "$failed logic test(s) failed" }
    Step "logic tests: $passed passed"
}
finally {
    try { Remove-Item $testDir -Recurse -Force } catch { }
}

# ---------------------------------------------------------------------------------------
# 5. Installer
# ---------------------------------------------------------------------------------------
$installerBuilt = $null
if ($Installer) {
    $isscCandidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    )
    $iscc = $isscCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) {
        Fail @'
Inno Setup is not installed. Either:
  * install it from https://jrsoftware.org/isdl.php and re-run with -Installer, or
  * run this script without -Installer to build only the exe.
'@
    }
    Step "compiling installer with $iscc"
    $iss = Join-Path $repo 'installer\Rz3.iss'
    & $iscc "/DAppVersion=$version" "/DAppSource=$distDir" "/DAppOutput=$distDir" $iss 2>&1 |
        ForEach-Object { if ($_ -notmatch '^\s*$') { Write-Host $_ } }
    if ($LASTEXITCODE -ne 0) { Fail "ISCC exited with $LASTEXITCODE" }
    $installerBuilt = Get-ChildItem $distDir -Filter 'Rz3-Setup-*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $installerBuilt) { Fail 'ISCC reported success but no Rz3-Setup-*.exe was produced' }
    Step "built $($installerBuilt.Name) ($([math]::Round($installerBuilt.Length / 1KB)) KB)"
}

# ---------------------------------------------------------------------------------------
# 6. Checksums for the release page
# ---------------------------------------------------------------------------------------
$hashLines = @()
foreach ($f in (Get-ChildItem $distDir -Filter 'Rz3*' -File)) {
    $h = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashLines += "$h  $($f.Name)"
}
$hashFile = Join-Path $distDir 'SHA256SUMS.txt'
$hashLines | Set-Content -Encoding ASCII $hashFile

Write-Host ''
Step "done - artifacts in $distDir"
Get-ChildItem $distDir -File | Where-Object { $_.Name -like 'Rz3*' -or $_.Name -eq 'SHA256SUMS.txt' } |
    ForEach-Object { "    {0,-34} {1,10:N0} bytes" -f $_.Name, $_.Length }
Write-Host ''
Write-Host "Files: $($distDir)" -ForegroundColor Green
