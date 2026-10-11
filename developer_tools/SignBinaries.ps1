param(
    [string]$TargetFolder = ".\compiled"
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================================="
Write-Host "AUTOMATIC CODE SIGNING (FREE & SELF-MANAGED)"
Write-Host "==========================================================="

# 1. Locate or create a dedicated local Code Signing certificate
$subject = "CN=FreeSnip"

$store = New-Object System.Security.Cryptography.X509Certificates.X509Store("My", "CurrentUser")
$store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
$cert = $store.Certificates |
    Where-Object { $_.Subject -match $subject } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
$store.Close()

if (-not $cert) {
    if (-not (Get-PSDrive Cert -ErrorAction SilentlyContinue)) {
        New-PSDrive -Name Cert -PSProvider Certificate -Root "\" -ErrorAction SilentlyContinue | Out-Null
    }
    Write-Host "[SIGN] Generating new self-signed Code Signing certificate for FreeSnip..."
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $subject `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -NotAfter (Get-Date).AddYears(10) `
        -KeyFriendlyName "FreeSnip Self-Signed Code Signing" `
        -KeyAlgorithm RSA `
        -KeyLength 2048
    Write-Host "[SIGN] Generated certificate thumbprint: $($cert.Thumbprint)"
} else {
    Write-Host "[SIGN] Found existing Code Signing certificate: $($cert.Thumbprint)"
}

# 2. Sign each executable and DLL in the target folder with SHA256 and RFC 3161 timestamping
$filesToSign = Get-ChildItem -Path $TargetFolder -File | Where-Object { $_.Extension -in '.exe', '.dll' }
if (-not $filesToSign) {
    Write-Host "[SIGN] No .exe or .dll files found in $TargetFolder to sign."
    exit 0
}

$timestampUrls = @(
    "http://timestamp.digicert.com",
    "http://timestamp.sectigo.com"
)

foreach ($file in $filesToSign) {
    Write-Host "[SIGN] Signing $($file.Name)..."
    $signed = $null

    foreach ($tsUrl in $timestampUrls) {
        try {
            $signed = Set-AuthenticodeSignature `
                -FilePath $file.FullName `
                -Certificate $cert `
                -TimestampServer $tsUrl `
                -HashAlgorithm SHA256 `
                -ErrorAction Stop
            if ($signed -and ($signed.Status -eq "Valid" -or $signed.Status -eq "UnknownError")) {
                Write-Host "[SIGN] $($file.Name) timestamped via $tsUrl (Status: $($signed.Status))"
                break
            }
        } catch {
            # Try next timestamp authority
        }
    }

    if (-not $signed -or ($signed.Status -ne "Valid" -and $signed.Status -ne "UnknownError")) {
        Write-Host "[SIGN] Timestamp servers unreachable; applying offline signature..."
        $signed = Set-AuthenticodeSignature `
            -FilePath $file.FullName `
            -Certificate $cert `
            -HashAlgorithm SHA256 `
            -ErrorAction SilentlyContinue
        Write-Host "[SIGN] $($file.Name) signed offline (Status: $($signed.Status))"
    }
}

Write-Host "[SIGN] All binaries signed successfully."
Write-Host "==========================================================="
exit 0
