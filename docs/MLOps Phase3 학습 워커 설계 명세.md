# MLOps Phase 3 — 학습 워커(VMS.TrainWorker) 설계 명세

문서 버전: v0.1 (2026-09-08) · 상위 문서: `D:\MLOps 개발 문서.md` §5.3 · 선행: Phase 1 모델 레지스트리(`D:\MLOps Phase1 모델 레지스트리 설계 명세.md`), Phase 2 데이터 관리(데이터셋 버전·내보내기 API)
개발 위치: 워커 = 새 프로젝트 `VMS.TrainWorker`(VMS 리포 또는 별도 리포, C# .NET 8 Worker Service) · 서버 측 = BODA.VMS.Web

---

## 1. 목표와 완료 기준

**목표**: 웹에서 학습 작업을 제출하면 GPU 가 있는 PC 의 워커 서비스가 데이터셋 버전을 받아 기존 `train_*.py` 로 학습하고, 진행률·로그를 브라우저에 실시간으로 보이며, 결과 `best.onnx` 와 지표를 모델 레지스트리에 Candidate 버전으로 자동 등록한다.

**완료 기준**
1. 워커 설치 → 서버 등록 → 자기진단(GPU·파이썬·패키지) 결과가 Web 워커 목록에 보인다.
2. Web 에서 Detection 작업 제출(데이터셋 버전·백본·하이퍼파라미터) → 워커가 받아 학습 → 진행률·손실·mAP·로그 스트리밍 → 완료 시 ModelVersion(Candidate) 생성 + 학습 곡선 첨부.
3. 취소·실패·워커 다운(하트비트 끊김) 이 상태 머신대로 처리되고 재시도 정책이 동작한다.
4. 사전학습 가중치는 서버 미러에서만 받는다(인터넷·SSL 검사 문제 제거). 폐쇄망에서 전 과정이 돈다.
5. 같은 데이터셋 버전·설정·스크립트 해시·seed 로 재실행하면 같은 지표가 나온다(재현성 레코드).
6. 다섯 작업 유형(Detection/Classification/Anomaly/Segmentation/OCR) 중 최소 Detection·Classification·Anomaly 가 동작한다. Segmentation 은 RF-DETR-Seg 스크립트 도입 후, OCR 은 paddle 환경 부트스트랩 완료 후.

**비목표**: 분산(다중 GPU) 학습, 하이퍼파라미터 자동 탐색, 브라우저 라벨링.

---

## 2. 구성 요소

```
[BODA.VMS.Web]                          [VMS.TrainWorker (Windows 서비스, GPU PC)]
  TrainingJob 큐 · 워커 레지스트리          ├─ Agent        : 등록·하트비트·long-poll·보고 (HttpClient, 서비스 계정 토큰)
  데이터셋 버전 export · 사전학습 미러       ├─ Environment  : venv 부트스트랩·자기진단·패키지 허용 목록
  아티팩트 저장 → ModelVersion(Candidate)    ├─ JobRunner    : 데이터셋 동기화 → 스크립트 실행(TrainingService 이식) → 프로토콜 파싱 → 아티팩트 업로드
  SignalR training/{jobId}                 ├─ Cache        : datasets\{versionHash}\ · pretrained\{ref}\ · jobs\{jobId}\
                                           └─ scripts\     : train_dfine.py 등 (서버가 서명한 해시 목록과 대조)
```

워커는 **서버로만 아웃바운드 HTTP** 를 연다(인바운드 포트 없음 → 방화벽 예외 불필요). 서버는 워커 주소를 모른다.

---

## 3. 데이터 모델 (서버, EF Core)

```
Worker        : Id · Name · MachineName · Token(해시) · Status(Online|Busy|Offline|Disabled) · LastHeartbeatAt
                · Capabilities(json: gpuName, gpuMemMB, cudaVersion, pythonVersion, packages{name:version}, scriptsHash, diskFreeGB, workerVersion)
                · TaskTypes(json string[] 지원 작업 유형) · MaxConcurrent(=1) · CreatedAt
TrainingJob   : Id · ProjectId · DatasetVersionId · TaskType · Script(train_dfine|train_classifier|train_anomaly|train_rfdetr_seg|train_ppocr)
                · Backbone(string) · PretrainedRef(string) · Hyperparams(json 화이트리스트) · Seed(int)
                · State(Queued|Assigned|Preparing|Running|Exporting|Uploading|Succeeded|Failed|Cancelled)
                · Priority(int) · WorkerId? · AssignedAt? · StartedAt? · FinishedAt? · Attempt(int) · MaxAttempts(=2)
                · Progress(0~100) · CurrentEpoch · TotalEpochs · LastLoss · LastMetric · Error(string?)
                · ResultModelVersionId? · Reproducibility(json: datasetManifestHash, scriptSha256, packages, seed, workerCaps)
                · CreatedBy · CreatedAt · CancelRequested(bool)
JobLogChunk   : JobId · Seq · Level(Info|Warn|Error|Stdout|Stderr) · Text · At        (최근 N줄은 메모리 캐시, 전체는 파일 스토리지)
JobArtifact   : JobId · Kind(Onnx|Metrics|Curve|TrainInfo|Log) · StorageKey · Sha256 · SizeBytes
PretrainedAsset: Ref(예: dfine-small-obj2coco) · Files(json: 이름·sha256·크기) · License · Source(URL) · AddedBy · AddedAt
```

상태 전이(서버가 소유, 워커 보고로만 진행):
```
Queued ─assign→ Assigned ─worker ack→ Preparing ─script start→ Running ─[ONNX]→ Exporting ─upload→ Uploading → Succeeded
   │                │                    │                     │
   └─cancel→ Cancelled  (Assigned/Preparing/Running 에서 CancelRequested → 워커 kill → Cancelled)
   하트비트 3회(90s) 끊김 & Running → Failed(WorkerLost) → Attempt<Max 면 Queued 로 재큐(다른 워커 가능)
   스크립트 exit≠0 또는 [ERROR] → Failed(ScriptError) — 재큐하지 않음(같은 입력이면 같은 실패)
```

---

## 4. 워커 ↔ 서버 프로토콜 (HTTP, JSON, Bearer 서비스 토큰)

| 메서드 · 경로 | 호출 주체·주기 | 내용 |
|---|---|---|
| `POST /api/workers/register` | 워커 시작 시 | `{name, machineName, capabilities, taskTypes, workerVersion}` → `{workerId, pollIntervalSec, heartbeatSec, scriptsManifest}` |
| `POST /api/workers/{id}/heartbeat` | 30s | `{status, currentJobId?, diskFreeGB, gpuMemFreeMB}` → `{cancelRequested?: jobId, disable?: bool}` |
| `GET /api/training-jobs/next?workerId=` | long-poll 25s (Idle 일 때) | 없으면 204. 있으면 Job 전체 + `datasetExportUrl` + `pretrainedFiles[]` + `scriptSha256` |
| `POST /api/training-jobs/{id}/ack` | 배정 직후 | Assigned → Preparing. 60s 내 ack 없으면 서버가 재큐 |
| `PATCH /api/training-jobs/{id}/progress` | 이벤트마다(최대 1/s 코얼레싱) | `{state?, progress, epoch, totalEpochs, loss?, metric?, logChunks[]}` |
| `POST /api/training-jobs/{id}/artifacts` (multipart) | 완료 시 | best.onnx · metrics.json · curves.png · vms_train_info.json · train.log → 서버가 ModelVersion(Candidate) 생성 후 `{modelVersionId}` |
| `POST /api/training-jobs/{id}/finish` | 마지막 | `{state: Succeeded|Failed|Cancelled, error?, reproducibility}` |
| `GET /api/dataset-versions/{id}/export?format=…` | Preparing | zip 스트림, `ETag: manifestHash` (Phase 2 API) |
| `GET /api/pretrained/{ref}/{file}` | Preparing | 사전학습 파일, `ETag: sha256` |
| `GET /api/workers/scripts/{name}` | scriptsManifest 와 로컬 해시 불일치 시 | 스크립트 본문 (서버가 배포하는 버전) |

인증: 워커 토큰은 설치 시 Web 관리자 화면에서 발급(1회 표시), 워커 로컬에 DPAPI 로 암호화 저장. 회전은 Web 에서 재발급 → 워커 설정 갱신.

---

## 5. JobRunner 실행 절차

1. **Preparing**
   - 데이터셋: `datasets\{manifestHash}\` 캐시가 있고 해시가 맞으면 재사용, 없으면 export zip 다운로드 → 압축 해제 → `data.yaml`(YOLO)·ImageFolder·MVTec·PP-OCR 형식 확인.
   - 사전학습: `pretrained\{ref}\` 캐시 확인(파일별 sha256). 없으면 미러에서 다운로드. `--pretrained` 에 로컬 폴더 경로를 넘긴다(HF 다운로드 경로는 워커에서 항상 비활성: `HF_HUB_OFFLINE=1`).
   - 스크립트: `scripts\{name}.py` 해시가 매니페스트와 다르면 서버 버전으로 교체(임의 스크립트 실행 방지).
   - 출력 폴더 `jobs\{jobId}\output\` 생성, 디스크 여유 확인(데이터셋 크기 ×3 이상).
2. **Running** — `TrainingService` 이식본으로 프로세스 실행:
   - 실행 파일: 워커 venv 의 `python.exe` (자기진단으로 torch cuda 확인된 것).
   - 인자: `--dataset --output --epochs --lr --batch_size --imgsz --export_onnx --pretrained <로컬> --seed` + 작업 유형별 허용 인자(hsv_*, mosaic/mixup, method/backbone/coreset_ratio, target). **화이트리스트 밖 키는 거부**(400 at 작업 생성 시점).
   - 환경 변수: `PYTHONIOENCODING=utf-8`, `HF_HUB_OFFLINE=1`, `CUDA_VISIBLE_DEVICES`(워커 설정), `OMP_NUM_THREADS`.
   - stdout 프로토콜 매핑: `[EPOCH] a/b` → epoch·totalEpochs, `[LOSS]` → loss, `[ACC]` → metric, `[PROGRESS]` → progress, `[ONNX]` → Exporting + 경로 기록, `[DONE]` → 완료, `[ERROR]` → 실패 사유. `[INFO]/[WARN]` 과 stderr 는 로그 청크.
   - 워치독: `[EPOCH]` 또는 `[PROGRESS]` 가 `maxSilenceMin`(기본 30분) 동안 없으면 프로세스 kill → Failed(Stalled). 전체 상한 `maxDurationH`(기본 24h).
   - 취소: 하트비트 응답의 `cancelRequested` → 프로세스 트리 kill(`taskkill /T`) → Cancelled 보고. 출력 폴더는 보존(디버그), 24h 후 정리.
   - GPU OOM(로그에 `CUDA out of memory`) 감지 시 1회 자동 재시도: batch_size 절반으로 같은 작업 재실행(Attempt 증가, 사유 기록).
3. **Exporting/Uploading** — `best.onnx` 존재·크기·`onnxruntime` 검증 로그("ONNX 검증 OK") 확인 → 아티팩트 업로드(재시도 3회, 지수 백오프) → 서버가 Phase 1 검증 파이프라인(규약·메타데이터·라이선스 게이트)으로 ModelVersion(Candidate) 생성, `Source=TrainingJob`.
4. **finish** — 재현성 레코드(데이터셋 해시·스크립트 sha·패키지 목록·seed·워커 capabilities)와 함께 종료 보고. 캐시 정리는 LRU(데이터셋 캐시 상한 기본 100GB).

---

## 6. 환경 부트스트랩·자기진단

- 워커 설치 패키지(`VMS-TrainWorker-x.y.z.msi`): 서비스 등록(자동 시작, 지연), `scripts\` 동봉, venv 부트스트랩 스크립트, 선택적 **오프라인 wheel 번들**(torch cu124·torchvision·transformers·onnx·onnxruntime·pyyaml·torchmetrics·pycocotools·anomalib·truststore) — 폐쇄망용.
- 첫 시작 시: `py -3.12` 탐색 → `venv\` 생성 → 허용 목록(§8 상위 문서) 패키지 설치(온라인이면 pip, 오프라인이면 번들) → 자기진단:
  `torch.cuda.is_available()`, GPU 이름·메모리, `transformers.__version__ >= 4.52`, `onnxruntime` 프로바이더, 디스크 여유, 스크립트 해시. 결과를 `register` 로 보고. 실패 항목이 있으면 워커 상태 `Disabled(진단 실패)` 로 표시하고 작업을 받지 않는다.
- 허용 목록 밖 패키지 설치 요청(작업 하이퍼파라미터·스크립트 갱신 포함)은 거부하고 감사 로그.
- SSL 검사 백신(Avast 등) 환경: 워커는 서버로만 통신하므로 무관. venv 부트스트랩의 pip 만 영향 → 번들 사용 또는 `truststore` 선설치.

---

## 7. 큐·스케줄 정책
- 배정: `Priority DESC, CreatedAt ASC`. 워커 `TaskTypes` 와 작업 유형 일치 + `Status=Online` + 데이터셋 크기 ≤ 워커 디스크 여유.
- 워커당 동시 1작업(GPU 독점). `MaxConcurrent` 는 향후 다중 GPU 용.
- 같은 데이터셋 버전·설정의 성공 작업이 있으면 제출 시 "동일 작업 존재" 경고(중복 실행 방지, 강제 가능).
- 우선순위 상승은 Engineer, 다른 사람 작업 취소는 Admin.

---

## 8. 보안
- 워커 토큰 = 서비스 계정, 범위는 워커 API·데이터셋 export·사전학습 미러·아티팩트 업로드로 한정(레시피·생산 이력 접근 불가).
- 스크립트는 서버 매니페스트 해시와 일치하는 것만 실행. 하이퍼파라미터는 타입·범위 검증된 화이트리스트 인자로만 변환(문자열 연결 금지, `ProcessStartInfo.ArgumentList` 사용).
- 데이터셋 zip 해제 시 경로 탈출(`..`)·심볼릭 링크 거부, 총 크기 상한.
- 워커 로컬 캐시는 서비스 계정만 접근(ACL). 로그에 토큰·경로 외 개인정보 미기록.
- 감사: `Training.JobCreated/Cancelled/Failed/Succeeded`, `Worker.Registered/Disabled`.

---

## 9. Web 화면
| 화면 | 내용 |
|---|---|
| Workers | 워커 카드: 상태·GPU·메모리·파이썬/패키지 버전·마지막 하트비트·현재 작업. [토큰 재발급]·[비활성화]. 진단 실패 항목 강조 |
| 학습 작업 생성 | 데이터셋 버전 선택 → 작업 유형 자동 → 백본(허용 목록 콤보: D-FINE nano/small/medium…) → 사전학습(미러 목록) → 하이퍼파라미터(에폭·배치·학습률 기본값 = 스크립트 권장값, D-FINE lr 0.00025) → 증강(hsv, mosaic/mixup 은 YOLO 전용 표시) → 우선순위 |
| 작업 목록/상세 | 상태 배지·진행률 바·에폭·손실/지표 스파크라인·워커·경과 시간. 상세: 실시간 로그(SignalR), 학습 곡선, 재현성 레코드, [취소]·[같은 설정으로 재실행]·[결과 모델 보기](Phase 1 상세로) |
| 사전학습 미러 관리 | 자산 목록·라이선스·해시, [추가](파일 업로드 또는 URL 가져오기, Admin), 워커 다운로드 통계 |

---

## 10. 설치·운영
- 설치: `BODA-VMS-TrainWorker-x.y.z.msi`(프로젝트 `src/BODA.VMS.MLOps.TrainWorker.Setup`, WiX 6, self-contained win-x64 publish 수확) → 서비스 `BodaVmsTrainWorker`(자동·지연 시작, **계정 기본 LocalSystem** — 세션 0 CUDA 가 막히는 환경만 지정 계정으로 바꾼다), 설정 파일 `%ProgramData%\BODA VMS TrainWorker\worker.json`(서버 URL·토큰(DPAPI)·캐시 경로·GPU 인덱스·상한값). 설정 경로 2가지: 마법사 "워커 연결 설정" 화면 또는 무인 `msiexec … SERVERURL= WORKERTOKEN=` → 둘 다 deferred CA 로 `configure` 를 부른다. 설정 없이 설치하면 서비스는 등록만 되고 시작하지 않는다(토큰 없이 뜨면 곧 종료하므로). 업그레이드는 기존 `worker.json` 존재(AppSearch)로 시작 조건을 만족한다.
- 오프라인: `scripts/make-wheel-bundle.ps1`(pip wheel + 번들만으로 설치 검증 + torch CUDA 빌드 검사) → `WheelBundle\` → `*-offline.msi`(`[설치 폴더]\wheels`, configure `--wheels`).
- 로그: `%ProgramData%\BODA VMS TrainWorker\logs\worker-yyyyMMdd.log`(워커 자체 파일 로거, 14일 보존) + 작업별 `jobs\{id}\train.log`.
- CLI: `configure`(--server --token [--name --python --wheels --cache --gpu --cpu]) · `diag`(venv 부트스트랩 + 자기진단 출력).
- 업데이트: 워커 MSI 독립 배포. 프로토콜 버전(`X-Worker-Protocol: 1`)으로 서버가 호환 검사, 불일치 시 `Disabled(업데이트 필요)`.
- 올인원 형태(관리 서버 = 워커 PC): 같은 MSI 를 설치하고 서버 URL 을 `http://localhost:5310` 로.
- 절차 문서: `docs/워커 설치 가이드.md`.
- 백업 대상 아님(캐시·작업 폴더는 재생성 가능). 아티팩트는 서버 스토리지에 있다.

---

## 11. 테스트
- **프로토콜 단위**: 가짜 `train_fake.py`(프로토콜 라인만 출력, 지연·오류·무응답 시나리오) 로 상태 전이·워치독·취소·OOM 재시도·업로드 재시도.
- **서버 단위**: 배정 규칙(유형·상태·디스크), ack 타임아웃 재큐, 하트비트 소실 → WorkerLost 재큐, 중복 작업 경고, 인자 화이트리스트 거부.
- **환경**: 자기진단 결과에 따른 Disabled 전이, 허용 목록 밖 패키지 거부.
- **E2E(환경 변수 게이트, GPU PC)**: Web 작업 제출 → 워커 학습(샘플 데이터셋 3 에폭) → ModelVersion 생성 → 레시피 바인딩 → VMS 동기화 → 검사. 기존 `TrainingServiceDFineE2ETests` 를 워커 호스트로 확장.
- **부하**: 워커 3대·작업 10개 큐, 로그 스트리밍 1/s 코얼레싱 확인.

---

## 12. 작업 분해

| # | 작업 | 위치 | 비고 |
|---|---|---|---|
| 1 | `TrainingService`·프로토콜 파서를 VMS.Core 에서 재사용 가능한 형태로 정리(인자 화이트리스트·ArgumentList·이벤트) | VMS | WPF 도구도 같은 코드 사용 |
| 2 | 서버: Worker/TrainingJob/JobLogChunk/JobArtifact/PretrainedAsset 엔티티·마이그레이션·상태 머신·배정 API | Web | — |
| 3 | 서버: 사전학습 미러 API·관리 화면, 스크립트 매니페스트(서명 해시) 배포 | Web | 스크립트 원본은 VMS 리포 `scripts\` |
| 4 | 워커 프로젝트: Agent·Environment(venv 부트스트랩·자기진단)·JobRunner·Cache | VMS.TrainWorker | Worker Service 템플릿 |
| 5 | 워커 MSI(WiX, 서비스 등록, 설정 마법사 최소 UI 또는 CLI `worker.exe configure`) | VMS.MasterSetup 형제 프로젝트 | 오프라인 wheel 번들 옵션 |
| 6 | Web 화면: Workers·작업 생성·작업 목록/상세(SignalR 로그)·재실행 | Web | — |
| 7 | 아티팩트 → ModelVersion(Candidate) 연결(Phase 1 검증 파이프라인 재사용) | Web | — |
| 8 | 세그멘테이션 스크립트 `train_rfdetr_seg.py`(COCO 변환 포함) + YoloSegTool 규약 판별 | VMS | 별도 PR |
| 9 | 문서: 워커 설치 가이드, 별책 매뉴얼 §2 를 "웹에서 학습" 절로 확장, 배포 정책 §2.6 | VMS docs | — |

의존: 1 → 2 → (3, 4) → 5 → 6 → 7 → 9, 8 은 병행.

---

## 13. 미결
- 서비스 세션 0 에서의 CUDA 접근 — 일부 드라이버/보안 정책에서 제한. 대안: 로그온 세션의 사용자 프로세스로 실행(스케줄러 작업) 또는 지정 계정.
- 데이터셋 export 의 전송 형식 — zip 단일 파일 vs 매니페스트 + 파일 단위 병렬 다운로드(재개 가능). 대형(수십 GB) 데이터셋이면 후자.
- 학습 중간 체크포인트 업로드(장시간 작업의 워커 손실 대비) 도입 여부.
- OCR(PaddlePaddle) 환경을 같은 venv 에 둘지 별도 venv 로 분리할지 — paddle 과 torch 의 CUDA 의존 충돌 검토.
- 워커 다중 GPU(같은 PC 2 GPU) 지원 시점.

## 변경 이력
| 버전 | 날짜 | 내용 |
|---|---|---|
| v0.1 | 2026-09-08 | 초안 |
| v0.2 | 2026-09-10 | §10 설치 패키지 실물 반영(MSI 프로젝트·LocalSystem·설정 경로·오프라인 번들·파일 로그·CLI). 실증: 허용 목록 결함 2건 수정(`transformers<5` 가 rfdetr≥1.9 와 충돌 → `>=5.1,<6`; `torch<2.8` 이 PyPI CPU 빌드를 끌어옴 → `<2.7`, cu124 Windows wheel 은 2.6.0 까지), 진단에 CPU 빌드 검출 추가. 실 GPU(RTX 4060) 한 바퀴 통과: 제출 → 워커 → D-FINE 2 에폭 → ONNX 검증 → Candidate(약 50초). 남은 것: 실제 서비스 설치(세션 0 CUDA) 현장 확인, `train_dfine.py` 학습 곡선 아티팩트, `pretrainedRef` 없는 작업의 제출 시점 거부 |
