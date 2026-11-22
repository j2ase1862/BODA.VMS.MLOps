<#
.SYNOPSIS
  사내망 전용 https 인증서를 만들고(서버 PC), 그 공개 인증서를 신뢰 목록에 넣는다(접속하는 PC).

.DESCRIPTION
  BODA VMS 의 서버(생산 관리 웹 · MLOps)를 라인 PC 와 다른 PC 에 두고 https 로 쓸 때,
  공인 인증서나 사내 인증 기관이 없는 현장을 위한 보조 스크립트다. 두 가지 일을 한다.

  [서버 PC] 인자 없이 실행 — 인증서를 만든다
    1. 자체 서명 서버 인증서를 만들어 "로컬 컴퓨터\개인"(Cert:\LocalMachine\My)에 넣는다.
       인증서에는 이 PC 의 이름 · 전체 이름 · IPv4 주소 · localhost 가 들어간다
       (-DnsName / -IpAddress 로 더 넣을 수 있다). 개인 키는 이 PC 밖으로 나가지 않는다.
    2. 공개 인증서(.cer — 개인 키 없음)를 파일로 내보낸다. 이 파일을 접속하는 PC 로 가져간다.
    3. 설치 스크립트에 줄 지문(thumbprint)을 화면에 보여 준다.

  [접속하는 PC — 라인 PC 등] -ImportCer <파일> — 그 인증서를 믿게 한다
    공개 인증서를 "로컬 컴퓨터\신뢰할 수 있는 루트 인증 기관"(Cert:\LocalMachine\Root)에 넣는다.
    이 스크립트 없이 해도 된다:  certutil -addstore -f Root <파일>.cer

  인증서 검증을 끄는 방식이 아니다 — 접속하는 쪽(VMS · 브라우저 · 학습 워커)은 Windows 의
  신뢰 목록으로 인증서를 그대로 검증하고, 이 인증서 하나만 그 목록에 더한다.

  주의
  - 주소에 쓰는 이름이나 IP 가 인증서에 들어 있어야 한다. 서버의 IP 가 바뀌면(DHCP) 그 IP 로는
    접속되지 않는다 — 고정 IP 를 쓰거나 PC 이름으로 접속한다.
  - 만료일(-ValidYears, 기본 5년)이 지나면 접속이 모두 막힌다. 만료 전에 새로 만들어
    서버를 다시 설치하고, 새 .cer 를 접속하는 PC 마다 다시 넣는다.
  - 같은 PC 에 두 서버(웹 · MLOps)를 함께 두면 인증서 하나를 같이 쓴다.

  이 파일은 BODA.VMS.Web(deploy\New-HttpsCertificate.ps1)과 BODA.VMS.MLOps
  (scripts\new-https-certificate.ps1)에 같은 내용으로 들어 있다(예시의 파일 이름만 다르다).
  한쪽을 고치면 다른 쪽도 맞춘다.

.PARAMETER DnsName
  인증서에 더 넣을 이름 (예: 사내 DNS 별칭). 이 PC 의 이름과 localhost 는 자동으로 들어간다.

.PARAMETER IpAddress
  인증서에 더 넣을 IPv4 주소. 이 PC 에 지금 붙어 있는 IPv4 주소와 127.0.0.1 은 자동으로 들어간다.

.PARAMETER ValidYears
  유효 기간(년). 기본 5.

.PARAMETER FriendlyName
  인증서 관리 화면에 보이는 이름. 기본 "BODA VMS HTTPS".

.PARAMETER ExportPath
  내보낼 공개 인증서 파일. 기본은 현재 폴더의 boda-vms-https-<PC 이름>.cer.

.PARAMETER Exportable
  개인 키를 내보낼 수 있게 만든다(서버 PC 교체에 대비해 .pfx 로 백업해 두려는 경우). 기본은 내보낼 수 없음.

.PARAMETER TrustOnThisPc
  만든 인증서를 이 PC 의 신뢰 목록에도 넣는다. 서버 PC 의 브라우저로 https 주소를 열거나,
  같은 PC 의 MLOps 서버가 웹을 https 주소로 부르게 할 때 필요하다.

.PARAMETER ImportCer
  접속하는 PC 에서 쓴다 — 서버에서 가져온 .cer 파일을 신뢰 목록에 넣고 끝낸다.

.EXAMPLE
  # 서버 PC (관리자 PowerShell)
  .\scripts\new-https-certificate.ps1

.EXAMPLE
  # 서버 PC — 사내 DNS 별칭과 예비 IP 도 넣고, 이 PC 에서도 신뢰
  .\scripts\new-https-certificate.ps1 -DnsName vms-server.factory.local -IpAddress 192.168.0.20 -TrustOnThisPc

.EXAMPLE
  # 라인 PC (관리자 PowerShell)
  .\scripts\new-https-certificate.ps1 -ImportCer D:\boda-vms-https-SERVER01.cer

.NOTES
  요구사항: 관리자 권한, Windows 10 / Windows Server 2016 이상 (Windows PowerShell 5.1).
  되돌리기: 인증서 관리(certlm.msc)에서 지운다 — 서버는 "개인"의 "BODA VMS HTTPS",
  접속하는 PC 는 "신뢰할 수 있는 루트 인증 기관"에서 발급 대상이 서버 PC 이름인 것.
#>

[CmdletBinding()]
param(
    [string[]]$DnsName = @(),
    [string[]]$IpAddress = @(),
    [int]$ValidYears = 5,
    [string]$FriendlyName = "BODA VMS HTTPS",
    [string]$ExportPath,
    [switch]$Exportable,
    [switch]$TrustOnThisPc,
    [string]$ImportCer
)

$ErrorActionPreference = "Stop"

# 중복(대소문자 무시)과 빈 값을 걷어 내고 처음 나온 순서를 지킨다.
function Select-UniqueText {
    param([string[]]$Items)
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $result = @()
    foreach ($item in $Items) {
        if ([string]::IsNullOrWhiteSpace($item)) { continue }
        $value = $item.Trim()
        if ($seen.Add($value)) { $result += $value }
    }
    return , $result
}

# 주체 대체 이름(SAN) 확장 — New-SelfSignedCertificate -TextExtension 형식.
# 이름은 DNS= 로, 주소는 IPAddress= 로 따로 넣어야 한다. IP 를 DNS 이름 칸에 넣으면
# https://<IP> 접속에서 이름이 맞지 않는다고 거부된다.
function Get-SanExtensionText {
    param([string[]]$DnsNames, [string[]]$IpAddresses)
    $parts = @()
    foreach ($name in (Select-UniqueText $DnsNames)) {
        if ($name -match '[&=\s]') { throw "이름에 쓸 수 없는 글자가 있습니다: '$name'" }
        $parts += "DNS=$name"
    }
    foreach ($ip in (Select-UniqueText $IpAddresses)) {
        $parsed = $null
        if (-not [System.Net.IPAddress]::TryParse($ip, [ref]$parsed)) { throw "IP 주소가 아닙니다: '$ip'" }
        $parts += "IPAddress=$parsed"
    }
    if ($parts.Count -eq 0) { throw "인증서에 넣을 이름이나 주소가 하나도 없습니다." }
    return "2.5.29.17={text}" + ($parts -join "&")
}

# ----- 관리자 권한 확인 -----
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "관리자 권한으로 PowerShell 을 실행해야 합니다. (로컬 컴퓨터 인증서 저장소에 씁니다)"
    exit 1
}

# ----- 접속하는 PC: 공개 인증서를 신뢰 목록에 -----
if ($ImportCer) {
    if (-not (Test-Path $ImportCer)) {
        Write-Error "파일을 찾지 못했습니다: $ImportCer"
        exit 1
    }
    $cerFile = (Resolve-Path $ImportCer).Path
    $incoming = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cerFile
    Write-Host ""
    Write-Host "신뢰 목록에 넣을 인증서" -ForegroundColor Cyan
    Write-Host "  주체  : $($incoming.Subject)"
    Write-Host "  만료  : $($incoming.NotAfter.ToString('yyyy-MM-dd'))"
    Write-Host "  지문  : $($incoming.Thumbprint)   <- 서버에서 본 지문과 같은지 확인하세요"
    Import-Certificate -FilePath $cerFile -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    Write-Host ""
    Write-Host "완료 — 이 PC 는 이제 그 서버의 https 주소를 믿습니다 (로컬 컴퓨터\신뢰할 수 있는 루트 인증 기관)." -ForegroundColor Cyan
    Write-Host "  열려 있던 VMS · 브라우저는 다시 시작하세요."
    return
}

# ----- 서버 PC: 인증서 만들기 -----
if ($ValidYears -lt 1) {
    Write-Error "-ValidYears 는 1 이상이어야 합니다."
    exit 1
}

$computer = $env:COMPUTERNAME
$fqdn = $null
try { $fqdn = [System.Net.Dns]::GetHostEntry("").HostName } catch { }
$lanIps = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike "127.*" -and $_.IPAddress -notlike "169.254.*" } |
    Select-Object -ExpandProperty IPAddress)

$names = Select-UniqueText (@($computer, $fqdn, "localhost") + $DnsName)
$ips = Select-UniqueText ($lanIps + @("127.0.0.1") + $IpAddress)
$san = Get-SanExtensionText -DnsNames $names -IpAddresses $ips

if (-not $ExportPath) { $ExportPath = Join-Path (Get-Location).Path "boda-vms-https-$computer.cer" }
# 상대 경로는 PowerShell 의 현재 폴더 기준으로 푼다(.NET 의 현재 폴더와 다를 수 있다).
$ExportPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ExportPath)
$exportDir = Split-Path -Parent $ExportPath
if (-not (Test-Path $exportDir)) {
    Write-Error "내보낼 폴더가 없습니다: $exportDir"
    exit 1
}

# 같은 주체 이름의 인증서가 이미 있으면 서버는 만료가 가장 늦은 것을 고른다 — 미리 알려 준다.
$sameName = @(Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq "CN=$computer" })
if ($sameName.Count -gt 0) {
    Write-Warning "주체 이름이 CN=$computer 인 인증서가 이미 $($sameName.Count)개 있습니다. 새로 만드는 것보다 만료가 늦은 것이 있으면 설치 스크립트가 알려 줍니다 — 쓰지 않는 것은 certlm.msc 에서 지우세요."
}

$notAfter = (Get-Date).AddYears($ValidYears)
$keyPolicy = if ($Exportable) { "Exportable" } else { "NonExportable" }
# -Type Custom: 용도를 아래 확장으로 직접 정한다 — 서버 인증(1.3.6.1.5.5.7.3.1) 하나와 주체 대체 이름.
$cert = New-SelfSignedCertificate -Type Custom `
    -Subject "CN=$computer" `
    -FriendlyName $FriendlyName `
    -CertStoreLocation Cert:\LocalMachine\My `
    -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
    -KeyUsage DigitalSignature, KeyEncipherment `
    -KeyExportPolicy $keyPolicy `
    -NotAfter $notAfter `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.1", $san)

Export-Certificate -Cert $cert -FilePath $ExportPath -Type CERT | Out-Null

if ($TrustOnThisPc) {
    Import-Certificate -FilePath $ExportPath -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
}

Write-Host ""
Write-Host "===== https 인증서 준비 완료 =====" -ForegroundColor Cyan
Write-Host "  주체        : $($cert.Subject)"
Write-Host "  들어간 이름 : $($names -join ', ')"
Write-Host "  들어간 주소 : $($ips -join ', ')"
Write-Host "  만료        : $($cert.NotAfter.ToString('yyyy-MM-dd'))   <- 이 날 전에 새로 만들어 교체해야 합니다"
Write-Host "  지문        : $($cert.Thumbprint)" -ForegroundColor Yellow
Write-Host "  공개 인증서 : $ExportPath   (개인 키 없음 — 접속하는 PC 로 가져갑니다)"
if ($TrustOnThisPc) {
    Write-Host "  이 PC 신뢰  : 넣었습니다 (로컬 컴퓨터\신뢰할 수 있는 루트 인증 기관)"
}
Write-Host ""
Write-Host "다음 할 일"
Write-Host "  1. 서버 설치 스크립트에 지문을 줍니다:  -HttpsCertThumbprint $($cert.Thumbprint)"
Write-Host "  2. 접속하는 PC(라인 PC 등)마다 공개 인증서를 신뢰 목록에 넣습니다 (관리자):"
Write-Host "       certutil -addstore -f Root $(Split-Path -Leaf $ExportPath)"
Write-Host "  3. 접속 주소는 위 '들어간 이름/주소' 가운데 하나로 씁니다 (예: https://${computer}:<https 포트>)."
