<#
.SYNOPSIS
  BODA VMS MLOps 서버를 Windows 서비스로 설치(또는 갱신·제거)한다. 관리자 PowerShell 에서 실행.

.DESCRIPTION
  1) 서버를 self-contained win-x64 로 publish (-SkipPublish 로 생략, -PublishDir 로 기존 산출물 지정)
  2) 설치 폴더로 복사 (appsettings.Production.json 과 models\ 는 보존)
  3) appsettings.Production.template.json 의 {{Port}} {{DataDir}} {{ProductionWebUrl}} {{AuthMode}} {{LogArchiveDir}} 를 채워 appsettings.Production.json 생성
  4) 서비스 등록 (자동·지연 시작, 실패 시 재시작) + 서비스 환경변수 Jwt__Key / DOTNET_ENVIRONMENT=Production
  5) 방화벽 인바운드 규칙 (라인 PC·브라우저·워커가 붙는 포트)
     -HttpsCertThumbprint 를 주면 https 포트를 열고 그 포트에 규칙을 만든다. 그때 http 포트는 이 PC 안에서만 받는다.
  6) SAM 모델(있으면) 복사, 서비스 시작, 응답 확인
  7) 남은 한 줄 안내 — 운영 웹 모드면 그쪽 CORS, 자체 계정 모드면 첫 관리자와 임시 비밀번호

  Jwt:Key 는 운영 웹(BODA.VMS.Web)과 같은 값이어야 로그인 토큰이 통한다. -JwtKey 를 주지 않으면
  환경변수 MLOPS_JWT_KEY → 서버 프로젝트 user-secrets(Jwt:Key) 순서로 찾는다. 파일에는 남기지 않는다.

  -Local 은 운영 웹이 없는 설치다. 사람이 이 서버의 계정으로 로그인하고 서명 키는 따로 만든다
  (서비스 환경변수 Auth__Local__Key). 첫 관리자(admin)와 임시 비밀번호는 서버가 만들어
  설치 폴더의 initial-admin-password.txt 에 한 번 남긴다 — 첫 로그인에서 바꾸고 그 파일을 지운다.

  https 는 선택이다(기본은 종전과 같은 http). 다른 PC 의 VMS(보안 모드 Production)는 http:// 서버 주소를
  거부하므로, 라인 PC 가 다른 PC 에서 붙는 현장은 -HttpsCertThumbprint 로 https 포트를 연다.
  인증서는 이 PC 의 "로컬 컴퓨터\개인"(Cert:\LocalMachine\My)에 미리 넣어 둔다 — 사내 전용이면
  scripts\new-https-certificate.ps1 로 만든다. 절차는 docs\서버 설치 가이드.md 의 "다른 PC 에서 https 로 쓰기".

.EXAMPLE
  .\scripts\install-server-service.ps1 -DataDir D:\BODA-MLOps -ProductionWebUrl http://localhost:5292
  .\scripts\install-server-service.ps1 -SkipPublish -PublishDir .\src\BODA.VMS.MLOps.Server\obj\publish-server
  .\scripts\install-server-service.ps1 -Local -DataDir D:\BODA-MLOps
  .\scripts\install-server-service.ps1 -DataDir D:\BODA-MLOps -ProductionWebUrl https://server01:5293 -HttpsCertThumbprint 0123ABCD...
  .\scripts\install-server-service.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "C:\Program Files\BODA Vision AI\BODA VMS MLOps Server",
    [string]$DataDir = "C:\ProgramData\BODA VMS MLOps",
    # 학습 로그 보관 폴더. 주지 않으면 $DataDir\job-logs. "none" 이면 보관하지 않는다.
    [string]$LogArchiveDir = "",
    [int]$Port = 5310,
    [string]$ProductionWebUrl = "http://localhost:5292",
    # 운영 웹의 기계용 API 키(X-API-Key). 모니터링이 검사 이력을 당겨 올 때 쓴다 —
    # 운영 웹이 키 강제 모드면 없으면 모니터링만 401 로 막힌다. 파일이 아니라 서비스 환경변수로 넣는다.
    [string]$WebApiKey = "",
    [string]$JwtKey = "",
    # 운영 웹이 없는 설치. 사람은 이 서버의 계정(아이디·비밀번호)으로 들어온다.
    # 첫 관리자와 임시 비밀번호는 서버가 만들어 설치 폴더에 한 번 남긴다.
    [switch]$Local,
    # 로컬 모드 서명 키. 주지 않으면 스크립트가 만든다. 운영 웹의 Jwt 키와 달라야 한다.
    [string]$LocalKey = "",
    # 주면 https 포트를 함께 연다 — Cert:\LocalMachine\My 에 있는 서버 인증서의 지문(thumbprint).
    # https 는 0.0.0.0:$HttpsPort 로 열리고, http($Port)는 이 PC 안(localhost)에서만 받게 바뀐다.
    # 주지 않으면 https 와 관련된 것은 아무것도 하지 않는다(종전과 같은 설치).
    [string]$HttpsCertThumbprint = "",
    [int]$HttpsPort = 5311,
    [string]$ServiceName = "BodaVmsMlops",
    [string]$PublishDir = "",
    [switch]$SkipPublish,
    [switch]$SkipFirewall,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$serverProj = Join-Path $repo "src\BODA.VMS.MLOps.Server\BODA.VMS.MLOps.Server.csproj"
$template = Join-Path $repo "src\BODA.VMS.MLOps.Server\appsettings.Production.template.json"
if (-not $PublishDir) { $PublishDir = Join-Path $repo "src\BODA.VMS.MLOps.Server\obj\publish-server" }
$exe = Join-Path $InstallDir "BODA.VMS.MLOps.Server.exe"
$ruleName = "BODA VMS MLOps ($Port/TCP)"
$httpsRuleName = "BODA VMS MLOps ($HttpsPort/TCP)"

# ── HTTPS (선택) ──
# 서버 코드는 건드리지 않는다. Kestrel 이 원래 읽는 두 가지만 준다.
#   · 수신 주소 : appsettings.Production.json 의 Urls 에 https 주소를 더한다
#   · 인증서    : 서비스 환경변수 Kestrel__Certificates__Default__* 로 저장소의 인증서를 가리킨다
# 인증서를 파일(.pfx)이 아니라 저장소에서 읽는 이유 — 파일로 주면 그 비밀번호를 설정 파일이나
# 서비스 환경변수에 적어야 한다. 저장소의 개인 키는 서비스 계정(LocalSystem)과 관리자만 쓴다.
# .pfx 로 받은 인증서는 먼저 저장소에 넣는다 (서버 설치 가이드).
# 아래 함수들은 BODA.VMS.Web 의 deploy\Install-Web-Offline.ps1 과 같은 규칙이다.

# 지문을 붙여 넣을 때 섞이는 빈칸 · 콜론 · 보이지 않는 글자를 걷어 낸다.
function ConvertTo-CertThumbprint {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return "" }
    return ($Text -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
}

# 설정 파일의 Urls 값. 지문이 없으면 템플릿이 쓰는 값 그대로다(종전과 같은 값).
# https 를 켜면 http 는 localhost 로 좁힌다 — 같은 PC 의 워커와 응답 확인은 그대로 붙고,
# 다른 PC 에서 평문으로 들어오는 길은 닫힌다.
function Get-ServerUrlsValue {
    param([Parameter(Mandatory = $true)][int]$Port, [string]$Thumbprint, [int]$HttpsPort)
    if ([string]::IsNullOrWhiteSpace($Thumbprint)) { return "http://0.0.0.0:$Port" }
    return "http://localhost:$Port;https://0.0.0.0:$HttpsPort"
}

# 주소 문자열에서 https 포트를 읽는다. 없으면 $null.
function Get-HttpsPortFromUrls {
    param([string]$Text)
    if ($Text -and $Text -match 'https://[^;\s"]*:(\d+)') { return [int]$Matches[1] }
    return $null
}

# Kestrel 과 같은 규칙: 확장 키 용도(EKU)가 없거나, 있으면 "서버 인증"이 들어 있어야 한다.
function Test-ServerAuthAllowed {
    param([Parameter(Mandatory = $true)]$Certificate)
    $ekus = @($Certificate.Extensions | Where-Object {
        $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] })
    if ($ekus.Count -eq 0) { return $true }
    foreach ($eku in $ekus) {
        foreach ($oid in $eku.EnhancedKeyUsages) {
            if ($oid.Value -eq '1.3.6.1.5.5.7.3.1') { return $true }
        }
    }
    return $false
}

# Kestrel 은 지문이 아니라 주체 이름으로 인증서를 찾는다 — 이름이 들어 있고, 개인 키가 있고,
# 서버 인증에 쓸 수 있는 것 가운데 만료가 가장 늦은 것. 같은 규칙으로 미리 골라 본다.
function Select-KestrelStoreCertificate {
    param([object[]]$Certificates, [Parameter(Mandatory = $true)][string]$SubjectName)
    return $Certificates | Where-Object {
        $_.Subject.IndexOf($SubjectName, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
        $_.HasPrivateKey -and (Test-ServerAuthAllowed -Certificate $_)
    } | Sort-Object NotAfter -Descending | Select-Object -First 1
}

# 지문으로 인증서를 찾아 서버 인증서로 쓸 수 있는지 확인하고, Kestrel 에 줄 주체 이름을 돌려준다.
# 쓸 수 없으면 이유를 적어 던진다 — 서비스에 손대기 전에 멈추기 위해서다.
function Resolve-HttpsCertificate {
    param([Parameter(Mandatory = $true)][string]$Thumbprint, [object[]]$StoreCertificates)
    $cert = $StoreCertificates | Where-Object { $_.Thumbprint -eq $Thumbprint } | Select-Object -First 1
    if (-not $cert) {
        throw "지문 $Thumbprint 인 인증서가 '로컬 컴퓨터\개인'(Cert:\LocalMachine\My)에 없습니다. 확인: Get-ChildItem Cert:\LocalMachine\My"
    }
    if (-not $cert.HasPrivateKey) {
        throw "인증서 '$($cert.Subject)' 에 개인 키가 없습니다. 공개 인증서(.cer)만 넣은 것입니다 - 개인 키가 든 .pfx 를 넣거나 new-https-certificate.ps1 로 만드세요."
    }
    $now = Get-Date
    if ($cert.NotBefore -gt $now -or $cert.NotAfter -le $now) {
        throw "인증서 '$($cert.Subject)' 의 유효 기간이 지금이 아닙니다 ($($cert.NotBefore.ToString('yyyy-MM-dd')) ~ $($cert.NotAfter.ToString('yyyy-MM-dd')))."
    }
    if (-not (Test-ServerAuthAllowed -Certificate $cert)) {
        throw "인증서 '$($cert.Subject)' 는 용도에 '서버 인증'이 없어 https 서버 인증서로 쓸 수 없습니다."
    }
    $name = $cert.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    if ([string]::IsNullOrWhiteSpace($name)) {
        throw "인증서에 주체 이름(CN)이 없어 서버가 이 인증서를 찾을 수 없습니다. 주체 이름이 있는 인증서를 쓰세요."
    }
    $picked = Select-KestrelStoreCertificate -Certificates $StoreCertificates -SubjectName $name
    if (-not $picked -or $picked.Thumbprint -ne $cert.Thumbprint) {
        $other = if ($picked) { "'$($picked.Subject)' (지문 $($picked.Thumbprint), 만료 $($picked.NotAfter.ToString('yyyy-MM-dd')))" } else { "없음" }
        throw "주체 이름 '$name' 으로 찾으면 서버는 다른 인증서를 고릅니다: $other. 같은 이름의 인증서가 여럿이면 만료가 가장 늦은 것이 쓰입니다 - 쓰지 않는 것을 저장소에서 지우거나 그 인증서의 지문을 주세요."
    }
    return [pscustomobject]@{ Certificate = $cert; SubjectName = $name }
}

# 서비스 환경변수 - Kestrel 의 기본 인증서를 저장소에서 읽게 한다.
# AllowInvalid=true 는 "서버가 자기 인증서를 고를 때 이 PC 가 그 인증서를 신뢰하는지 따지지 않는다"
# 는 뜻이다. 자체 서명 인증서는 이 PC 의 신뢰 목록에 없을 수 있고, 폐쇄망에서는 폐기 목록 확인이
# 실패할 수 있어서 켠다. 접속하는 쪽(VMS · 브라우저 · 워커)의 인증서 검증과는 무관하다 - 그쪽은 그대로 엄격하다.
function Get-KestrelCertificateEnv {
    param([Parameter(Mandatory = $true)][string]$SubjectName)
    return @(
        "Kestrel__Certificates__Default__Subject=$SubjectName",
        "Kestrel__Certificates__Default__Store=My",
        "Kestrel__Certificates__Default__Location=LocalMachine",
        "Kestrel__Certificates__Default__AllowInvalid=true"
    )
}

$HttpsCertThumbprint = ConvertTo-CertThumbprint $HttpsCertThumbprint
$httpsEnabled = [bool]$HttpsCertThumbprint
$httpsCert = $null

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "관리자 PowerShell 에서 실행하세요 (서비스 등록·방화벽·Program Files 쓰기)." }

function Stop-ServiceIfExists {
    $svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne "Stopped") {
        Write-Host "서비스 정지: $ServiceName"
        Stop-Service $ServiceName -Force
        $svc.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(60))
    }
    return [bool]$svc
}

# ── 제거 ──
if ($Uninstall) {
    if (Stop-ServiceIfExists) {
        & sc.exe delete $ServiceName | Out-Null
        Write-Host "서비스 삭제: $ServiceName"
    }
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    # https 로 설치했었다면 그 포트의 규칙도 지운다. 포트는 남아 있는 설정 파일의 Urls 에 적혀 있다.
    $oldCfg = Join-Path $InstallDir "appsettings.Production.json"
    if ((Test-Path $oldCfg) -and ((Get-Content $oldCfg -Raw -Encoding UTF8) -match '"Urls"\s*:\s*"([^"]*)"')) {
        $oldHttpsPort = Get-HttpsPortFromUrls $Matches[1]
        if ($oldHttpsPort -and $oldHttpsPort -ne $Port) {
            Get-NetFirewallRule -DisplayName "BODA VMS MLOps ($oldHttpsPort/TCP)" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        }
    }
    Write-Host "제거 완료. 설치 폴더($InstallDir)와 데이터($DataDir)는 남겨 두었습니다."
    return
}

# ── HTTPS 사전 확인 (무엇이든 바꾸기 전에) ──
if ($httpsEnabled) {
    if ($HttpsPort -eq $Port) { throw "-HttpsPort 와 -Port 가 같습니다 ($Port). 서로 다른 포트를 주세요." }
    try {
        $httpsCert = Resolve-HttpsCertificate -Thumbprint $HttpsCertThumbprint `
            -StoreCertificates @(Get-ChildItem -Path Cert:\LocalMachine\My)
    } catch {
        throw "HTTPS 인증서를 쓸 수 없습니다 - $($_.Exception.Message)"
    }
    Write-Host "HTTPS: 포트 $HttpsPort, 인증서 '$($httpsCert.SubjectName)' (만료 $($httpsCert.Certificate.NotAfter.ToString('yyyy-MM-dd')))"
}

# ── 1) publish ──
if (-not $SkipPublish) {
    Write-Host "publish → $PublishDir"
    if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }
    & dotnet publish $serverProj -c Release -r win-x64 --self-contained true -p:DebugType=none -o $PublishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 실패 (exit $LASTEXITCODE)" }
}
if (-not (Test-Path (Join-Path $PublishDir "BODA.VMS.MLOps.Server.exe"))) { throw "publish 산출물이 없습니다: $PublishDir" }

# ── 서명 키 ──
# 모드마다 필요한 키가 다르다. Web 모드는 운영 웹과 같은 Jwt:Key, Local 모드는 우리만 쓰는 Auth:Local:Key.
if (-not $JwtKey) { $JwtKey = $env:MLOPS_JWT_KEY }
if (-not $JwtKey) {
    $line = & dotnet user-secrets list --project $serverProj 2>$null | Where-Object { $_ -like "Jwt:Key = *" } | Select-Object -First 1
    if ($line) { $JwtKey = $line.Substring("Jwt:Key = ".Length).Trim() }
}

if ($Local) {
    # 이미 설치된 서버라면 쓰던 키를 그대로 둔다 — 키가 바뀌면 그 순간 모두가 다시 로그인해야 한다.
    if (-not $LocalKey) {
        $existing = (Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment -ErrorAction SilentlyContinue).Environment
        $kept = ($existing | Where-Object { $_ -like "Auth__Local__Key=*" } | Select-Object -First 1)
        if ($kept) {
            $LocalKey = $kept -replace '^Auth__Local__Key=', ''
            Write-Host "로컬 서명 키: 기존 설정 값을 유지합니다"
        }
    }
    if (-not $LocalKey) {
        $bytes = New-Object byte[] 48
        [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        $LocalKey = [Convert]::ToBase64String($bytes)
        Write-Host "로컬 서명 키를 새로 만들었습니다 (서비스 환경변수에만 둡니다)"
    }
    if ($LocalKey.Length -lt 32) { throw "-LocalKey 는 32자 이상이어야 합니다." }
    if ($JwtKey -and $LocalKey -ceq $JwtKey) { throw "-LocalKey 는 Jwt:Key 와 달라야 합니다 (발급 주체가 다릅니다)." }
}
elseif (-not $JwtKey -or $JwtKey.Length -lt 32) {
    throw "Jwt:Key(32자 이상)가 필요합니다. -JwtKey, 환경변수 MLOPS_JWT_KEY, 또는 서버 프로젝트 user-secrets 로 주세요. 운영 웹과 같은 키여야 합니다. 운영 웹이 없는 설치라면 -Local 을 쓰세요."
}

# ── 2) 파일 복사 (설정·모델 보존) ──
$existed = Stop-ServiceIfExists
New-Item -ItemType Directory -Force $InstallDir | Out-Null
New-Item -ItemType Directory -Force $DataDir, (Join-Path $DataDir "storage"), (Join-Path $DataDir "logs") | Out-Null
& robocopy $PublishDir $InstallDir /MIR /NFL /NDL /NJH /NJS /NP /XF appsettings.Production.json /XD models | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy 실패 (exit $LASTEXITCODE)" }

# SAM 모델: 저장소에 없는 파일이라 개발 PC 의 서버 프로젝트 폴더에서 가져온다 (없으면 기능만 꺼진다)
$samSrc = Join-Path $repo "src\BODA.VMS.MLOps.Server\models\sam"
$samDst = Join-Path $InstallDir "models\sam"
if ((Test-Path $samSrc) -and -not (Test-Path (Join-Path $samDst "mobile_sam_encoder.onnx"))) {
    New-Item -ItemType Directory -Force $samDst | Out-Null
    Copy-Item (Join-Path $samSrc "*") $samDst -Force
    Write-Host "SAM 모델 복사: $samDst"
}

# ── 3) appsettings.Production.json ──
$cfg = Get-Content $template -Raw -Encoding UTF8
$cfg = $cfg.Replace("{{Port}}", "$Port").Replace("{{DataDir}}", $DataDir.Replace("\", "/")).Replace("{{ProductionWebUrl}}", $ProductionWebUrl)
$cfg = $cfg.Replace("{{AuthMode}}", $(if ($Local) { "Local" } else { "Web" }))
if (-not $LogArchiveDir) { $LogArchiveDir = Join-Path $DataDir "job-logs" }
if ($LogArchiveDir -eq "none") { $LogArchiveDir = "" }
$cfg = $cfg.Replace("{{LogArchiveDir}}", $LogArchiveDir.Replace("\", "/"))
$cfgPath = Join-Path $InstallDir "appsettings.Production.json"
# 이전 설치가 https 를 열어 두었다면 그 포트를 기억해 둔다 — 이번에 끄거나 포트를 바꾸면 그때 만든
# 방화벽 규칙을 지워야 한다. 덮어쓰기 전에 읽는다.
$oldHttpsPort = $null
if ((Test-Path $cfgPath) -and ((Get-Content $cfgPath -Raw -Encoding UTF8) -match '"Urls"\s*:\s*"([^"]*)"')) {
    $oldHttpsPort = Get-HttpsPortFromUrls $Matches[1]
}
# https 를 켤 때만 Urls 줄을 바꾼다. 켜지 않으면 템플릿이 만든 내용 그대로다.
if ($httpsEnabled) {
    $urlsLine = "`"Urls`": `"$(Get-ServerUrlsValue -Port $Port)`""
    if (-not $cfg.Contains($urlsLine)) { throw "템플릿의 Urls 줄이 예상과 다릅니다 ($urlsLine). appsettings.Production.template.json 을 확인하세요." }
    $cfg = $cfg.Replace($urlsLine, "`"Urls`": `"$(Get-ServerUrlsValue -Port $Port -Thumbprint $HttpsCertThumbprint -HttpsPort $HttpsPort)`"")
}
[IO.File]::WriteAllText($cfgPath, $cfg, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "설정: $cfgPath"

# ── 4) 서비스 ──
if (-not $existed) {
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName "BODA VMS MLOps Server" `
        -Description "BODA VMS MLOps - 모델 레지스트리·데이터셋·라벨링·학습 작업·모니터링 관리 서버 (포트 $Port)" `
        -StartupType Automatic | Out-Null
    Write-Host "서비스 등록: $ServiceName"
} else {
    & sc.exe config $ServiceName binPath= "`"$exe`"" | Out-Null
}
& sc.exe config $ServiceName start= delayed-auto | Out-Null
& sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/10000/restart/30000 | Out-Null
# 서비스 전용 환경변수: Jwt 키·Web API 키는 파일에 남기지 않는다
$envKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
# -WebApiKey 를 안 주면 이미 설정된 값을 그대로 둔다. 이 블록이 Environment 를 통째로 다시 쓰기
# 때문에, 보존하지 않으면 다음 재배포에서 키가 조용히 사라져 모니터링만 401 로 막힌다.
if (-not $WebApiKey) {
    $existingEnv = (Get-ItemProperty -Path $envKey -Name Environment -ErrorAction SilentlyContinue).Environment
    $kept = ($existingEnv | Where-Object { $_ -like "Monitoring__ApiKey=*" } | Select-Object -First 1)
    if ($kept) {
        $WebApiKey = $kept -replace '^Monitoring__ApiKey=', ''
        Write-Host "Web API 키: 기존 설정 값을 유지합니다"
    }
}
$serviceEnv = @(
    "DOTNET_ENVIRONMENT=Production",
    "ASPNETCORE_ENVIRONMENT=Production"
)
# Web 모드는 운영 웹과 같은 키로 토큰을 검증하고, Local 모드는 우리 키로 발급·검증한다.
# 로컬 모드에서도 Jwt__Key 는 있으면 남긴다 — 모니터링이 운영 웹으로 나갈 때 그 키를 쓴다.
if ($JwtKey) { $serviceEnv += "Jwt__Key=$JwtKey" }
if ($Local) { $serviceEnv += "Auth__Local__Key=$LocalKey" }
if ($WebApiKey) { $serviceEnv += "Monitoring__ApiKey=$WebApiKey" }
# 이 블록이 Environment 를 통째로 다시 쓰므로, https 없이 다시 설치하면 아래 값도 함께 사라진다.
if ($httpsEnabled) { $serviceEnv += Get-KestrelCertificateEnv -SubjectName $httpsCert.SubjectName }
Set-ItemProperty -Path $envKey -Name Environment -Type MultiString -Value $serviceEnv

# ── 5) 방화벽 ──
if (-not $SkipFirewall) {
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    # 이전 설치가 열어 둔 https 규칙은 지운다 (https 를 쓴 적이 없으면 $oldHttpsPort 가 비어 아무것도 하지 않는다)
    if ($oldHttpsPort -and $oldHttpsPort -ne $Port) {
        Get-NetFirewallRule -DisplayName "BODA VMS MLOps ($oldHttpsPort/TCP)" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    }
    # https 를 켜면 다른 PC 는 https 포트로만 들어온다 — http 포트 규칙은 다시 만들지 않는다.
    $openRule = $ruleName
    $openPort = $Port
    if ($httpsEnabled) {
        $openRule = $httpsRuleName
        $openPort = $HttpsPort
        Get-NetFirewallRule -DisplayName $openRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    }
    New-NetFirewallRule -DisplayName $openRule -Direction Inbound -Protocol TCP -LocalPort $openPort -Action Allow -Profile Any | Out-Null
    Write-Host "방화벽 인바운드 허용: $openRule"
}

# ── 6) 시작·확인 ──
Start-Service $ServiceName
$ok = $false
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep 2
    try { $r = Invoke-WebRequest "http://localhost:$Port/" -UseBasicParsing -TimeoutSec 3; if ($r.StatusCode -eq 200) { $ok = $true; break } } catch { }
    if ((Get-Service $ServiceName).Status -eq "Stopped") { break }
}
if (-not $ok) { throw "서비스가 $Port 에서 응답하지 않습니다. 로그: $DataDir\logs" }

Write-Host ""
Write-Host "설치 완료"
if ($httpsEnabled) {
    Write-Host "  화면/API : http://localhost:$Port  (이 PC 에서만)"
    Write-Host "  HTTPS    : https://<이 PC 이름 또는 IP>:$HttpsPort  (다른 PC 의 브라우저 · 라인 PC · 워커)"
    Write-Host "             인증서 '$($httpsCert.SubjectName)' - 만료 $($httpsCert.Certificate.NotAfter.ToString('yyyy-MM-dd')), 지문 $HttpsCertThumbprint"
    Write-Host "             주소에 쓰는 이름/IP 가 인증서에 들어 있어야 하고, 접속하는 PC 가 이 인증서를 신뢰해야 합니다 (서버 설치 가이드)"
} else {
    Write-Host "  화면/API : http://localhost:$Port  (다른 PC 에서는 http://<이 PC IP>:$Port)"
}
Write-Host "  서비스   : $ServiceName (자동·지연 시작, LocalSystem)"
Write-Host "  설치 폴더: $InstallDir"
Write-Host "  데이터   : $DataDir  (mlops.db · storage\ · logs\)  ← 백업 대상"
Write-Host "  학습 로그: $(if ($LogArchiveDir) { "$LogArchiveDir  (작업이 끝날 때마다 한 벌씩, 서버가 지우지 않음)" } else { '보관하지 않음' })"
if ($Local) {
    Write-Host "  로그인   : 이 서버의 계정 — 아이디·비밀번호 (dev 토큰 꺼짐)"
} else {
    Write-Host "  로그인   : 운영 웹(BODA.VMS.Web) 계정의 아이디·비밀번호 (dev 토큰 꺼짐, 토큰 붙여넣기는 뒷길로 남아 있음)"
}
Write-Host "  워커     : BODA-VMS-TrainWorker MSI 를 SERVERURL=http://localhost:$Port 로 설치"
if ($httpsEnabled) {
    Write-Host "             다른 PC 의 워커는 SERVERURL=https://<이 PC 이름 또는 IP>:$HttpsPort (그 PC 도 인증서를 신뢰해야 합니다)"
}

# ── 7) 남은 한 줄 ──
# 모드에 따라 할 일이 다르다. 자체 계정 모드에는 운영 웹이 없으므로 CORS 도 없다 —
# 대신 사람이 처음 들어올 계정을 알려 준다. 그 계정과 임시 비밀번호는 서버가 만들어
# 설치 폴더에 한 번 남기는데(Auth:Local:BootstrapAdmin), 여기서 보여 주지 않으면
# 설치한 사람이 그 파일을 찾아다녀야 한다.
if ($Local) {
    $seed = Join-Path $InstallDir "initial-admin-password.txt"
    Write-Host ""
    if (Test-Path $seed) {
        Write-Host "첫 관리자 계정 — 첫 로그인에서 비밀번호를 바꾸고 이 파일을 지우세요" -ForegroundColor Yellow
        Get-Content $seed -Encoding UTF8 | ForEach-Object { Write-Host "  $_" }
        Write-Host "  파일: $seed"
    } else {
        Write-Host "계정이 이미 있습니다 — 쓰던 계정으로 로그인하세요 (관리 ▸ 사용자 역할에서 추가·초기화)." -ForegroundColor Yellow
    }
    Write-Host "  서명 키는 서비스 환경변수 Auth__Local__Key 에 있습니다. 바꾸면 그 순간 모두 다시 로그인해야 합니다."
    return
}

# 운영 웹 모드: 브라우저가 그쪽으로 직접 로그인하므로 오리진 한 줄이 필요하다.
# 로그인 화면은 아이디·비밀번호를 우리 서버가 아니라 브라우저가 운영 웹으로 곧장 보낸다 (설치 가이드 3.4).
# 오리진이 달라지므로 운영 웹의 Cors:AllowedOrigins 에 이 서버 주소가 없으면 로그인만 막히고,
# 브라우저에는 네트워크 단절과 똑같은 오류만 보인다 — 설치하는 사람이 여기서 보고 가도록 남긴다.
$ips = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike "127.*" -and $_.IPAddress -notlike "169.254.*" } |
    Select-Object -ExpandProperty IPAddress | Sort-Object -Unique)
$origins = @("http://localhost:$Port") + ($ips | ForEach-Object { "http://${_}:$Port" })
if ($httpsEnabled) {
    # 다른 PC 의 브라우저는 https 주소로 들어온다. 이 PC 의 브라우저가 쓰는 http://localhost 도 남긴다.
    $origins = @("https://$($env:COMPUTERNAME.ToLowerInvariant()):$HttpsPort") +
        ($ips | ForEach-Object { "https://${_}:$HttpsPort" }) + @("http://localhost:$Port")
    if ($ProductionWebUrl -notlike "https://*") {
        Write-Host ""
        Write-Warning "운영 웹 주소가 https 가 아닙니다 ($ProductionWebUrl). 다른 PC 의 브라우저는 https 화면에서 http 주소로 로그인 요청을 보내지 못합니다(브라우저가 막습니다). 운영 웹도 https 로 열고 -ProductionWebUrl https://<운영 웹 이름>:<https 포트> 로 다시 설치하세요. 그 주소의 인증서를 이 PC 가 신뢰해야 합니다."
    }
}
Write-Host ""
Write-Host "남은 한 줄 — 운영 웹에 이 서버 주소를 허용하세요 (빠지면 로그인만 막힙니다)" -ForegroundColor Yellow
Write-Host "  운영 웹($ProductionWebUrl)의 appsettings.Production.json:"
Write-Host "    `"Cors`": { `"AllowedOrigins`": [ `"$($origins[0])`" ] }"
Write-Host "  주소는 사용자가 브라우저 주소창에 치는 것과 글자 그대로 같아야 합니다. 이 PC 기준 후보:"
Write-Host ("    " + ($origins -join "  ·  "))
Write-Host "  넣은 뒤 운영 웹 서비스를 다시 시작합니다. 설정 값이라 운영 웹 프로그램은 바뀌지 않습니다."
Write-Host "  자세한 것은 설치 가이드 3.4 (docs/manual/install.json)."
