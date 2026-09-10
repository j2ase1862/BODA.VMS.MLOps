<#
.SYNOPSIS
  BODA VMS MLOps 서버를 Windows 서비스로 설치(또는 갱신·제거)한다. 관리자 PowerShell 에서 실행.

.DESCRIPTION
  1) 서버를 self-contained win-x64 로 publish (-SkipPublish 로 생략, -PublishDir 로 기존 산출물 지정)
  2) 설치 폴더로 복사 (appsettings.Production.json 과 models\ 는 보존)
  3) appsettings.Production.template.json 의 {{Port}} {{DataDir}} {{ProductionWebUrl}} 을 채워 appsettings.Production.json 생성
  4) 서비스 등록 (자동·지연 시작, 실패 시 재시작) + 서비스 환경변수 Jwt__Key / DOTNET_ENVIRONMENT=Production
  5) 방화벽 인바운드 규칙 (라인 PC·브라우저·워커가 붙는 포트)
  6) SAM 모델(있으면) 복사, 서비스 시작, 응답 확인

  Jwt:Key 는 운영 웹(BODA.VMS.Web)과 같은 값이어야 로그인 토큰이 통한다. -JwtKey 를 주지 않으면
  환경변수 MLOPS_JWT_KEY → 서버 프로젝트 user-secrets(Jwt:Key) 순서로 찾는다. 파일에는 남기지 않는다.

.EXAMPLE
  .\scripts\install-server-service.ps1 -DataDir D:\BODA-MLOps -ProductionWebUrl http://localhost:5292
  .\scripts\install-server-service.ps1 -SkipPublish -PublishDir .\src\BODA.VMS.MLOps.Server\obj\publish-server
  .\scripts\install-server-service.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "C:\Program Files\BODA Vision AI\BODA VMS MLOps Server",
    [string]$DataDir = "C:\ProgramData\BODA VMS MLOps",
    [int]$Port = 5310,
    [string]$ProductionWebUrl = "http://localhost:5292",
    [string]$JwtKey = "",
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

# ── Jwt 키 ──
if (-not $JwtKey) { $JwtKey = $env:MLOPS_JWT_KEY }
if (-not $JwtKey) {
    $line = & dotnet user-secrets list --project $serverProj 2>$null | Where-Object { $_ -like "Jwt:Key = *" } | Select-Object -First 1
    if ($line) { $JwtKey = $line.Substring("Jwt:Key = ".Length).Trim() }
}
if (-not $JwtKey -or $JwtKey.Length -lt 32) { throw "Jwt:Key(32자 이상)가 필요합니다. -JwtKey, 환경변수 MLOPS_JWT_KEY, 또는 서버 프로젝트 user-secrets 로 주세요. 운영 웹과 같은 키여야 합니다." }

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
# 서비스 전용 환경변수: Jwt 키는 파일에 남기지 않는다
$envKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
Set-ItemProperty -Path $envKey -Name Environment -Type MultiString -Value @(
    "DOTNET_ENVIRONMENT=Production",
    "ASPNETCORE_ENVIRONMENT=Production",
    "Jwt__Key=$JwtKey"
)

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
Write-Host "  로그인   : 운영 웹(BODA.VMS.Web)에서 받은 토큰 붙여넣기 (dev 토큰 꺼짐)"
Write-Host "  워커     : BODA-VMS-TrainWorker MSI 를 SERVERURL=http://localhost:$Port 로 설치"
