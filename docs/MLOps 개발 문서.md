# BODA VMS MLOps 플랫폼 개발 문서 — AI 학습 도구의 웹 전환

문서 버전: v0.1 (초안, 2026-09-08)
작성 배경: VMS.DeepLearning(WPF) 을 GS 인증 범위에서 분리하고, 데이터 관리 → 라벨링 → 학습 → 배포 → 모니터링을 웹 플랫폼으로 옮기는 개발 계획
관련 문서: `D:\Repo\VMS\docs\gs\guides\gs_scope_ai_tools.md` (인증 범위), `docs\design\lot-concept-spec.md`, `docs\design\wo-mixed-recipe-spec.md`, BODA.VMS.Web 리포 (`D:\Project\BODA.VMS.Web`)

---

## 1. 목표와 범위

### 1.1 목표
- 검사 모델의 **생애주기 전체**(데이터 수집 → 큐레이션 → 라벨링 → 학습 → 평가 → 배포 → 모니터링 → 재학습)를 브라우저에서 수행한다.
- 현장 검사 런타임(VMS·VisionSetup)은 그대로 **SDK 역할**을 유지한다. 플랫폼은 모델을 만들고 내려보내는 상위 계층이다.
- 여러 라인·여러 공장의 모델과 데이터를 한 곳에서 관리한다 (다중 사이트).
- 학습 스택은 상업적 제약이 없는 라이선스(Apache 2.0 / BSD / MIT)만 사용한다.

### 1.2 비목표 (이번 로드맵에서 하지 않는 것)
- 클라우드 SaaS 운영. 고객 공장 이미지는 온프레미스에 머문다 (Cloudflare Tunnel 을 통한 원격 접근은 기존 Web 방식 유지).
- 추론을 서버로 옮기는 것. 택트·PLC·카메라 소유권 때문에 추론은 라인 PC 에 남는다.
- 범용 AutoML. 검출 / 분류 / 이상탐지 / 세그멘테이션 / OCR 다섯 작업 유형만 지원한다.

### 1.3 SAIGE VISION 과의 위치
SAIGE 는 플랫폼(데이터·라벨링·학습·배포) + SDK 구조다. 우리는 이미 SDK(검사 런타임)와 운영 웹(생산 이력·작업지시·NG 이미지 업로드)을 갖고 있고, **모델 레지스트리·학습 실행기·브라우저 라벨링** 세 가지가 없다. 차별점은 플랫폼이 아니라 VisionSetup 의 룰 기반 도구, PLC/IO, 3D·로봇까지 묶인 런타임이므로, 플랫폼은 런타임의 고착도를 높이는 방향으로 설계한다.

---

## 2. 현재 자산 (재사용 대상)

| 자산 | 위치 | 재사용 방식 |
|---|---|---|
| 검사 런타임 + ONNX 추론 엔진 (YOLO / D-FINE / 분류 / 이상탐지 / YOLO-seg / PP-OCR) | `VMS.VisionSetup/VisionTools/DeepLearning/*` | 그대로. 모델 규약(부록 B)의 소비자 |
| 모델 규약 자동 판별 (`DetectionModelFormatProbe`, `OnnxMetadataReader.ReadGraphIoNames`) | 동상 | 레지스트리가 업로드 시 같은 판별 로직으로 메타데이터 검증 (C# 공용 라이브러리로 승격) |
| 학습 스크립트 5종 + stdout 프로토콜 (`[EPOCH] [LOSS] [ACC] [PROGRESS] [ONNX] [DONE] [ERROR]`) | `VMS.DeepLearning/scripts/train_*.py` | 학습 워커의 실행 단위. 프로토콜은 그대로 두고 워커가 이벤트로 변환 |
| `TrainingService` (프로세스 실행·인자 조립·프로토콜 파싱) | `VMS.Core/Services/TrainingService.cs` | 워커의 C# 호스트로 이식 (Windows 서비스) |
| 라벨링 앱 (데이터셋·라벨·MobileSAM·Active Learning·Inference 미리보기) | `VMS.DeepLearning` (WPF) | 도메인 모델과 내보내기 형식(YOLO txt, ImageFolder, MVTec, PP-OCR)만 가져오고 UI 는 재작성 |
| 운영 웹 (ASP.NET Core 8 + Blazor WASM, JWT, Role, SignalR, 생산 이력, NG 이미지 업로드, 레시피 동기화) | BODA.VMS.Web | 플랫폼의 호스트. 데이터 수집 파이프라인(NG 이미지)과 배포 채널(레시피 동기화)을 확장 |
| VMS ↔ Web 클라이언트 (업로드 큐, 오프라인 복구, 라이선스 클라이언트) | `VMS.Core` | 모델 pull·데이터 push 에 재사용 |
| 라이선스 정리 (D-FINE Apache 2.0, YOLO 옵션화) | PR #429 | 플랫폼 기본 백본 |

---

## 3. 목표 아키텍처

```
┌────────────────────────── 고객 사내망 ──────────────────────────┐
│                                                                  │
│  [라인 PC × N]  VMS / VisionSetup (검사 런타임, ONNX 추론)         │
│      │ ▲                                                         │
│      │ │ ① NG/샘플 이미지 push (기존 업로드 큐)                     │
│      │ │ ② 모델 pull (레시피 ↔ 모델 버전 바인딩)                    │
│      ▼ │                                                         │
│  [관리 서버]  BODA.VMS.Web (ASP.NET Core + Blazor WASM)            │
│      ├─ 생산 이력 · 작업지시 · 레시피 (기존)                        │
│      ├─ 데이터 관리 · 라벨링 · 데이터셋 버전 (신규)                  │
│      ├─ 모델 레지스트리 · 배포 · 모니터링 (신규)                     │
│      ├─ 학습 작업 큐 (신규)  ── SignalR 진행률 ──▶ 브라우저          │
│      └─ 스토리지 (이미지·데이터셋·모델 아티팩트)                     │
│               │ ▲                                                 │
│               │ │ ③ 작업 배정 / 결과 보고 (HTTP + 토큰)              │
│               ▼ │                                                 │
│  [GPU 워커 × M]  VMS.TrainWorker (Windows 서비스, Python venv)      │
│      └─ train_dfine.py / train_classifier.py / … (기존 스크립트)    │
│      └─ SAM 인코더 서비스 (라벨링 보조 추론)                        │
└──────────────────────────────────────────────────────────────────┘
```

### 3.1 배치 형태
| 형태 | 구성 | 대상 |
|---|---|---|
| 올인원 | 관리 서버 + GPU 워커가 한 PC (라벨링 PC 겸용) | 소규모 사이트, 첫 도입 |
| 분리형 | 관리 서버(기존 Web 서버) + GPU 워커 1~N | 다중 라인 공장 |
| 단독 모드 | 서버 없음. 라인 PC 에서 기존 WPF 도구 유지 | 서버를 두지 않는 사이트 (#407 단독 모드와 동일 원칙) |

### 3.2 설계 원칙
1. **런타임은 플랫폼 없이도 동작한다.** 레시피의 모델 참조는 로컬 경로를 항상 허용하고, 플랫폼 바인딩은 선택이다.
2. **스크립트 프로토콜은 계약이다.** 워커·WPF 도구·CLI 가 같은 `train_*.py` 를 실행한다. 프로토콜 변경은 세 곳 동시 갱신.
3. **모델 규약은 런타임이 정의한다.** 레지스트리는 규약 판별·검증만 하고 변환하지 않는다.
4. **모든 아티팩트는 해시로 식별한다.** 데이터셋 버전 = 파일 목록 해시, 모델 = ONNX SHA-256, 학습 작업 = (데이터셋 해시, 설정, 스크립트 버전).
5. **라이선스 게이트.** 사전학습 가중치·패키지 목록은 허용 목록에 있는 것만 워커가 설치·다운로드한다.

---

## 4. 도메인 모델

```
Site 1─* Line 1─* Recipe ─┐
                           ├─ ModelBinding (Recipe.Tool → ModelVersion, 활성/롤백 이력)
Project 1─* Dataset 1─* Image 1─* Annotation
             │ 1─* DatasetVersion (스냅샷: 이미지 목록 + 라벨 해시 + split)
             │ 1─* TrainingJob (DatasetVersion, TaskType, Backbone, Hyperparams, WorkerId, 상태, 로그, 지표)
             └─ Model 1─* ModelVersion (ONNX 아티팩트, 규약, 메타데이터, 지표, 출처 TrainingJob, 라이선스 태그)
Image ← ImageSource (수동 업로드 | 라인 NG 업로드 | Active Learning 수집)
```

| 엔티티 | 핵심 필드 | 비고 |
|---|---|---|
| Dataset | id, projectId, taskType(Detection/Classification/Anomaly/Segmentation/OCR), classes[], createdBy | 작업 유형은 생성 후 불변 |
| Image | id, datasetId, storageKey, width, height, sha256, source, lineId?, inspectionId?, tags[] | 라인 NG 업로드는 inspectionId 로 생산 이력과 연결 |
| Annotation | id, imageId, classId, shape(box/polygon/mask/text), payload(json), reviewedBy, version | 폴리곤은 D-FINE 학습 시 외접 박스로 변환 (스크립트 기존 동작) |
| DatasetVersion | id, datasetId, manifestHash, split{train,val,test}, imageCount, createdAt | 학습은 항상 버전에 대해 실행 |
| TrainingJob | id, datasetVersionId, script, backbone, pretrainedRef, hyperparams(json), state(Queued/Running/Succeeded/Failed/Cancelled), progress, metrics(json), logRef, workerId | 상태 전이는 워커 보고로만 |
| ModelVersion | id, modelId, artifactKey, sha256, format(dfine/yolo/classifier/anomaly/yoloseg/ppocr), inputSize, classes[], metrics, trainingJobId, license, stage(Candidate/Staging/Production/Retired) | 규약은 업로드 시 `DetectionModelFormatProbe` 로 검증 |
| ModelBinding | recipeId, toolId, modelVersionId, boundAt, boundBy, previousVersionId | 레시피 동기화 payload 에 포함 → VMS 가 pull |

---

## 5. 컴포넌트 상세

### 5.1 모델 레지스트리와 배포 (Phase 1)
- **업로드**: WPF 도구 또는 워커가 `POST /api/models/{modelId}/versions` 로 ONNX + 메타(json) 업로드. 서버는 SHA-256, 규약 판별, `names`/`imgsz`/`model_format` 메타데이터 존재, 파일 크기 상한(예: 2GB)을 검증한다.
- **스테이지**: Candidate → Staging(테스트 라인) → Production. 승격은 Engineer 이상 권한, 감사 로그 기록.
- **바인딩**: 레시피 편집(Web 또는 VisionSetup)에서 도구의 모델 참조를 `model://{modelId}@{version}` 형식으로 저장. 레시피 동기화(#287/#288 경로) 시 VMS 가 참조를 해석해 아티팩트를 로컬 캐시(`%LocalAppData%\BODA VISION AI\models\{sha256}.onnx`)로 내려받고 경로를 치환한다.
- **롤백**: 바인딩 이력에서 이전 버전 선택 → 다음 동기화에 반영. 라인 PC 는 캐시에 이전 아티팩트가 있으면 다운로드 없이 전환.
- **오프라인**: 캐시에 있는 모델은 서버 없이 동작. 캐시 없음 + 서버 없음이면 도구 실패(OnnxLoadException 경로)로 격리.
- **모니터링**: 생산 이력의 NG 율·판정 분포를 모델 버전별로 집계 (기존 Production History 에 modelVersion 열 추가). 드리프트 경고는 임계값 기반(주간 NG 율 변화)으로 시작.

### 5.2 데이터 관리 (Phase 2)
- **수집**: 라인 NG 이미지 업로드(기존)를 `ImageSource=LineNG` 로 데이터 풀에 적재. 정상 샘플은 비율 샘플링(예: 1/200) 옵션.
- **큐레이션**: 태그·검색·중복 제거(pHash)·품질 필터(블러·노출). 데이터셋으로 "보내기" 하면 Image 레코드가 복제되지 않고 참조만 생성.
- **버전**: DatasetVersion 생성 시 manifest(json) 를 스토리지에 고정. 학습·평가는 버전 기준.
- **보존**: 원본 이미지 보존 정책(라인별 일수·용량 상한)은 기존 Retention 설정과 통합. 데이터셋에 속한 이미지는 보존 예외.
- **스토리지 사이징**: 2448×2048 BMP 15MB 기준 라인당 하루 NG 200장 = 3GB. 적재 시 무손실 PNG 또는 원본 유지 선택(검사 재현성 때문에 JPEG 변환은 기본 꺼짐).
- **내보내기**: 기존 형식(YOLO txt + data.yaml / ImageFolder / MVTec / PP-OCR) 그대로 — 워커와 WPF 도구가 동일 형식을 읽는다.

### 5.3 학습 서비스 (Phase 3)
- **워커**: `VMS.TrainWorker` — C# Windows 서비스. 부팅 시 서버에 등록(GPU 정보, 파이썬 버전, 설치 패키지 목록). 작업은 long-poll 로 수신, 데이터셋 버전을 로컬 캐시에 동기화한 뒤 `train_*.py` 를 `TrainingService` 와 같은 방식으로 실행. stdout 프로토콜을 파싱해 `PATCH /api/training-jobs/{id}/progress` 로 보고, 서버는 SignalR 로 브라우저에 중계.
- **환경**: 워커 설치 패키지에 파이썬 venv 부트스트랩(오프라인 wheel 번들 옵션)을 포함. 패키지 허용 목록(§8) 밖은 설치 거부.
- **사전학습 가중치**: 서버가 미러(`/api/pretrained/{ref}`)를 제공. 워커는 서버에서만 받는다 (인터넷·SSL 문제 제거, Avast HTTPS 검사 사례 참고).
- **재현성**: 작업 레코드에 데이터셋 해시·설정·스크립트 버전(git sha 또는 파일 해시)·패키지 목록을 저장. 같은 입력이면 같은 결과가 나오도록 seed 고정.
- **결과**: `best.onnx` + 지표(mAP50, val loss, 혼동행렬) + 학습 곡선을 아티팩트로 업로드 → ModelVersion(Candidate) 자동 생성.
- **큐 정책**: 워커당 동시 1작업, GPU 메모리 부족 시 batch 자동 반감 재시도 1회, 우선순위는 프로젝트 단위.

### 5.4 브라우저 라벨링 (Phase 4)
- **요구사항**: 2448×2048 이상 대형 이미지의 부드러운 확대·이동, 박스/폴리곤/분류/OCR 텍스트 라벨, 단축키(←/→, 클래스 숫자키, Enter/Esc), 다중 사용자 잠금(이미지 단위), 검토 상태.
- **기술 선택**: Blazor WASM 은 캔버스 상호작용에 불리하므로 라벨링 캔버스는 **JS 컴포넌트**(TypeScript, Canvas/WebGL)로 만들고 Blazor 는 JS interop 으로 감싼다. 대안으로 CVAT(MIT) 임베드를 검토하되, 라이선스와 인증·권한 통합 비용을 비교한 뒤 결정.
- **이미지 전송**: 서버가 타일 피라미드(256px 타일, 4단계)를 미리 생성해 뷰포트만 전송. 원본은 다운로드 링크.
- **SAM 보조**: MobileSAM 인코더는 워커 또는 서버 GPU 에서 실행해 임베딩을 캐시하고, 디코더는 브라우저 onnxruntime-web(WebGPU/WASM) 로 실행해 클릭 반응을 즉시 낸다.
- **Active Learning**: 후보 모델로 미라벨 풀을 일괄 추론 → 불확실도 순 정렬 → 라벨링 큐에 넣기. 모델 예측을 초기 라벨로 채우는 기존 기능 유지.

### 5.5 WPF 도구의 위치
- Phase 1~3 동안 WPF 도구는 유지 보수 모드. Phase 1 에서 "레지스트리에 업로드", Phase 2 에서 "웹 데이터셋 버전 내려받기" 버튼만 추가.
- Phase 4 완료 후 WPF 라벨링은 폐기하고 인스톨러 `AiTools` Feature 는 워커 설치 패키지로 대체.

---

## 6. API 초안

| 메서드 · 경로 | 용도 | 권한 |
|---|---|---|
| `POST /api/datasets` · `GET /api/datasets/{id}` | 데이터셋 생성·조회 | Engineer |
| `POST /api/datasets/{id}/images` (multipart) | 이미지 추가 | Engineer, Line(자동 업로드) |
| `PUT /api/images/{id}/annotations` | 라벨 저장 (낙관적 잠금 version) | Labeler |
| `POST /api/datasets/{id}/versions` | 스냅샷 생성 (split 비율) | Engineer |
| `GET /api/dataset-versions/{id}/export?format=yolo` | 워커·WPF 용 내보내기(zip) | Worker, Engineer |
| `POST /api/training-jobs` | 작업 생성 (datasetVersionId, script, hyperparams) | Engineer |
| `GET /api/training-jobs/next?worker=…` | 워커 long-poll | Worker |
| `PATCH /api/training-jobs/{id}/progress` | 진행률·지표·로그 청크 | Worker |
| `POST /api/training-jobs/{id}/artifacts` | best.onnx·지표 업로드 → ModelVersion 생성 | Worker |
| `POST /api/models/{id}/versions` | 외부 ONNX 수동 등록 (규약 검증) | Engineer |
| `POST /api/model-versions/{id}/promote` | 스테이지 승격/강등 | Engineer(Staging), Admin(Production) |
| `GET /api/model-versions/{id}/artifact` | 아티팩트 다운로드 (ETag = sha256) | Line, Engineer |
| `PUT /api/recipes/{id}/tools/{toolId}/model` | 바인딩 | Engineer |
| `GET /api/pretrained/{ref}` | 사전학습 가중치 미러 | Worker |
| SignalR `training/{jobId}` | progress, log, done | 브라우저 |
| SignalR `models` | version created / promoted / bound | 브라우저, VMS |

기존 Web 의 Role(사용자 매뉴얼 §3.6)에 **Labeler**, **Engineer**, **Worker(서비스 계정)** 를 추가한다.

---

## 7. 보안·권한·감사
- 인증: 기존 JWT + 리프레시(30일 절대 만료). 워커는 서비스 계정 토큰(장기, 워커 ID 바인딩, 회전 가능).
- 업로드 파일 검증: 이미지는 매직 바이트·크기 상한·재인코딩 없이 저장하되 EXIF 제거. ONNX 는 protobuf 파싱(`OnnxMetadataReader`)으로 구조 검증, 세션 생성은 격리 프로세스에서.
- 스크립트 실행: 워커는 서버가 서명한 스크립트 해시 목록만 실행(임의 코드 실행 방지). 하이퍼파라미터는 화이트리스트 인자만 전달.
- 감사: 데이터셋 버전 생성, 모델 승격/바인딩, 라벨 수정은 기존 AuditLogger 카테고리에 `Model`, `Dataset` 추가.
- 개인정보: 이미지에 작업자 얼굴이 찍힐 수 있으므로 라인 업로드 시 ROI 크롭 옵션(기존 NG 이미지 960px 썸네일 경로 재사용).

---

## 8. 라이선스 정책 (허용 목록)

| 구성 | 허용 | 금지·주의 |
|---|---|---|
| 검출 백본 | D-FINE(Apache 2.0), RT-DETRv2 / D-FINE 계열(Apache 2.0), RF-DETR(Apache 2.0), YOLOX(Apache 2.0), PP-YOLOE(Apache 2.0) | Ultralytics YOLOv5/8/11(AGPL-3.0), YOLOv9(GPL-3.0), YOLOv10(AGPL-3.0) — Enterprise License 보유 사이트 전용 옵션 |
| 세그멘테이션 | RF-DETR-Seg(Apache 2.0), torchvision Mask R-CNN(BSD) | YOLOv8-seg(AGPL) |
| 분류·이상탐지 | torchvision(BSD), anomalib(Apache 2.0) | — |
| OCR | PaddleOCR(Apache 2.0) | — |
| 라벨링 보조 | MobileSAM(Apache 2.0), DINOv2 백본(Apache 2.0) | SAM 2 는 Apache 2.0 이나 가중치 조건 확인 후 |
| 프레임워크 | torch(BSD), transformers(Apache 2.0), onnxruntime(MIT), onnxruntime-web(MIT) | — |
| 라벨링 UI 임베드 후보 | CVAT(MIT) | Label Studio(Apache 2.0 이나 Enterprise 기능 분리 주의) |

새 패키지·가중치 추가는 이 표와 `gs_distribution_policy.md` §2.6 을 함께 갱신한다.

---

## 9. 단계별 로드맵

| 단계 | 산출물 | 의존 | 규모(가늠) |
|---|---|---|---|
| **Phase 0 — 정리 (진행 중)** | D-FINE 전환(#429 완료), AI 학습 도구 인스톨러 분리·GS 범위 정의(진행), 모델 규약 판별 라이브러리 `VMS.Core` 로 승격 | — | 1~2주 |
| **Phase 1 — 모델 레지스트리·배포** | Web: Model/ModelVersion/Binding 엔티티·API·화면(목록·상세·승격·바인딩 이력). WPF 도구: 업로드 버튼. VMS: `model://` 참조 해석 + 캐시 + 동기화. 생산 이력 modelVersion 열 | Phase 0 | 4~6주 |
| **Phase 2 — 데이터 관리** | 이미지 풀(NG 업로드 연결), 태그·검색·중복 제거, 데이터셋·버전·내보내기 API, 보존 정책 통합. WPF 도구: 웹 데이터셋 내려받기 | Phase 1 | 4~6주 |
| **Phase 3 — 학습 서비스** | `VMS.TrainWorker` 서비스 + 설치 패키지(venv 부트스트랩), 작업 큐·진행률 SignalR·아티팩트 업로드, 사전학습 미러, 재현성 레코드 | Phase 2 | 6~8주 |
| **Phase 4 — 브라우저 라벨링** | JS 캔버스 컴포넌트(박스·폴리곤·분류·OCR), 타일 피라미드, SAM 보조(서버 인코더 + 브라우저 디코더), 검토·잠금, Active Learning 큐 | Phase 2 | 10~14주 |
| **Phase 5 — 모니터링·재학습 루프** | 모델별 NG 율·드리프트 경고, 재학습 제안(데이터 임계치), 파이프라인 템플릿(수집→학습→후보 생성 자동) | Phase 1·3 | 4주 |

MVP = Phase 1 + Phase 3 (WPF 라벨링 유지). 이 시점에 "웹에서 학습을 돌리고 라인에 배포·롤백"이 성립한다.

---

## 10. 리스크와 대응

| 리스크 | 영향 | 대응 |
|---|---|---|
| GPU 서버 부재 사이트 | 학습 불가 | 올인원(라벨링 PC 겸 워커) 형태, 단독 모드 유지 |
| 파이썬 환경 편차(드라이버·CUDA·wheel) | 워커 실패 | venv 부트스트랩 + 오프라인 wheel 번들, 워커 등록 시 자기진단(torch cuda, 패키지 버전) |
| 대형 이미지 브라우저 성능 | 라벨링 UX | 타일 피라미드, WebGL 캔버스, 원본은 필요 시만 |
| Blazor 와 JS 컴포넌트 통합 | 개발 비용 | 라벨링 화면만 JS 앱으로 분리(같은 인증 토큰), 나머지는 Blazor 유지 |
| 스토리지 증가 | 서버 디스크 | 보존 정책·용량 상한·경고, 데이터셋 참조 방식(복제 금지) |
| 라이선스 오염(패키지 추가) | 상용 배포 위험 | 허용 목록(§8) + 워커 설치 거부 + PR 체크리스트 |
| GS 인증 범위 충돌 | 인증 재신청 | 플랫폼은 별도 제품으로 취급, 런타임 규약만 인증 제품에 포함 |
| 사전학습 가중치 다운로드 실패(SSL 검사 백신 등) | 학습 시작 불가 | 서버 미러 + 워커는 서버에서만 다운로드 |

---

## 11. 테스트 전략
- **규약 계약 테스트**: 스텁 ONNX(base64 내장, `DFineOnnxEngineTests` 방식)로 규약 판별·파서 회귀. 레지스트리 업로드 검증도 같은 스텁 사용.
- **워커 프로토콜 테스트**: 가짜 `train_*.py`(프로토콜 라인만 출력)로 진행률·오류·취소 경로. 실제 GPU 학습은 환경 변수 게이트(`TrainingServiceDFineE2ETests` 방식).
- **동기화 E2E**: Web 에 모델 등록 → 레시피 바인딩 → VMS 동기화 → 캐시 파일 해시 일치 → 도구 실행.
- **라벨링 UI**: Playwright 로 박스 그리기·저장·잠금 시나리오, 대형 이미지 성능 예산(60fps 팬/줌).
- **부하**: 이미지 업로드 동시 10 라인, 학습 작업 큐 3 워커.

---

## 12. 운영·배포
- 관리 서버: 기존 MSI 동봉 Web 또는 별도 서버 배포(운영 서비스화 절차 재사용). 스토리지 경로·용량 상한은 AppSetup 의 Web 초기 구성 카드에 추가.
- 워커: 별도 설치 패키지(`VMS-TrainWorker-x.y.z.msi`) — 서비스 등록, venv 부트스트랩, 서버 URL·토큰 입력. 인스톨러 `AiTools` Feature 는 Phase 4 이후 이 패키지로 대체.
- 업데이트: 서버·워커·라인 PC 는 독립 버전. 규약 버전(`model_format`, 프로토콜 태그)만 호환 표를 유지.
- 백업: DB + 스토리지(이미지·아티팩트) 스냅샷. 모델 아티팩트는 해시 이름이라 중복 저장 없이 미러 가능.

---

## 13. 결정이 필요한 사항
1. 라벨링 캔버스를 자체 개발할지, CVAT 를 임베드할지 (Phase 4 착수 전, PoC 2주).
2. 워커의 파이썬 격리 방식 — venv 부트스트랩 vs Windows 컨테이너(GPU 패스스루 제약 검토).
3. 스토리지 백엔드 — 로컬 디스크(단일 서버) vs MinIO(S3 호환, 다중 서버). MVP 는 로컬 디스크 + 추상화 인터페이스.
4. 사전학습 가중치 미러의 갱신 주기와 오프라인 배포 매체(USB) 절차.
5. 플랫폼 제품명과 라이선스(좌석) 모델 — 기존 SW 라이선스 체계(LicGen) 확장 여부.
6. Phase 1 을 v1.31.x 릴리즈에 실을지, 별도 브랜치에서 묶어 갈지.

---

## 부록 A. 학습 스크립트 stdout 프로토콜 (현행)
```
[EPOCH] 5/100      현재/전체 에폭
[LOSS] 0.0234      학습 손실
[ACC] 0.87         검증 지표 (mAP50 / 정확도)
[PROGRESS] 45.5    0~100
[ONNX] <path>      산출 모델 경로
[DONE]             정상 종료
[ERROR] message    실패 (exit code ≠ 0)
[INFO]/[WARN]      로그 (파싱 대상 아님)
```

## 부록 B. 모델 규약 (런타임이 판별)
| 형식 | 입력 | 출력 | 메타데이터 |
|---|---|---|---|
| dfine (deploy) | `images[N,3,S,S]` RGB 0~1 stretch, `orig_target_sizes[N,2]` (h,w) | `labels[N,300]`, `boxes[N,300,4]` xyxy 픽셀, `scores[N,300]` | `names`, `imgsz`, `model_format=dfine` |
| dfine (raw HF) | `pixel_values` | `logits[N,Q,nc]`, `pred_boxes[N,Q,4]` cxcywh 정규화 | `names` |
| yolo | `images[N,3,S,S]` letterbox | `[N,4+nc,A]` 또는 `[N,A,4+nc]` | `names`, `imgsz` |
| yoloseg | 동상 | `output0[N,4+nc+32,A]`, `output1[N,32,mh,mw]` | `names` |
| classifier | `input[N,3,H,W]` (ImageNet 정규화 옵션) | `output[N,nc]` | `names` |
| anomaly / ppocr | 각 도구 도움말 참조 | — | — |

## 부록 C. 관련 코드·문서
- `VMS.VisionSetup/VisionTools/DeepLearning/DFineOnnxEngine.cs`, `OnnxMetadataReader.cs`, `OnnxEngineCache.cs`
- `VMS.Core/Services/TrainingService.cs`, `VMS.DeepLearning/scripts/train_dfine.py`
- `docs/gs/guides/gs_scope_ai_tools.md`, `docs/gs/guides/gs_distribution_policy.md` §2.6
- `docs/manuals/BODA-VMS-AI-Tools-Manual.html` (별책)
- BODA.VMS.Web: 레시피 동기화·NG 이미지 업로드·Role 매트릭스 (사용자 매뉴얼 §3.6)

## 변경 이력
| 버전 | 날짜 | 내용 |
|---|---|---|
| v0.1 | 2026-09-08 | 초안 — 목표·아키텍처·도메인·컴포넌트·API·보안·라이선스·로드맵·리스크·테스트·운영·미결 |
