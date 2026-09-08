# BODA VMS MLOps

검사 모델의 생애주기를 웹에서 다루는 플랫폼입니다. 이 리포는 상위 설계 문서(`docs/MLOps 개발 문서.md`)의
**Phase 1(모델 레지스트리·배포)** 과 **Phase 3(학습 워커)** 을 구현합니다. Phase 2(데이터 관리)는 학습에 필요한
최소한(데이터셋 버전 = 내보내기 zip)만 선행 구현했고, Phase 4(브라우저 라벨링)는 범위 밖입니다.

현장 검사 런타임(VMS·VisionSetup)은 그대로 SDK 역할을 유지합니다. 이 플랫폼은 모델을 만들고 내려보내는 상위 계층입니다.

## 구성

```
BODA.VMS.MLOps.slnx
├── src/BODA.VMS.MLOps.Core        순수 로직 — 도메인 열거형·model:// 참조·작업 상태 머신
│                                  ONNX 규약 판별 확장·하이퍼파라미터 화이트리스트·인자 조립
├── src/BODA.VMS.MLOps.Contracts   서버 ↔ 워커 ↔ 브라우저 공용 DTO
├── src/BODA.VMS.MLOps.Server      관리 서버 (ASP.NET Core 8, SQLite WAL, SignalR)
├── src/BODA.VMS.MLOps.TrainWorker 학습 워커 (Windows 서비스, GPU PC)
├── tests/BODA.VMS.MLOps.Tests     xUnit 90개 — 규약·상태 머신·API·워커 프로토콜·E2E
├── scripts/                       학습 스크립트(VMS 리포와 동일 규약) + 진단·허용 목록·가짜 스크립트
└── docs/                          상위 설계 문서 3종
```

외부 의존은 `VMS.Core.Contracts` NuGet 패키지 하나입니다. VMS 런타임과 공유하는 규약 계층으로,
ONNX 메타데이터 리더, 검출 규약 판별, 학습 stdout 프로토콜 파서가 들어 있습니다.
VMS 리포에서 `tools\pack-contracts.ps1` 로 만들어 `D:\Repo\nuget-local` 에 놓으면 `nuget.config` 가 집어 갑니다.

## 실행

```bash
# 규약 패키지 준비 (VMS 리포에서 1회)
D:\Repo\VMS\tools\pack-contracts.ps1

dotnet build BODA.VMS.MLOps.slnx
dotnet test  BODA.VMS.MLOps.slnx

# 서버 (Swagger: http://localhost:5310/swagger)
dotnet run --project src/BODA.VMS.MLOps.Server

# 워커 (GPU PC)
BODA.VMS.MLOps.TrainWorker.exe configure --server http://<서버>:5310 --token wk_...
BODA.VMS.MLOps.TrainWorker.exe
```

서버는 시작할 때 `Jwt:Key` 가 32자 이상인지 확인하고, 없으면 부팅을 멈춥니다.
개발은 `dotnet user-secrets set "Jwt:Key" "<32자 이상>"`, 운영은 환경변수 `Jwt__Key` 를 씁니다.
BODA.VMS.Web 과 같은 키·발급자를 쓰면 기존 로그인 토큰이 그대로 통합니다.

## 인증과 역할

토큰 두 종류를 `Authorization` 헤더 접두사로 구분합니다. 사용자 JWT 와 `wk_` 로 시작하는 워커 서비스 계정 토큰입니다.
워커 토큰은 서버에 SHA-256 해시로만 저장되고, 발급 응답에서 한 번만 노출됩니다.

| 역할 | 할 수 있는 일 |
|---|---|
| Viewer | 조회 |
| Labeler | 조회 + 라벨 (Phase 4 용, 현재 미사용) |
| Engineer | 모델·버전 등록, Staging 승격, 바인딩, 학습 작업 생성 |
| Admin | Production·Retired 승격, 워커 발급·비활성, 사전학습 미러 관리 |
| Worker | 워커 프로토콜, 데이터셋 export, 사전학습 다운로드, 아티팩트 업로드 |
| Line | 모델 참조 해석, 아티팩트 다운로드, 동기화 페이로드 |

## 모델 레지스트리 (Phase 1)

업로드는 스트리밍 저장과 SHA-256 계산으로 시작해, 같은 계열에 같은 파일이면 기존 버전을 그대로 돌려줍니다.
그다음 `InferenceSession` 을 만들지 않고 protobuf 만 읽어 규약을 판별하고, 클래스 목록과 입력 크기를 확인합니다.
`names` 가 없으면 업로드 폼의 클래스를 요구하고, 모델 계열의 클래스와 순서까지 어긋나면 거부합니다.
YOLO 파생 모델은 라이선스 필드가 없으면 등록되지 않고, Production 승격에는 Admin 의 라이선스 확인이 더 필요합니다.

스테이지는 Candidate에서 Staging, Production, Retired 순으로 이동합니다. Production 은 모델 계열당 하나뿐이라,
새 버전이 올라오면 이전 Production 은 Staging 으로 내려갑니다. 롤백은 바인딩 이력에서 이전 버전을 고르는 것으로 끝납니다.

레시피 도구에는 `model://{modelId}@{version}` 또는 `model://{modelId}@production` 참조를 저장합니다.
라인 PC 는 동기화 페이로드에 실린 sha256 으로 아티팩트를 미리 받아 두고, 서버가 없어도 캐시로 검사를 계속합니다.
아티팩트 응답의 ETag 가 sha256 이라 두 번째 요청부터는 304 로 끝납니다.

### 상위 설계 문서와 다른 점

ONNX 해시의 유일 범위를 전역이 아니라 모델 계열 안으로 좁혔습니다.
전역 유일로 두면 다른 계열에 같은 파일을 올렸을 때 남의 계열 버전이 반환되어 정보가 새고,
그 계열의 작업 유형·클래스 검증도 건너뛰게 됩니다. 아티팩트 파일 자체는 여전히 sha 경로로 한 벌만 저장합니다.

### VMS 리포에 필요한 후속 조치

`VMS.Core.Contracts` 의 `OnnxMetadataReader` 는 protobuf 길이 필드를 남은 바이트와 대조하지 않습니다.
조작된 varint 하나로 스트림이 뒤로 감겨 파싱이 무한히 반복되므로, 신뢰할 수 없는 파일에 쓰면 요청 하나가 스레드를 영구 점유합니다.
이 리포는 자체 `OnnxSafeReader` 로 우회했지만, VisionSetup 이 현장에서 임의 경로의 ONNX 를 열 수 있으니
VMS 리포에도 같은 하한 검사를 넣는 편이 좋습니다.

## 학습 워커 (Phase 3)

워커는 서버로만 아웃바운드 HTTP 를 열기 때문에 방화벽 예외가 필요 없습니다. 서버는 워커 주소를 모릅니다.

시작할 때 venv 를 만들고 허용 목록(`scripts/requirements-allowlist.txt`) 안의 패키지만 설치한 뒤,
`worker_diag.py` 로 GPU·CUDA·패키지 버전을 확인해 서버에 보고합니다. 진단에 실패하면 서버가 워커를 비활성으로 두고
작업을 주지 않습니다. 프로토콜 버전이 서버와 다를 때도 마찬가지입니다.

작업을 받으면 데이터셋 zip 을 내려받아 경로 탈출과 심볼릭 링크를 막으며 풀고, 사전학습 가중치는 서버 미러에서만 받습니다.
스크립트는 서버 매니페스트의 해시와 일치할 때만 실행합니다. 하이퍼파라미터는 서버와 워커가 같은 화이트리스트로
두 번 검증하고, `ProcessStartInfo.ArgumentList` 로만 넘겨 문자열 연결을 하지 않습니다.

실행 중에는 stdout 프로토콜을 읽어 1초 단위로 진행률과 로그를 보고합니다.
`[EPOCH]` 나 `[PROGRESS]` 가 30분간 없으면 프로세스 트리를 종료하고, 전체 24시간 상한도 겁니다.
로그에서 CUDA 메모리 부족이 보이면 batch 를 절반으로 줄여 한 번 다시 돌립니다.
끝나면 `best.onnx` 를 Phase 1 검증 파이프라인에 태워 Candidate 버전으로 등록하고,
데이터셋 해시·스크립트 해시·패키지 목록·seed 를 재현성 레코드로 남깁니다.

상태는 서버가 소유합니다. 워커는 보고만 하고, 전이 규칙은 서버가 검증합니다.

```
Queued → Assigned → Preparing → Running → Exporting → Uploading → Succeeded
```

배정 후 60초 안에 ack 이 없거나 하트비트가 90초간 끊기면 서버가 작업을 다시 큐에 넣습니다.
재시도 한도를 넘기면 실패로 확정합니다. 스크립트 오류는 같은 입력이면 같은 결과이므로 재시도하지 않습니다.

## API

전체 목록은 개발 모드의 Swagger(`/swagger`)에 있습니다. 주요 경로만 적습니다.

| 경로 | 용도 |
|---|---|
| `POST /api/models`, `POST /api/models/{id}/versions` | 모델 계열 생성, ONNX 업로드 |
| `POST /api/model-versions/{id}/promote` | 스테이지 승격·강등 |
| `GET /api/model-versions/{id}/artifact` | 아티팩트 다운로드 (ETag = sha256) |
| `GET /api/models/{id}/resolve?stage=production` | 라인 PC 의 참조 해석 |
| `PUT /api/recipes/{recipeId}/tools/{toolId}/model` | 바인딩 |
| `POST /api/model-bindings/{id}/rollback` | 롤백 |
| `GET /api/recipes/{recipeId}/model-bindings/payload` | 동기화 페이로드 (VMS 프리페치) |
| `POST /api/workers`, `POST /api/workers/register` | 워커 발급(Admin), 워커 등록 |
| `POST /api/training-jobs` | 학습 작업 생성 |
| `GET /api/training-jobs/next?wait=25` | 워커 long-poll |
| `PATCH /api/training-jobs/{id}/progress` | 진행률·로그 보고 |
| `POST /api/training-jobs/{id}/artifacts` | 아티팩트 업로드 → Candidate 생성 |
| `GET /api/pretrained/{ref}/{file}` | 사전학습 미러 |
| SignalR `/hubs/models`, `/hubs/training` | 버전·바인딩 변경, 진행률·로그 중계 |

오류는 항상 `{"code": "...", "message": "...", "details": [...]}` 형태입니다.
`code` 는 `ClassMismatch`, `MissingNames`, `LicenseRequired`, `InvalidStageTransition`, `DuplicateJob` 등입니다.
응답의 열거형은 camelCase 이고, 그 값을 그대로 URL 에 되돌려 넣어도 동작합니다.

## 테스트

```bash
dotnet test BODA.VMS.MLOps.slnx
```

90개가 GPU 없이 몇 초 만에 끝납니다. 규약 판별은 VMS 리포와 같은 스텁 ONNX 를 씁니다.
워커 경로는 `scripts/train_fake.py` 가 stdout 프로토콜만 흉내 내며 정상·오류·무응답·취소·OOM 상황을 만들어 줍니다.
E2E 테스트는 인메모리 서버에 실제 `JobRunner` 를 붙여 작업 제출부터 Candidate 등록까지 한 번에 확인합니다.
파이썬이 없는 PC 에서는 워커 프로세스 테스트가 조용히 통과합니다.

## 남은 일

- 데이터셋 export 를 zip 단일 파일에서 매니페스트 기반 재개 가능 다운로드로 (수십 GB 대비)
- 세그멘테이션 스크립트 `train_rfdetr_seg.py` 도입, OCR venv 분리 검토
- 워커 MSI(WiX) 와 오프라인 wheel 번들
- Blazor 화면 (모델 목록·상세·워커·작업 생성·실시간 로그)
- 생산 이력 modelVersionId 연동과 모델별 NG 율 통계
- EF Core 마이그레이션 도입 (현재는 시작 시 `EnsureCreated`)
