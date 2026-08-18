<#
.SYNOPSIS
    Uploads a built release to the VPS, where Caddy serves it at /download.

.DESCRIPTION
    Copies the archive, its checksum and release.json to the dist directory that
    compose.yml mounts into Caddy, then verifies the upload two ways: the VPS
    re-checks the SHA-256 against the file it received, and the archive is
    fetched over HTTPS to confirm the route actually serves it.

    Both checks matter. sha256sum proves the bytes survived the wire; the HTTPS
    fetch proves the volume mount, the route and the certificate are all working,
    which is the part that is wrong the first time.

    Uses the OpenSSH client that ships with Windows. If ssh and scp are on PATH,
    there is nothing to install.

.PARAMETER Server
    SSH destination, as user@host.

.PARAMETER RemotePath
    The dist directory on the VPS. Defaults to ~/game-launcher/deploy/dist.

.PARAMETER Domain
    Public name to verify against. Defaults to the host part of -Server.

.PARAMETER Version
    Which release to publish. Defaults to the newest archive in output.

.PARAMETER Port
    SSH port, if not 22.

.PARAMETER SkipVerify
    Upload without checking afterwards.

.EXAMPLE
    .\publish-release.ps1 -Server root@relay.example.com

.EXAMPLE
    .\publish-release.ps1 -Server me@vps -RemotePath /srv/don/dist -Domain don.example.com
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Server,

    [string] $RemotePath = '~/game-launcher/deploy/dist',
    [string] $Domain,
    [string] $Version,
    [int]    $Port,
    [switch] $SkipVerify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$outputRoot = Join-Path $PSScriptRoot 'output'

function Write-Section {
    param([string] $Message)
    Write-Host "`n=== $Message" -ForegroundColor Cyan
}

foreach ($tool in @('ssh', 'scp')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "'$tool' is not on PATH. Install the Windows OpenSSH Client: Settings > System > Optional features."
    }
}

if (-not (Test-Path -LiteralPath $outputRoot)) {
    throw "No output directory. Run build-release.ps1 first."
}

# ---------------------------------------------------------------- what to send

if ($Version) {
    $archive = Get-Item -LiteralPath (Join-Path $outputRoot "Don-$Version-win-x64.zip") -ErrorAction SilentlyContinue
    if (-not $archive) { throw "No archive for version $Version in '$outputRoot'." }
}
else {
    $archive = Get-ChildItem -LiteralPath $outputRoot -Filter 'Don-*-win-x64.zip' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1

    if (-not $archive) { throw "No release archive in '$outputRoot'. Run build-release.ps1 first." }
}

$checksum = Get-Item -LiteralPath "$($archive.FullName).sha256" -ErrorAction SilentlyContinue
$manifest = Get-Item -LiteralPath (Join-Path $outputRoot 'release.json') -ErrorAction SilentlyContinue

if (-not $checksum) { throw "'$($archive.Name).sha256' is missing. Re-run build-release.ps1." }

$payload = @($archive, $checksum)
if ($manifest) { $payload += $manifest }

# A setup executable, if Inno Setup produced one, travels with the archive.
$setup = Get-ChildItem -LiteralPath $outputRoot -Filter 'Don-Setup-*.exe' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($setup) { $payload += $setup }

if (-not $Domain) { $Domain = ($Server -split '@')[-1] }

$sshArguments = @()
$scpArguments = @()

if ($Port) {
    # Deliberately different letters: ssh takes -p, scp takes -P.
    $sshArguments += @('-p', "$Port")
    $scpArguments += @('-P', "$Port")
}

Write-Section 'Publishing'
Write-Host "  release  $($archive.BaseName)"
Write-Host "  to       ${Server}:${RemotePath}"
Write-Host "  serving  https://$Domain/download/"

foreach ($file in $payload) {
    Write-Host "    $($file.Name)  $([math]::Round($file.Length / 1MB, 1)) MB"
}

# ------------------------------------------------------------------- upload

Write-Section 'Uploading'

# Created first: scp to a missing directory writes the file under that name
# instead, which silently produces one file called "dist".
& ssh @sshArguments $Server "mkdir -p '$RemotePath'"
if ($LASTEXITCODE -ne 0) { throw "Could not create '$RemotePath' on $Server (ssh exit $LASTEXITCODE)." }

foreach ($file in $payload) {
    Write-Host "  $($file.Name)"
    & scp @scpArguments $file.FullName "${Server}:${RemotePath}/"
    if ($LASTEXITCODE -ne 0) { throw "scp failed for '$($file.Name)' (exit $LASTEXITCODE)." }
}

if ($SkipVerify) {
    Write-Section 'Done (unverified)'
    Write-Host "  https://$Domain/download/$($archive.Name)`n"
    return
}

# ------------------------------------------------------- verify on the server

Write-Section 'Verifying the upload'

& ssh @sshArguments $Server "cd '$RemotePath' && sha256sum -c '$($checksum.Name)'"

if ($LASTEXITCODE -ne 0) {
    throw "The checksum did not match on the server. The upload is corrupt; do not hand out the link."
}

# ------------------------------------------------------------ verify the route

Write-Section 'Verifying the download URL'

$url = "https://$Domain/download/$($archive.Name)"

try {
    # HEAD, so this proves the route without pulling seventy megabytes back down.
    $response = Invoke-WebRequest -Uri $url -Method Head -UseBasicParsing -TimeoutSec 30

    $served = [int64] $response.Headers['Content-Length']

    if ($served -ne $archive.Length) {
        throw "The server offers $served bytes; the local archive is $($archive.Length). Something is serving a different file."
    }

    Write-Host "  $url"
    Write-Host "  $($response.StatusCode), $([math]::Round($served / 1MB, 1)) MB"
}
catch {
    Write-Warning "The archive uploaded and its checksum matched, but $url did not respond as expected."
    Write-Warning $_.Exception.Message
    Write-Warning 'Check that compose.yml mounts ./dist and that the stack has been restarted since.'
    exit 1
}

Write-Section "Published $($archive.BaseName)"
Write-Host @"
  Send this to whoever is installing it:

    $url

  They extract it and double-click Install.cmd. Windows will warn about an
  unsigned application; there is a note about that in the archive's README.
"@
Write-Host ''
