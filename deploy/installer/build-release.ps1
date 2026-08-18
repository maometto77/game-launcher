<#
.SYNOPSIS
    Builds a release of Don that can be handed to someone else.

.DESCRIPTION
    Publishes the desktop client as a single self-contained win-x64 executable,
    stages it alongside the install and uninstall scripts, and produces:

        Don-<version>-win-x64.zip   the archive to distribute
        Don-<version>-win-x64.zip.sha256
        release.json                version, hash and size, for the download page

    If Inno Setup is installed it also compiles Don.iss into a conventional
    setup executable. That is optional: the zip is a complete, working install on
    its own, which matters because the whole payload is one executable and a
    couple of scripts. There is nothing an installer must do that a copy cannot.

.PARAMETER Version
    Version to stamp on the release. Defaults to the version compiled into the
    executable.

.PARAMETER SkipPublish
    Stage and package whatever is already in bin\publish\win-x64.

.PARAMETER SkipInstaller
    Do not compile the Inno Setup installer even if Inno Setup is present.

.PARAMETER OutputRoot
    Where to write the release. Defaults to deploy\installer\output.

.EXAMPLE
    .\build-release.ps1

.EXAMPLE
    .\build-release.ps1 -Version 1.2.0 -SkipInstaller
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $OutputRoot,
    [switch] $SkipPublish,
    [switch] $SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$AppName    = 'Don'
$ExeName    = 'Don.exe'
$Runtime    = 'win-x64'

$repoRoot   = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).ProviderPath
$project    = Join-Path $repoRoot 'GameLauncher.Desktop'
$publishDir = Join-Path $project "bin\publish\$Runtime"

if (-not $OutputRoot) { $OutputRoot = Join-Path $PSScriptRoot 'output' }

function Write-Section {
    param([string] $Message)
    Write-Host "`n=== $Message" -ForegroundColor Cyan
}

# ------------------------------------------------------------ check the scripts

# A PowerShell script is parsed in full before a single line of it runs, so one
# syntax error anywhere means the script does nothing at all. In an installer
# that is discovered by the person who downloaded it, and in an uninstaller it is
# discovered by someone who now cannot remove the application.
#
# This is first, before the expensive publish, and it is here because exactly
# that happened: "$InstallRoot:" in a message parses as a drive-qualified
# variable reference and took the whole uninstaller down with it.

Write-Section 'Checking the scripts'

$scriptNames = @('Install-Don.ps1', 'Uninstall-Don.ps1')
$broken = $false

foreach ($name in $scriptNames) {
    $path = Join-Path $PSScriptRoot $name
    $parseErrors = $null

    $null = [System.Management.Automation.Language.Parser]::ParseFile(
        $path, [ref] $null, [ref] $parseErrors)

    if ($parseErrors -and $parseErrors.Count -gt 0) {
        $broken = $true
        Write-Host "  $name" -ForegroundColor Red
        foreach ($parseError in $parseErrors) {
            Write-Host "    line $($parseError.Extent.StartLineNumber): $($parseError.Message)" -ForegroundColor Red
        }
    }
    else {
        Write-Host "  $name parses"
    }

    # Non-ASCII survives fine in a file with a byte-order mark and turns to
    # mojibake in one without, because Windows PowerShell falls back to the
    # legacy code page. Keeping these scripts to ASCII sidesteps the question of
    # which encoding they are read with.
    $lineNumber = 0

    foreach ($line in [System.IO.File]::ReadAllLines($path)) {
        $lineNumber++
        if ($line -match '[^\x00-\x7F]') {
            $broken = $true
            Write-Host "    line ${lineNumber}: non-ASCII character" -ForegroundColor Red
        }
    }
}

if ($broken) { throw 'The installer scripts did not pass their checks. Nothing has been built.' }

# ------------------------------------------------------------------- publish

if (-not $SkipPublish) {
    Write-Section 'Publishing'

    # Cleared first: the single-file bundler will happily leave a previous run's
    # loose assemblies beside the new executable, and they get packaged.
    if (Test-Path -LiteralPath $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }

    & dotnet publish $project -c Release -p:PublishProfile=$Runtime --nologo

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
}

$publishedExe = Join-Path $publishDir $ExeName

if (-not (Test-Path -LiteralPath $publishedExe)) {
    throw "'$publishedExe' was not produced. If the assembly name changed, this script and Don.iss both need to know."
}

if (-not $Version) {
    $info = (Get-Item -LiteralPath $publishedExe).VersionInfo
    $Version = $info.ProductVersion
    if (-not $Version) { $Version = $info.FileVersion }
    if (-not $Version) { $Version = '1.0.0' }
}

# A ProductVersion can carry build metadata such as "1.0.0+3a1c9e2", which is
# meaningful in the binary and unhelpful in a file name.
$Version = ($Version -split '\+')[0].Trim()

Write-Host "  $ExeName $Version  ($([math]::Round((Get-Item -LiteralPath $publishedExe).Length / 1MB, 1)) MB)"

# --------------------------------------------------------------------- stage

$stageName = "$AppName-$Version-$Runtime"
$stageDir  = Join-Path $OutputRoot $stageName

Write-Section "Staging $stageName"

if (Test-Path -LiteralPath $stageDir) { Remove-Item -LiteralPath $stageDir -Recurse -Force }
$null = New-Item -ItemType Directory -Path $stageDir -Force

Copy-Item -LiteralPath $publishedExe -Destination $stageDir

# Anything the publish profile put beside the executable (a bundled aria2c, for
# instance) travels with it. Two things are held back: debug symbols, which are
# a third of the archive and useless to whoever is installing, and dotfiles,
# which are repository bookkeeping that has no business in a release.
foreach ($item in Get-ChildItem -LiteralPath $publishDir -Exclude $ExeName) {
    if ($item.Extension -ieq '.pdb') { continue }
    Copy-Item -LiteralPath $item.FullName -Destination $stageDir -Recurse -Force
}

foreach ($dotfile in Get-ChildItem -LiteralPath $stageDir -Recurse -File -Force |
    Where-Object { $_.Name.StartsWith('.') }) {
    Remove-Item -LiteralPath $dotfile.FullName -Force
}

# tools\ is where ExternalToolLocator looks before falling back to PATH, so an
# empty one ships on purpose. Unlabelled it is a mystery folder, and the note
# costs nothing.
$toolsDir = Join-Path $stageDir 'tools'

if (-not (Test-Path -LiteralPath $toolsDir)) {
    $null = New-Item -ItemType Directory -Path $toolsDir -Force
}

if (-not (Get-ChildItem -LiteralPath $toolsDir -File)) {
    Set-Content -LiteralPath (Join-Path $toolsDir 'README.txt') -Encoding UTF8 -Value @"
Drop aria2c.exe in this folder to enable torrent downloads and multi-connection
HTTP transfers. $AppName looks here first, then on PATH, and works without it.

Get it from https://github.com/aria2/aria2/releases (the win-64bit build).
"@
}

foreach ($script in @('Install-Don.ps1', 'Uninstall-Don.ps1', 'Install.cmd', 'Uninstall.cmd')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination $stageDir
}

$readme = @"
$AppName $Version
================

To install
----------
Double-click Install.cmd.

It installs to your own user profile, so Windows will not ask for an
administrator password. $AppName appears in the Start Menu, on the desktop, and
in Add/Remove Programs like anything else.

Windows will probably warn you first. This build is not code-signed, so
SmartScreen shows "Windows protected your PC": choose More info, then Run
anyway. A signing certificate is the only thing that removes that warning, and
it is bought per year.

To remove it
------------
Use Add/Remove Programs, or double-click Uninstall.cmd.

Your library (games, playtime, collections, achievements, and your relay
sign-in) lives in %LOCALAPPDATA%\$AppName and is kept unless you say otherwise
when uninstalling. Reinstalling picks up exactly where you left off.

Options
-------
The scripts take switches if you want something other than the defaults:

    powershell -ExecutionPolicy Bypass -File Install-Don.ps1 -AllUsers
    powershell -ExecutionPolicy Bypass -File Install-Don.ps1 -NoStartMenu
    powershell -ExecutionPolicy Bypass -File Uninstall-Don.ps1 -RemoveLibrary

Run `Get-Help .\Install-Don.ps1 -Full` for the rest.
"@

Set-Content -LiteralPath (Join-Path $stageDir 'README.txt') -Value $readme -Encoding UTF8

$staged = @(Get-ChildItem -LiteralPath $stageDir -Recurse -File)
Write-Host "  $($staged.Count) file(s), $([math]::Round(($staged | Measure-Object -Property Length -Sum).Sum / 1MB, 1)) MB"

# ------------------------------------------------------------------ package

Write-Section 'Packaging'

Add-Type -AssemblyName System.IO.Compression.FileSystem

$zipPath = Join-Path $OutputRoot "$stageName.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

# ZipFile rather than Compress-Archive: it streams, where Compress-Archive
# buffers, and an 80 MB payload is enough for that to matter.
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stageDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)

$zip  = Get-Item -LiteralPath $zipPath
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()

# Written in the format sha256sum reads, so it can be checked on the VPS with
# the tool that is already there.
Set-Content -LiteralPath "$zipPath.sha256" -Value "$hash  $($zip.Name)" -Encoding ASCII

Write-Host "  $($zip.Name)  $([math]::Round($zip.Length / 1MB, 1)) MB"
Write-Host "  sha256 $hash"

# ---------------------------------------------------------------- manifest

# What a download page reads, and what an updater would poll. Kept beside the
# archive so publishing is a copy of one directory.
$release = [ordered]@{
    product     = $AppName
    version     = $Version
    runtime     = $Runtime
    file        = $zip.Name
    size        = $zip.Length
    sha256      = $hash
    releasedUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}

$manifestPath = Join-Path $OutputRoot 'release.json'

# Written without a byte-order mark. Set-Content -Encoding UTF8 emits one under
# Windows PowerShell, and a BOM in front of the opening brace is not valid JSON
# to a strict parser — including the browser's JSON.parse, which is exactly what
# a download page would use to read this.
[System.IO.File]::WriteAllText(
    $manifestPath,
    ($release | ConvertTo-Json),
    (New-Object System.Text.UTF8Encoding $false))

# ----------------------------------------------------------- Inno Setup, maybe

$setupPath = $null

if (-not $SkipInstaller) {
    $iscc = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

    if (-not $iscc) {
        $command = Get-Command 'iscc' -ErrorAction SilentlyContinue
        if ($command) { $iscc = $command.Source }
    }

    if ($iscc) {
        Write-Section 'Compiling the Inno Setup installer'

        & $iscc "/DAppVersion=$Version" "/O$OutputRoot" (Join-Path $PSScriptRoot 'Don.iss')

        if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

        $setupPath = Join-Path $OutputRoot "$AppName-Setup-$Version.exe"
    }
    else {
        Write-Section 'Inno Setup not found: skipping the setup executable'
        Write-Host '  The zip is a complete install on its own. To build a conventional'
        Write-Host '  installer as well, install Inno Setup 6 and run this again:'
        Write-Host '    winget install JRSoftware.InnoSetup'
    }
}

# -------------------------------------------------------------------- done

Write-Section "Release $Version"
Write-Host "  $zipPath"
if ($setupPath -and (Test-Path -LiteralPath $setupPath)) { Write-Host "  $setupPath" }
Write-Host "  $manifestPath"
Write-Host "`nTo publish it, see deploy/README.md.`n"
