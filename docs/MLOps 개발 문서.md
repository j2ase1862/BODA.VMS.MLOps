# BODA VMS MLOps 플랫폼 개발 문서 — AI 학습 도구의 웹 전환

문서 버전: v0.3 (2026-10-21)
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
| 분리형 | 관리 서버(기존 Web 서버) + GPU 워커 1~N, 라인 PC 는 런타임만 | 다중 라인 공장 |
| 올인원 | 관리 서버 + GPU 워커가 한 PC (라벨링 PC 겸용) | 소규모 사이트, 첫 도입 |
| 라인 PC 겸용 | 라인 PC 한 대에 런타임 + 관리 서버 + GPU 워커 | 워커 PC 를 따로 둘 수 없는 소규모 사이트 |

~~단독 모드 (서버 없음, 라인 PC 에서 WPF 도구 유지)~~ 는 2026-10-21 에 접었다. 딥러닝 학습은 MLOps 한 경로로 모은다 (§5.5).
서버를 두지 않던 사이트는 **라인 PC 겸용**으로 간다 — PC 를 더 사지 않아도 된다.

**라인 PC 겸용은 검사와 학습이 한 GPU 를 나눠 쓴다.** 학습이 GPU 를 수십 분 채우면 검사 추론이 늦어져 택트를 놓친다.
WPF 도구로 학습해도 같은 문제였다 — 누가 띄웠든 같은 GPU 다. 워커 PC 를 따로 두는 것이 가장 깨끗하지만 소규모 사이트에는
그 비용이 크므로, **AutoRun 중에는 학습을 돌리지 않는 것을 코드로 막는다** (§5.6). VMS 1.43.0 · 워커 0.1.3 부터 들어 있고,
그보다 낮은 판이 섞인 PC 에서는 운영 규칙(검사 중에는 그 워커를 비활성화)으로 막는다.

### 3.2 설계 원칙
1. **런타임은 플랫폼 없이도 동작한다.** 레시피의 모델 참조는 로컬 경로를 항상 허용하고, 플랫폼 바인딩은 선택이다.
2. **스크립트 프로토콜은 계약이다.** 워커·WPF 도구·CLI 가 같은 `train_*.py` 를 실행한다. 프로토콜 변경은 세 곳 동시 갱신.
   (2026-10-21 WPF 도구 정리 뒤: 스크립트 원본은 MLOps `scripts/`, 파서는 `VMS.Core.Contracts` — 바꿀 때 두 리포 PR 이 함께 간다.)
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

**2026-10-21 결정 — WPF 도구를 정리하고 딥러닝은 MLOps 로 일원화한다.**
Phase 4 가 끝났는데도 WPF 도구가 라벨링·학습을 그대로 갖고 설치되어, 같은 일을 하는 화면이 두 벌이 됐다.
사용자 매뉴얼 7.1 의 [레지스트리에 등록] 을 웹에서 찾다 못 찾는 일이 실제로 있었다. 학습 엔진(`train_*.py`)은 하나지만
WPF 에서 올린 버전은 출처가 `Upload` 라 데이터셋 판·seed 가 남지 않아, 두 길을 섞으면 재현되는 모델과 안 되는 모델이 섞인다.
단독 모드도 함께 접는다 (§3.1).

정리 전에 MLOps 가 먼저 채울 것 — WPF 에는 있고 웹에는 없거나 모자란 것 (2026-10-21 대조):

| 순서 | 항목 | 지금 웹 | 범위 |
|---|---|---|---|
| 1 | PP-OCR 내보내기 | 이미지당 **첫 Text 박스만** rec 로 낸다(`DatasetExport.WritePpOcr`, `DatasetSnapshotService` 크롭). 크롭할 박스도 Text 가 아니라 첫 박스다 | 박스마다 rec 크롭. 형식은 `train_ppocr.py` 가 읽는 그대로. **det 은 만들지 않는다** — `train_ppocr.py --target detection` 이 det 설정에 rec 라벨 파일을 넘겨 검출 학습이 돌지 않으므로, 허용 목록의 `detection` 선택지를 닫는다 |
| 1b | PP-OCR rec 데이터셋 가져오기 | 밖에서 만든 zip 을 `ppocr` 판으로 올리는 길은 있다(`DatasetVersionService.UploadAsync`, 내용 무검사) | VisionSetup SynthData 출력(AiTools 와 함께 학습 카드가 사라진다)을 zip 으로 올려 워커가 그대로 학습하는지 검증하고, 구조가 틀린 zip 은 올릴 때 이유와 함께 거절 |
| 2 | 데이터셋 클래스 추가 | 생성 후 불변(`UpdateDatasetRequest` 는 이름·설명·보관만) | **끝에 덧붙이기만**. 이름·순서 변경·삭제는 막는다 — 뜬 판과 학습한 모델이 그 순서를 쥐고 있다 |
| 3 | 모델 돌려 보기 | 없음. 승격하는 사람이 지표 숫자만 본다 | `PrelabelService` 의 검출 추론을 재사용해 사진별 겹쳐 보기 · conf/IoU 조절 (WPF 미리보기도 검출만) |
| 4 | 폴더 단위 Active Learning | 불확실도로 라벨링 순서만 정하고 보여 주지 않는다 | 이미지 풀 → 후보 모델로 훑기 → 불확실도 순 목록 → 골라 데이터셋에 담기 |
| 5 | 증강 프리셋 | 값을 하나씩 넣는다 | 제출 화면의 값 묶음 버튼. 화이트리스트 검사는 그대로 |

**진행 (2026-10-21):** 1·1b(#45) · 2 클래스 덧붙이기(#48, 검출은 클래스가 다른 학습을 제출에서 막는다) ·
3 모델 돌려 보기(#49) 머지. 3 을 하며 D-FINE 추론이 `orig_target_sizes` 를 넣지 않아 사전 라벨링이 D-FINE 에서 모든 사진을
건너뛰던 결함을 찾아, 런타임 규약(늘려 맞춤 + (h, w))으로 고쳤다. 4·5 는 WPF 제거의 선행 조건이 아니다.
이어서 VMS PR #570 으로 WPF 도구를 정리했다 — **VMS v1.44.0** 부터 AI 학습 도구가 설치되지 않고, 마스터 MSI 의
`AiTools` 가 빠지며, 학습 스크립트 원본은 MLOps `scripts/` 로 넘어왔다(`tools/check_train_curves.py` 도 함께).
4 는 "새 사진 훑기" 로 넣었다 — 데이터셋 상세 → 수집 사진 중 그 데이터셋에 없는 것을 후보 모델로 60장씩 훑어
**후보 없음 → 최고 점수가 낮은 순**(WPF 의 "신뢰도 낮은 순")으로 보여 주고, 사람이 골라 담는다. 고른 사진만 미리 채운다
(`PrelabelRequest.ImageIds`). 훑기는 아무것도 담지 않는다.
5 증강 프리셋은 WPF 의 넷(기본 · 공장 조명 · 작은 결함 · 최소)을 `Core/Training/AugmentationPresets` 로 옮기되 **스크립트가 실제로 쓰는 값만** 바꾼다 —
`train_dfine.py` 는 mosaic·mixup 을 버리고 `train_rfdetr_seg.py` 는 증강 인자를 하나도 쓰지 않는다. **이로써 1~5 가 모두 들어갔다.** 같은 이유로 스크립트가 받기만 하고 쓰지 않는 칸은 학습 제출 화면에서 숨긴다(`HyperparamWhitelist.Unused` — 검증 목록에서는 빼지 않아 옛 작업 재시도가 막히지 않는다).

그 뒤 VMS 리포에서 (1·1b·2·3 이 머지된 뒤):
- **학습 스크립트 원본을 MLOps 리포로 옮긴다** (지금은 `VMS.DeepLearning/scripts`). 그 뒤로는 stdout 프로토콜(`TrainingOutputParser`·`TrainingArgumentBuilder`)은
  `VMS.Core.Contracts` 에, 스크립트는 MLOps 에 있으므로 **프로토콜을 바꿀 때는 두 리포 PR 이 함께 간다.** 옮기는 시점에 CLAUDE.md 의 "손대면 안 되는 것" 절도 고친다.
- 마스터 MSI 에서 `AiTools` Feature·체크박스·`ExcludeAiTools` 를 지운다. **워커는 별도 MSI 로 배포한다** — LocalSystem 서비스이고 Python·GPU 환경을 요구해
  라인 PC 기본 설치와 성격이 다르다. 라인 PC 겸용은 두 MSI 를 각각 설치한다.
- VisionSetup 의 Deep Learning 메뉴·`VMS.DeepLearning` 프로젝트·이를 참조하는 시험, VisionSetup 의 죽은 `TrainingService`·`AnnotationViewModel`·`AnnotationView`,
  SynthData 창의 학습 카드(→ MLOps 로 올리는 안내로), 도움말·템플릿 문구의 "VMS.DeepLearning 에서 학습" 안내를 지운다.

### 5.6 검사 중 학습 막기 (라인 PC 겸용)
**구현됨 (2026-10-21):** VMS PR #566 · v1.43.0(신호), MLOps PR #46 · 워커 0.1.3(양보). 세션 0 의 SYSTEM 이 사용자 세션의 신호를 보는 것은
시험 프로그램으로 확인했다(#46 댓글). VMS AutoRun 에서 워커가 실제로 끊기는 끝단은 카메라가 있는 현장에서 확인한다.

라인 PC 겸용(§3.1)에서 VMS 가 **AutoRun 중이면 그 PC 의 워커는 학습하지 않는다.** 검사가 학습보다 먼저다.
- **신호는 같은 PC 안에서 주고받는다.** VMS 가 AutoRun 동안 Windows 명명 객체를 잡고, 워커가 작업을 받기 전과 학습 중에 확인한다.
  서버를 거치지 않으므로 서버가 죽어도 동작하고, VMS 가 비정상 종료하면 운영체제가 객체를 풀어 준다(파일 표시는 남아 워커가 영영 멈춘다).
  워커는 LocalSystem(세션 0), VMS 는 사용자 세션이라 `Global\` 이름 공간과 접근 권한을 맞춰야 한다. 이름은 두 리포가 같은 문자열을 써야 하는 계약이라 `VMS.Core.Contracts` 에 둔다.
- **학습 중에 AutoRun 이 시작되면 학습을 중단하고 대기열로 되돌린다.** 일시정지는 GPU 메모리를 쥔 채라 검사 추론이 메모리 부족으로 터질 수 있고,
  AutoRun 을 학습 끝까지 미루는 것은 라인이 학습을 기다리는 꼴이다. 되돌린 것은 실패 시도로 세지 않는다(`MaxAttempts` 를 갉아먹지 않게).
  새 전이이므로 `TrainingJobStateMachine` 과 그 시험부터 고친다. 처음부터 다시 도는 것이 비용이고, 체크포인트 이어 하기는 `train_*.py` 변경이라 나중 일이다.
- **처음에는 학습만 막는다.** 서버도 같은 PC 면 SAM 보조·사전 라벨링·모델 돌려 보기가 CPU 를 쓰지만 학습보다 훨씬 가볍다. 택트에 영향이 보이면 그때 넣는다.
- 순서: Contracts 에 신호 이름 → VMS 의 AutoRun 이 신호를 잡음 → 워커가 받아 중단·대기열 (Phase 3 명세 §7).

VMS 쪽 검토 결과 (2026-10-21, VMS PR 머지 때 확정):
- **신호를 잡는 구간:** AutoProcessService 의 `StartAsync` 부터 `StopCoreAsync` 끝까지(`IsRunning`) + **Roller 연속 검사**.
  수동 단발 검사·VisionSetup 티칭·Test Run·시뮬레이션은 대화형이라 잡지 않는다. 그래서 이름이 AutoRun 보다 넓다.
- **이름(안):** `VMS.Core.Services.InspectionActivitySignal.MutexName = @"Global\BODA.VMS.InspectionActive"`.
  같은 클래스의 `TryAcquire()`(VMS 용, 실패하면 null — 생성 실패가 AutoRun 을 막지 않는다)와 `IsActive()`(워커용)를 양쪽이 같이 쓴다.
  **워커는 `IsActive()` 만 부른다** — `UnauthorizedAccessException`(있는데 못 엶)도 "검사 중" 으로 본다.
- **Mutex 는 `initiallyOwned: false`** — 소유는 스레드에 묶이는데 VMS 의 시작·정지는 다른 스레드라 abandoned 가 된다. 신호는 존재만 쓴다.
- **다중 인스턴스는 문제없다.** VMS 는 한 PC 다중 인스턴스(`--instance`)를 지원하지만, 명명 객체는 마지막 핸들이 닫힐 때 사라지므로
  이름 하나로 충분하다. 이 이름은 인스턴스 접미사(`QualifyIpcName`)를 붙이지 않는다.
- **라인 검사도 GPU 를 쓴다** (OnnxRuntime.Gpu, Auto = CUDA → DML → CPU). 같은 GPU 를 두고 직접 경합한다.
- **VMS 는 워커를 기다리지 않는다.** 그래서 AutoRun 직후 몇 사이클은 워커가 GPU 메모리를 놓기 전이다.
  워커는 신호를 **1~2초 주기**로 보고, 중단은 **파이썬 프로세스 트리를 즉시 종료**한다.
- **패키지:** `VMS.Core.Contracts 1.43.0` (VMS v1.43.0 과 함께). 이쪽 참조를 `1.33.*` → `1.43.*` 로 올린다.

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
| `POST /api/training-jobs/{id}/artifacts` | best.onnx·지표 업로드 → ModelVersion 생성 (실패·취소 때는 train.log 만) | Worker |
| `GET /api/training-jobs/{id}/log` | 로그 한 벌 다운로드 — 머리말 + train.log(없으면 서버 로그 줄). 끝난 작업은 `Mlops:LogArchiveDir` 에도 같은 파일 | Viewer |
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
| v0.2 | 2026-10-21 | WPF 도구 정리·딥러닝 MLOps 일원화 결정(§5.5), 단독 모드를 접고 라인 PC 겸용 배치 추가(§3.1), AutoRun 중 학습 막기(§5.6) |
| v0.3 | 2026-10-21 | 선행 조건 1~3 완료, WPF 도구 정리(VMS #570 · v1.44.0 예정) — 학습 스크립트 원본이 MLOps 로(§3.2-2, §5.5) |
