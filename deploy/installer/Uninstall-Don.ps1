<#
.SYNOPSIS
    Removes Don.

.DESCRIPTION
    Deletes exactly the files listed in install-manifest.txt, the shortcuts, the
    Add/Remove Programs entry, and the extraction cache the single-file bundler
    leaves in TEMP.

    The library is kept unless you ask for it to go. It holds games, playtime,
    collections, achievements and, in settings.json, the only copy of the relay
    token, which the relay stores as a hash and cannot reissue. An uninstall that
    silently deleted that would be unforgivable, so it is offered and defaults to
    no.

    Working from the manifest rather than deleting the install directory means an
    uninstall can only ever remove what the install put there. If Don was
    installed into a folder that already had other things in it, they survive.

.PARAMETER InstallRoot
    Where Don is installed. Defaults to this script's own folder.

.PARAMETER AllUsers
    Remove a per-machine install. Requires an elevated session.

.PARAMETER RemoveLibrary
    Also delete %LOCALAPPDATA%\Don. Irreversible.

.PARAMETER KeepLibrary
    Keep the library without being asked. Implied by -Silent.

.PARAMETER Silent
    Never prompt.

.EXAMPLE
    .\Uninstall-Don.ps1

.EXAMPLE
    .\Uninstall-Don.ps1 -Silent -RemoveLibrary
    Unattended removal of the application and all of its data.
#>
[CmdletBinding()]
param(
    [string] $InstallRoot,
    [switch] $AllUsers,
    [switch] $RemoveLibrary,
    [switch] $KeepLibrary,
    [switch] $Silent,

    # Set when this script has already relaunched itself out of the install
    # directory. Not for callers.
    [switch] $Relaunched
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$AppName      = 'Don'
$ExeName      = 'Don.exe'
$ManifestName = 'install-manifest.txt'
$RegistryKey  = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\Don'

function Write-Step {
    param([string] $Message)
    if (-not $Silent) { Write-Host "  $Message" }
}

function Test-Elevated {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not $InstallRoot) { $InstallRoot = $PSScriptRoot }

if (-not (Test-Path -LiteralPath $InstallRoot)) {
    Write-Host "$AppName is not installed at '$InstallRoot'. Nothing to do."
    return
}

$InstallRoot = (Resolve-Path -LiteralPath $InstallRoot).ProviderPath

# ----------------------------------------------- step out of the way first

# This script is one of the files it has to delete, and PowerShell holds the
# file open while it runs. So it copies itself to TEMP, restarts there, and the
# copy does the work, which is why the last thing removed can be the directory
# this started in.
if (-not $Relaunched -and $PSScriptRoot -and ($PSScriptRoot -ieq $InstallRoot)) {
    $staging = Join-Path $env:TEMP ("don-uninstall-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $null = New-Item -ItemType Directory -Path $staging -Force

    $copy = Join-Path $staging 'Uninstall-Don.ps1'
    Copy-Item -LiteralPath $PSCommandPath -Destination $copy -Force

    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$copy`"",
        '-InstallRoot', "`"$InstallRoot`"", '-Relaunched'
    )

    if ($AllUsers)      { $arguments += '-AllUsers' }
    if ($RemoveLibrary) { $arguments += '-RemoveLibrary' }
    if ($KeepLibrary)   { $arguments += '-KeepLibrary' }
    if ($Silent)        { $arguments += '-Silent' }

    $process = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') `
                             -ArgumentList $arguments -PassThru -Wait -NoNewWindow

    # The staging copy cannot delete itself either, but TEMP is swept by Windows
    # and this is one small file.
    exit $process.ExitCode
}

if ($AllUsers -and -not (Test-Elevated)) {
    throw '-AllUsers removes files from Program Files and keys from the machine registry. Re-run this from an elevated PowerShell session.'
}

if (-not $Silent) { Write-Host "`nRemoving $AppName" -ForegroundColor Cyan }
Write-Step "from $InstallRoot"

# ------------------------------------------------------------ stop it running

$installedExe = Join-Path $InstallRoot $ExeName

$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and ($_.Path -ieq $installedExe) } catch { $false }
})

if ($running.Count -gt 0) {
    Write-Step "Closing $AppName"

    foreach ($process in $running) { $null = $process.CloseMainWindow() }
    $null = $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue

    foreach ($process in $running) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    }

    # The file lock outlives the process by a moment, and a delete attempted in
    # that window fails on a process that has already gone.
    Start-Sleep -Milliseconds 500
}

# ---------------------------------------------------------------- the files

$manifestPath = Join-Path $InstallRoot $ManifestName
$failed = New-Object System.Collections.Generic.List[string]

if (Test-Path -LiteralPath $manifestPath) {
    $entries = @(Get-Content -LiteralPath $manifestPath -Encoding UTF8 | Where-Object { $_ })
    Write-Step "Removing $($entries.Count) recorded file(s)"

    foreach ($entry in $entries) {
        # Shortcuts are recorded as absolute paths and live outside the install
        # directory; payload files are recorded relative to it.
        $target = if ([System.IO.Path]::IsPathRooted($entry)) {
            $entry
        } else {
            Join-Path $InstallRoot $entry
        }

        if (-not (Test-Path -LiteralPath $target)) { continue }

        try {
            Remove-Item -LiteralPath $target -Force
        }
        catch {
            $failed.Add($target)
        }
    }
}
else {
    # An install from a release before manifests, or one whose manifest was
    # deleted. Only the files this project is known to install are removed, and
    # anything else in the directory is left alone.
    Write-Step 'No manifest found; removing the known payload only'

    foreach ($name in @($ExeName, 'Install-Don.ps1', 'Uninstall-Don.ps1', 'Install.cmd', 'Uninstall.cmd')) {
        $target = Join-Path $InstallRoot $name
        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
        }
    }

    foreach ($folder in @('tools')) {
        $target = Join-Path $InstallRoot $folder
        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # Shortcuts are not discoverable without a manifest, so they are removed by
    # the names the installer uses.
    foreach ($root in @(
        (Join-Path $env:APPDATA    'Microsoft\Windows\Start Menu\Programs'),
        (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'),
        [Environment]::GetFolderPath('Desktop'),
        (Join-Path $env:PUBLIC 'Desktop'))) {

        $link = Join-Path $root "$AppName.lnk"
        if (Test-Path -LiteralPath $link) {
            Remove-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue
        }
    }
}

# Empty directories left behind, deepest first so a parent is only considered
# once its children are gone.
if (Test-Path -LiteralPath $InstallRoot) {
    $directories = @(Get-ChildItem -LiteralPath $InstallRoot -Recurse -Directory |
        Sort-Object { $_.FullName.Length } -Descending)

    foreach ($directory in $directories) {
        if (-not (Get-ChildItem -LiteralPath $directory.FullName -Force)) {
            Remove-Item -LiteralPath $directory.FullName -Force -ErrorAction SilentlyContinue
        }
    }

    $remaining = @(Get-ChildItem -LiteralPath $InstallRoot -Force)

    if ($remaining.Count -eq 0) {
        Remove-Item -LiteralPath $InstallRoot -Force -ErrorAction SilentlyContinue
    }
    else {
        # Braced because "$InstallRoot:" parses as a drive-qualified variable
        # reference, which is a parse error and takes the whole script with it.
        Write-Step "Leaving ${InstallRoot} - it still holds $($remaining.Count) file(s) this install did not create"
    }
}

# --------------------------------------------------------- extraction cache

# A single-file build unpacks itself here on first run. Left behind it is a few
# hundred megabytes of nothing.
$extraction = Join-Path $env:TEMP ".net\$AppName"

if (Test-Path -LiteralPath $extraction) {
    Write-Step 'Removing the extraction cache'
    Remove-Item -LiteralPath $extraction -Recurse -Force -ErrorAction SilentlyContinue
}

# ------------------------------------------------------------- the registry

$registryHive = if ($AllUsers) { 'HKLM:' } else { 'HKCU:' }
$registryPath = Join-Path $registryHive $RegistryKey

if (Test-Path -LiteralPath $registryPath) {
    Remove-Item -LiteralPath $registryPath -Recurse -Force
    Write-Step 'Removed the Add/Remove Programs entry'
}

# ------------------------------------------------------------- the library

$library = Join-Path $env:LOCALAPPDATA $AppName

if (Test-Path -LiteralPath $library) {
    $size = [math]::Round((Get-ChildItem -LiteralPath $library -Recurse -File -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum / 1MB, 1)

    $remove = $RemoveLibrary.IsPresent

    if (-not $remove -and -not $KeepLibrary -and -not $Silent) {
        Write-Host "`nYour library is still at:" -ForegroundColor Yellow
        Write-Host "  $library  ($size MB)"
        Write-Host '  Games, playtime, collections, achievements, and your relay token.'
        Write-Host '  The relay stores that token as a hash and cannot reissue it.'

        $answer = Read-Host "`n  Delete it as well? [y/N]"
        $remove = ($answer -match '^[Yy]')
    }

    if ($remove) {
        Remove-Item -LiteralPath $library -Recurse -Force
        Write-Step 'Removed the library'
    }
    else {
        Write-Step "Kept the library at $library"
    }
}

# -------------------------------------------------------------------- done

if ($failed.Count -gt 0) {
    Write-Warning "$($failed.Count) file(s) could not be removed, most likely still in use:"
    foreach ($path in $failed) { Write-Warning "  $path" }
    exit 1
}

if (-not $Silent) {
    Write-Host "`n$AppName has been removed.`n" -ForegroundColor Green
}
