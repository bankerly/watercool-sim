param([string]$Exe, [string]$Cer, [string]$Dir)
$ErrorActionPreference = 'Stop'
if (-not $Exe) {
  if (-not $Dir) { $Dir = Split-Path -Parent $PSScriptRoot }
  $Exe = (Get-ChildItem -LiteralPath $Dir -Filter '*.exe' | Sort-Object Length -Descending | Select-Object -First 1).FullName
}
if (-not $Cer) { $Cer = Join-Path (Split-Path -Parent $Exe) 'WaterCoolSim-selfsigned.cer' }
$subject = 'CN=WaterCoolSim Local Code Signing'
try {
  $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject } | Select-Object -First 1
  if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature -FriendlyName 'WaterCoolSim Local Signing' -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}')
  }
  Export-Certificate -Cert $cert -FilePath $Cer -Force | Out-Null
  $c2 = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($Cer)
  foreach ($sn in @('Root','TrustedPublisher')) {
    $st = New-Object System.Security.Cryptography.X509Certificates.X509Store($sn,'CurrentUser')
    $st.Open('ReadWrite'); $st.Add($c2); $st.Close()
  }
  Unblock-File -LiteralPath $Exe -ErrorAction SilentlyContinue
  $sig2 = $null
  try { $sig2 = Set-AuthenticodeSignature -LiteralPath $Exe -Certificate $cert -TimestampServer "http://timestamp.digicert.com" } catch { $sig2 = $null }
  if (-not $sig2) { Set-AuthenticodeSignature -LiteralPath $Exe -Certificate $cert | Out-Null }
  $sig = Get-AuthenticodeSignature -LiteralPath $Exe
  'signed: ' + $sig.Status + '  (' + $sig.SignerCertificate.Subject + ')'
} catch { 'sign FAILED: ' + $_.Exception.Message }