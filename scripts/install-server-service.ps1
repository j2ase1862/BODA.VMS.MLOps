<#
.SYNOPSIS
  BODA VMS MLOps 서버를 Windows 서비스로 설치(또는 갱신·제거)한다. 관리자 PowerShell 에서 실행.

.DESCRIPTION
  1) 서버를 self-contained win-x64 로 publish (-SkipPublish 로 생략, -PublishDir 로 기존 산출물 지정)
  2) 설치 폴더로 복사 (appsettings.Production.json 과 models\ 는 보존)
  3) appsettings.Production.template.json 의 {{Port}} {{DataDir}} {{ProductionWebUrl}} {{AuthMode}} 를 채워 appsettings.Production.json 생성
  4) 서비스 등록 (자동·지연 시작, 실패 시 재시작) + 서비스 환경변수 Jwt__Key / DOTNET_ENVIRONMENT=Production
  5) 방화벽 인바운드 규칙 (라인 PC·브라우저·워커가 붙는 포트)
  6) SAM 모델(있으면) 복사, 서비스 시작, 응답 확인
  7) 남은 한 줄 안내 — 운영 웹 모드면 그쪽 CORS, 자체 계정 모드면 첫 관리자와 임시 비밀번호

  Jwt:Key 는 운영 웹(BODA.VMS.Web)과 같은 값이어야 로그인 토큰이 통한다. -JwtKey 를 주지 않으면
  환경변수 MLOPS_JWT_KEY → 서버 프로젝트 user-secrets(Jwt:Key) 순서로 찾는다. 파일에는 남기지 않는다.

  -Local 은 운영 웹이 없는 설치다. 사람이 이 서버의 계정으로 로그인하고 서명 키는 따로 만든다
  (서비스 환경변수 Auth__Local__Key). 첫 관리자(admin)와 임시 비밀번호는 서버가 만들어
  설치 폴더의 initial-admin-password.txt 에 한 번 남긴다 — 첫 로그인에서 바꾸고 그 파일을 지운다.

.EXAMPLE
  .\scripts\install-server-service.ps1 -DataDir D:\BODA-MLOps -ProductionWebUrl http://localhost:5292
  .\scripts\install-server-service.ps1 -SkipPublish -PublishDir .\src\BODA.VMS.MLOps.Server\obj\publish-server
  .\scripts\install-server-service.ps1 -Local -DataDir D:\BODA-MLOps
  .\scripts\install-server-service.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "C:\Program Files\BODA Vision AI\BODA VMS MLOps Server",
    [string]$DataDir = "C:\ProgramData\BODA VMS MLOps",
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
    Write-Host "제거 완료. 설치 폴더($InstallDir)와 데이터($DataDir)는 남겨 두었습니다."
    return
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
$cfgPath = Join-Path $InstallDir "appsettings.Production.json"
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
Set-ItemProperty -Path $envKey -Name Environment -Type MultiString -Value $serviceEnv

# ── 5) 방화벽 ──
if (-not $SkipFirewall) {
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile Any | Out-Null
    Write-Host "방화벽 인바운드 허용: $ruleName"
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
Write-Host "  화면/API : http://localhost:$Port  (다른 PC 에서는 http://<이 PC IP>:$Port)"
Write-Host "  서비스   : $ServiceName (자동·지연 시작, LocalSystem)"
Write-Host "  설치 폴더: $InstallDir"
Write-Host "  데이터   : $DataDir  (mlops.db · storage\ · logs\)  ← 백업 대상"
if ($Local) {
    Write-Host "  로그인   : 이 서버의 계정 — 아이디·비밀번호 (dev 토큰 꺼짐)"
} else {
    Write-Host "  로그인   : 운영 웹(BODA.VMS.Web) 계정의 아이디·비밀번호 (dev 토큰 꺼짐, 토큰 붙여넣기는 뒷길로 남아 있음)"
}
Write-Host "  워커     : BODA-VMS-TrainWorker MSI 를 SERVERURL=http://localhost:$Port 로 설치"

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
Write-Host ""
Write-Host "남은 한 줄 — 운영 웹에 이 서버 주소를 허용하세요 (빠지면 로그인만 막힙니다)" -ForegroundColor Yellow
Write-Host "  운영 웹($ProductionWebUrl)의 appsettings.Production.json:"
Write-Host "    `"Cors`": { `"AllowedOrigins`": [ `"$($origins[0])`" ] }"
Write-Host "  주소는 사용자가 브라우저 주소창에 치는 것과 글자 그대로 같아야 합니다. 이 PC 기준 후보:"
Write-Host ("    " + ($origins -join "  ·  "))
Write-Host "  넣은 뒤 운영 웹 서비스를 다시 시작합니다. 설정 값이라 운영 웹 프로그램은 바뀌지 않습니다."
Write-Host "  자세한 것은 설치 가이드 3.4 (docs/manual/install.json)."
