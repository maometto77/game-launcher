<#
.SYNOPSIS
    Installs Don.

.DESCRIPTION
    Copies the published payload into place, creates shortcuts, and registers
    the application in Add/Remove Programs.

    Per-user by default, into %LOCALAPPDATA%\Programs\Don, because a launcher is
    a personal application and asking a friend for an administrator password to
    install one is a bad trade. -AllUsers installs to Program Files instead and
    does require elevation.

    Everything written is recorded in install-manifest.txt beside the executable.
    Uninstall-Don.ps1 removes exactly the files on that list, which is why an
    uninstall cannot take anything with it that it did not put there, including
    when the install directory is somewhere shared or was chosen by mistake.

.PARAMETER Source
    Folder holding Don.exe. Defaults to the folder this script is in, so an
    extracted release archive installs by running the script inside it.

.PARAMETER InstallRoot
    Where to install. Overrides the per-user and -AllUsers defaults.

.PARAMETER AllUsers
    Install to Program Files for every user. Requires an elevated session.

.PARAMETER DesktopShortcut
    Also place a shortcut on the desktop.

.PARAMETER NoStartMenu
    Skip the Start Menu shortcut.

.PARAMETER Silent
    Never prompt. Fails instead of asking.

.PARAMETER Launch
    Start Don when the install finishes.

.EXAMPLE
    .\Install-Don.ps1
    Installs for the current user, with a Start Menu shortcut.

.EXAMPLE
    .\Install-Don.ps1 -AllUsers -DesktopShortcut -Silent
    Unattended per-machine install. Must be run elevated.
#>
[CmdletBinding()]
param(
    [string]   $Source,
    [string]   $InstallRoot,
    [switch]   $AllUsers,
    [switch]   $DesktopShortcut,
    [switch]   $NoStartMenu,
    [switch]   $Silent,
    [switch]   $Launch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$AppName      = 'Don'
$ExeName      = 'Don.exe'
$Publisher    = 'Don'
$ManifestName = 'install-manifest.txt'
$RegistryKey  = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\Don'

function Write-Step {
    param([string] $Message)
    if (-not $Silent) { Write-Host "  $Message" }
}

function Write-Title {
    param([string] $Message)
    if (-not $Silent) { Write-Host "`n$Message" -ForegroundColor Cyan }
}

function Test-Elevated {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ---------------------------------------------------------------- the payload

if (-not $Source) { $Source = $PSScriptRoot }

$Source = (Resolve-Path -LiteralPath $Source).ProviderPath
$sourceExe = Join-Path $Source $ExeName

if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw "No $ExeName in '$Source'. Point -Source at the folder holding the published executable."
}

# The version shown in Add/Remove Programs comes off the binary rather than a
# constant here, so a release cannot be labelled with the previous version's
# number by forgetting to edit this script.
$versionInfo = (Get-Item -LiteralPath $sourceExe).VersionInfo
$version     = $versionInfo.ProductVersion
if (-not $version) { $version = $versionInfo.FileVersion }
if (-not $version) { $version = '1.0.0' }

# A ProductVersion carries build metadata after a '+', which in this build is
# the source revision. That belongs in the binary and not in Add/Remove
# Programs, where it turns a version into a forty-character hash.
$version = ($version -split '\+')[0].Trim()

# A single-file build carries its own runtime, so a plausible payload is tens of
# megabytes. Anything tiny is a stub or a partial download, and installing it
# would fail later and less clearly than it fails here.
$exeLength = (Get-Item -LiteralPath $sourceExe).Length
if ($exeLength -lt 1MB) {
    throw "'$sourceExe' is only $([math]::Round($exeLength / 1KB)) KB. That is not a complete self-contained build; the download may have been truncated."
}

# ----------------------------------------------------------- where it is going

if (-not $InstallRoot) {
    if ($AllUsers) {
        $InstallRoot = Join-Path $env:ProgramFiles $AppName
    }
    else {
        $InstallRoot = Join-Path (Join-Path $env:LOCALAPPDATA 'Programs') $AppName
    }
}

if ($AllUsers -and -not (Test-Elevated)) {
    throw '-AllUsers writes to Program Files and the machine registry. Re-run this from an elevated PowerShell session, or drop -AllUsers to install just for you.'
}

$registryHive = 'HKCU:'
if ($AllUsers) { $registryHive = 'HKLM:' }
$registryPath = Join-Path $registryHive $RegistryKey

$installedExe = Join-Path $InstallRoot $ExeName
$manifestPath = Join-Path $InstallRoot $ManifestName

Write-Title "Installing $AppName $version"
Write-Step  "from  $Source"
Write-Step  "to    $InstallRoot"
Write-Step  ("scope " + $(if ($AllUsers) { 'all users' } else { 'this user only' }))

if ($Source -ieq $InstallRoot) {
    throw "Source and destination are the same folder. Run this from the extracted archive, or pass -InstallRoot."
}

# ------------------------------------------------- a copy already running here

# Copying over a running executable fails with a file lock, and the error names
# a path rather than the cause. Checked by path rather than by process name so
# an unrelated process that happens to be called Don is never touched.
$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and ($_.Path -ieq $installedExe) } catch { $false }
})

if ($running.Count -gt 0) {
    if (-not $Silent) {
        Write-Host "`n$AppName is running and has to close before it can be replaced." -ForegroundColor Yellow
        $answer = Read-Host '  Close it now? [Y/n]'
        if ($answer -and $answer -notmatch '^[Yy]') { throw 'Cancelled. Close Don and run this again.' }
    }

    Write-Step 'Closing the running instance'

    # Asked politely first: CloseMainWindow lets the app flush settings and its
    # database. Killed only if it ignores that.
    foreach ($process in $running) {
        $null = $process.CloseMainWindow()
    }

    $null = $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue

    foreach ($process in $running) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    }
}

# ------------------------------------------------------ clear a previous build

# Removing the previous payload rather than copying over it, so a file dropped
# from a later release does not linger and get loaded.
if (Test-Path -LiteralPath $manifestPath) {
    Write-Step 'Removing the previous version'

    foreach ($relative in (Get-Content -LiteralPath $manifestPath -Encoding UTF8)) {
        if (-not $relative) { continue }
        $stale = Join-Path $InstallRoot $relative
        if (Test-Path -LiteralPath $stale) {
            Remove-Item -LiteralPath $stale -Force -ErrorAction SilentlyContinue
        }
    }
}

# --------------------------------------------------------------- the copy

if (-not (Test-Path -LiteralPath $InstallRoot)) {
    $null = New-Item -ItemType Directory -Path $InstallRoot -Force
}

$payload = @(Get-ChildItem -LiteralPath $Source -Recurse -File | Where-Object {
    # The wrappers are conveniences for the archive and mean nothing once
    # installed; the manifest is rewritten below rather than copied.
    $_.Name -notin @('Install.cmd', 'Uninstall.cmd', $ManifestName)
})

Write-Step "Copying $($payload.Count) file(s), $([math]::Round(($payload | Measure-Object -Property Length -Sum).Sum / 1MB)) MB"

$installed = New-Object System.Collections.Generic.List[string]

foreach ($file in $payload) {
    $relative    = $file.FullName.Substring($Source.Length).TrimStart('\', '/')
    $destination = Join-Path $InstallRoot $relative
    $parent      = Split-Path -Parent $destination

    if (-not (Test-Path -LiteralPath $parent)) {
        $null = New-Item -ItemType Directory -Path $parent -Force
    }

    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force

    # Files that arrived from a browser carry a mark-of-the-web stream, and
    # Windows treats those as untrusted for as long as it survives the copy.
    Unblock-File -LiteralPath $destination -ErrorAction SilentlyContinue

    $installed.Add($relative)
}

if (-not (Test-Path -LiteralPath $installedExe)) {
    throw "The copy finished but '$installedExe' is missing. Nothing has been registered."
}

# ------------------------------------------------------------- the shortcuts

function New-Shortcut {
    param(
        [string] $Path,
        [string] $Target,
        [string] $Description
    )

    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent)) {
        $null = New-Item -ItemType Directory -Path $parent -Force
    }

    $shell    = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)

    $shortcut.TargetPath       = $Target
    $shortcut.WorkingDirectory = Split-Path -Parent $Target
    $shortcut.IconLocation     = "$Target,0"
    $shortcut.Description      = $Description
    $shortcut.Save()

    # Released explicitly: the COM object keeps a handle on the .lnk, and
    # leaving it to the garbage collector means an uninstall in the same session
    # can fail to delete the shortcut it just made.
    $null = [Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut)
    $null = [Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
}

$shortcuts = New-Object System.Collections.Generic.List[string]

if (-not $NoStartMenu) {
    $programsRoot = if ($AllUsers) {
        Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
    } else {
        Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    }

    # Straight into Programs rather than a folder of its own. A folder holding
    # one shortcut is a folder the user has to open every time.
    $startMenuLink = Join-Path $programsRoot "$AppName.lnk"
    New-Shortcut -Path $startMenuLink -Target $installedExe -Description "$AppName game launcher"
    $shortcuts.Add($startMenuLink)
    Write-Step 'Start Menu shortcut'
}

if ($DesktopShortcut) {
    $desktopRoot = if ($AllUsers) {
        Join-Path $env:PUBLIC 'Desktop'
    } else {
        [Environment]::GetFolderPath('Desktop')
    }

    $desktopLink = Join-Path $desktopRoot "$AppName.lnk"
    New-Shortcut -Path $desktopLink -Target $installedExe -Description "$AppName game launcher"
    $shortcuts.Add($desktopLink)
    Write-Step 'Desktop shortcut'
}

# ---------------------------------------------------------------- the manifest

# Written before the registry entry, so an uninstall started after any later
# failure still knows the full list of files to remove.
foreach ($shortcut in $shortcuts) {
    $installed.Add($shortcut)
}

$installed.Add($ManifestName)
Set-Content -LiteralPath $manifestPath -Value $installed -Encoding UTF8

# ------------------------------------------------------- Add/Remove Programs

$uninstallScript = Join-Path $InstallRoot 'Uninstall-Don.ps1'
$uninstallCommand = "`"$PSHOME\powershell.exe`" -NoProfile -ExecutionPolicy Bypass -File `"$uninstallScript`""

if (-not (Test-Path -LiteralPath $uninstallScript)) {
    Write-Warning "Uninstall-Don.ps1 was not in the payload, so $AppName will not appear in Add/Remove Programs."
}
else {
    $null = New-Item -Path $registryPath -Force

    $sizeKb = [int](($payload | Measure-Object -Property Length -Sum).Sum / 1KB)

    $values = @{
        DisplayName          = $AppName
        DisplayVersion       = $version
        Publisher            = $Publisher
        DisplayIcon          = "$installedExe,0"
        InstallLocation      = $InstallRoot
        UninstallString      = $uninstallCommand
        QuietUninstallString = "$uninstallCommand -Silent"
        NoModify             = 1
        NoRepair             = 1
        EstimatedSize        = $sizeKb
    }

    foreach ($name in $values.Keys) {
        $type = if ($values[$name] -is [int]) { 'DWord' } else { 'String' }
        $null = New-ItemProperty -Path $registryPath -Name $name -Value $values[$name] -PropertyType $type -Force
    }

    Write-Step 'Registered in Add/Remove Programs'
}

# -------------------------------------------------------------------- done

if (-not $Silent) {
    Write-Host "`n$AppName $version is installed." -ForegroundColor Green
    Write-Host "  $installedExe"
    Write-Host "`nTo remove it, use Add/Remove Programs, or run:"
    Write-Host "  $uninstallCommand`n"
}

if ($Launch) {
    Write-Step "Starting $AppName"
    Start-Process -FilePath $installedExe -WorkingDirectory $InstallRoot
}
