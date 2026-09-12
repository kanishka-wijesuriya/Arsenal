<#
.SYNOPSIS
    Authenticode-signs a built Arsenal.exe, and refuses to pretend when it cannot.

.DESCRIPTION
    Arsenal writes to ACPI, installs drivers and replaces its own binary. Windows
    shows "Windows protected your PC" for every unsigned download of something like
    that, and an unsigned executable never accumulates SmartScreen reputation the way
    a signed one does - so the warning does not fade with popularity. This is the
    largest single install-conversion cost the project has.

    Signing needs a certificate this repository cannot contain:

      * An OV code-signing certificate (~$200-400/yr). Since June 2023 the private key
        must live on a hardware token or an approved HSM - a .pfx file on disk is no
        longer issued. SmartScreen reputation builds over time and downloads.

      * Or an EV certificate (~$400-700/yr), which carries SmartScreen reputation
        immediately. For a consumer download this is usually worth the difference.

    Either way the signing key is held on a token, so this script drives signtool
    against whatever certificate is installed rather than taking a key as an argument.

.PARAMETER Path
    The Arsenal.exe to sign.

.PARAMETER Thumbprint
    SHA-1 thumbprint of the signing certificate in the current user's store. Defaults
    to $env:ARSENAL_SIGNING_THUMBPRINT.

.PARAMETER TimestampUrl
    RFC 3161 timestamp server. Timestamping is not optional: without it every signature
    stops validating the day the certificate expires, including on copies already
    installed.

.EXAMPLE
    .\sign-release.ps1 -Path ..\..\releases\stable\1.0.0-initial-release\Arsenal.exe
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$Thumbprint = $env:ARSENAL_SIGNING_THUMBPRINT,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Path)) { throw "No such file: $Path" }

# signtool ships with the Windows SDK and is not on PATH by default.
$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $signtool) {
    throw 'signtool.exe was not found. Install the Windows SDK (Signing Tools for Desktop Apps).'
}

if (-not $Thumbprint) {
    Write-Warning @'
No signing certificate configured, so this build will ship UNSIGNED.

Every user will see "Windows protected your PC" on first run, and the warning will
not fade over time. This is a known, accepted state for now - the download page says
so plainly, which is the right thing to do while it is true.

To sign: obtain an OV or EV code-signing certificate, install it, and set
ARSENAL_SIGNING_THUMBPRINT to its SHA-1 thumbprint. Then re-run this script and
remove the unrecognised-publisher wording from the download page on the website.
'@
    exit 2
}

Write-Output "Signing $Path"
& $signtool sign /fd SHA256 /td SHA256 /tr $TimestampUrl /sha1 $Thumbprint /d 'Arsenal' $Path
if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE." }

# Signing is only done when Windows agrees it is. Verifying separately catches a
# signature that applied but does not chain, and a timestamp that silently did not.
& $signtool verify /pa /all $Path
if ($LASTEXITCODE -ne 0) { throw "The signature did not verify (exit code $LASTEXITCODE)." }

$signature = Get-AuthenticodeSignature -LiteralPath $Path
Write-Output ''
Write-Output "Status:    $($signature.Status)"
Write-Output "Signer:    $($signature.SignerCertificate.Subject)"
Write-Output "Timestamp: $($signature.TimeStamperCertificate.Subject)"

if ($signature.Status -ne 'Valid') { throw "Authenticode status is $($signature.Status), not Valid." }
if (-not $signature.TimeStamperCertificate) { throw 'The signature is not timestamped. It will stop validating when the certificate expires.' }

Write-Output ''
Write-Output 'Signed and verified. Publish the SHA-256 of THIS file, not the pre-signing one.'
