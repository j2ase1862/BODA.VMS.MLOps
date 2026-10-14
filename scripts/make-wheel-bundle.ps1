<#
.SYNOPSIS
  폐쇄망 워커용 오프라인 wheel 번들을 만든다 (Phase 3 §6).

.DESCRIPTION
  requirements-allowlist.txt 의 모든 패키지를 (의존성 포함) wheel 파일로 받아 한 폴더에 모은다.
  sdist 만 있는 패키지는 이 PC 에서 wheel 로 빌드한다 (pip wheel). 그래서 번들은
  **이 PC 와 같은 조건(Windows x64 · Python 3.12 · CUDA 12.4)** 의 워커 PC 에서만 쓴다.

  결과 폴더를 src\BODA.VMS.MLOps.TrainWorker.Setup\WheelBundle\ 에 두고 Setup 프로젝트를 빌드하면
  bin\Release\BODA-VMS-TrainWorker-<버전>-offline\ 배포 폴더(MSI + wheels\ + install-offline.cmd)가 나온다.
  wheel 은 MSI 안에 넣지 않는다(torch CUDA wheel 2.5GB > Windows Installer 파일 한도 2GB). 워커는 설치 때 받은
  WHEELBUNDLE 폴더를 pip --no-index --find-links 로 쓴다.

  검증: 마지막에 임시 venv 를 만들어 번들만으로 설치가 되는지 확인한다 (-SkipVerify 로 생략).

.PARAMETER Output
  wheel 을 모을 폴더. 기본값은 Setup 프로젝트의 WheelBundle\.

.PARAMETER Python
  기반 파이썬 실행 파일. 기본값은 py -3.12 가 가리키는 것.

.EXAMPLE
  .\scripts\make-wheel-bundle.ps1
  dotnet build src\BODA.VMS.MLOps.TrainWorker.Setup -c Release
#>
[CmdletBinding()]
param(
    [string]$Output = "",
    [string]$Python = "",
    [switch]$SkipVerify
)

$ErrorActionPreference = "Stop"
# 기본 출력 폴더는 본문에서 정한다 — Windows PowerShell 5.1 은 param 기본값 안의 $PSScriptRoot 가 비어
# Join-Path 가 "빈 문자열" 오류로 시작도 못 한다 (2026-10-14 실증).
if (-not $Output) { $Output = Join-Path $PSScriptRoot "..\src\BODA.VMS.MLOps.TrainWorker.Setup\WheelBundle" }
$req = Join-Path $PSScriptRoot "requirements-allowlist.txt"
if (-not (Test-Path $req)) { throw "requirements-allowlist.txt 가 없습니다: $req" }

if (-not $Python) {
    $Python = (& py -3.12 -c "import sys;print(sys.executable)" 2>$null)
    if (-not $Python) { throw "Python 3.12 를 찾지 못했습니다. -Python 으로 경로를 주세요." }
}
$ver = & $Python -c "import sys;print('%d.%d' % sys.version_info[:2])"
if ($ver -ne "3.12") { throw "워커 venv 는 Python 3.12 기준입니다 (지정된 파이썬: $ver)" }

$Output = [System.IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force $Output | Out-Null
Write-Host "wheel 번들 → $Output  (python $Python)"

# 1) 다운로드 + 필요하면 빌드. requirements 의 --extra-index-url(cu124) 이 그대로 적용된다.
& $Python -m pip wheel --disable-pip-version-check -r $req -w $Output
if ($LASTEXITCODE -ne 0) { throw "pip wheel 실패 (exit $LASTEXITCODE)" }

# pip 자체·setuptools·wheel 도 넣어 둔다 — venv 의 기본 pip 이 너무 낡으면 --no-index 설치가 막힌다
& $Python -m pip download --disable-pip-version-check -d $Output pip setuptools wheel
if ($LASTEXITCODE -ne 0) { throw "pip download(pip/setuptools/wheel) 실패" }

$count = (Get-ChildItem $Output -Filter *.whl).Count
$sizeGB = [math]::Round(((Get-ChildItem $Output | Measure-Object Length -Sum).Sum / 1GB), 2)
Write-Host "wheel $count 개, $sizeGB GB"

# torch 가 CUDA 빌드인지 확인 — CPU 빌드가 섞이면 워커 진단이 Disabled 로 끝난다
$torch = Get-ChildItem $Output -Filter "torch-*.whl" | Select-Object -First 1
if (-not $torch) { throw "torch wheel 이 없습니다" }
if ($torch.Name -notmatch "\+cu\d+") { throw "torch 가 CUDA 빌드가 아닙니다: $($torch.Name) — requirements 의 cu124 인덱스·버전 범위를 확인하세요" }
Write-Host "torch: $($torch.Name)"

# 2) 번들만으로 설치되는지 임시 venv 로 확인
if (-not $SkipVerify) {
    $tmp = Join-Path $env:TEMP ("wheel-verify-" + [guid]::NewGuid().ToString("N"))
    Write-Host "검증 venv: $tmp"
    & $Python -m venv $tmp
    & (Join-Path $tmp "Scripts\python.exe") -m pip install --disable-pip-version-check --no-index --find-links $Output -r $req
    $ok = ($LASTEXITCODE -eq 0)
    $importsOk = $false
    if ($ok) {
        # 설치 성공만으로는 부족하다 — 학습 스크립트들이 쓰는 import 까지 확인 (anomalib [core] 누락 같은 것)
        & (Join-Path $tmp "Scripts\python.exe") (Join-Path $PSScriptRoot "bundle_import_check.py")
        $importsOk = ($LASTEXITCODE -eq 0)
    }
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    if (-not $ok) { throw "번들만으로 설치가 되지 않습니다 — 위 pip 출력에서 빠진 패키지를 확인하세요" }
    if (-not $importsOk) { throw "번들 venv 에서 학습 스크립트 import 가 실패합니다 — 위 FAIL 항목의 패키지를 requirements-allowlist.txt 에 추가하세요" }
    Write-Host "검증 통과: 번들만으로 전체 설치 + 학습 스크립트 import 확인"
}

Write-Host "다음: dotnet build src\BODA.VMS.MLOps.TrainWorker.Setup -c Release  → bin\Release\BODA-VMS-TrainWorker-<버전>-offline\"
