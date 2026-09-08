# MLOps Phase 1 — 모델 레지스트리·배포 설계 명세

문서 버전: v0.1 (2026-09-08) · 상위 문서: `D:\MLOps 개발 문서.md` §5.1 · 개발 위치: BODA.VMS.Web (`D:\Project\BODA.VMS.Web`) + VMS 리포 연동 PR

---

## 1. 목표와 완료 기준

**목표**: 학습된 ONNX 모델을 웹에 등록하고, 버전·스테이지를 관리하며, 레시피의 도구에 바인딩해 라인 PC 가 자동으로 내려받아 검사에 쓰게 한다. 롤백은 바인딩 이력에서 이전 버전을 고르는 것으로 끝난다.

**완료 기준 (Definition of Done)**
1. Web 에서 ONNX 업로드 → 규약 판별·메타데이터 검증 → ModelVersion(Candidate) 생성.
2. Staging → Production 승격이 권한·감사 로그와 함께 동작.
3. 레시피 도구의 ModelPath 에 `model://{modelId}@{version}` 참조를 저장하고, VMS/VisionSetup 이 레시피 동기화 시 아티팩트를 캐시로 내려받아 경로를 치환한다.
4. 서버가 없거나 오프라인이어도 캐시에 있는 모델로 검사가 계속된다.
5. 생산 이력에 modelVersionId 가 기록되고, 모델 버전별 NG 율이 화면에 보인다.
6. WPF 도구(VMS.DeepLearning)에 [레지스트리에 업로드] 버튼이 있다.

**비목표**: 데이터셋 관리, 학습 실행, 브라우저 라벨링 (Phase 2~4).

---

## 2. 용어

| 용어 | 뜻 |
|---|---|
| Model | 하나의 검사 목적을 가진 모델 계열 (예: "라인A 스크래치 검출"). 작업 유형·클래스 집합을 가진다 |
| ModelVersion | Model 의 불변 아티팩트 1개 (ONNX 파일 + 메타데이터 + 지표). 번호는 Model 안에서 1부터 증가 |
| Stage | Candidate(등록 직후) → Staging(테스트 라인 허용) → Production(운영 허용) → Retired |
| Binding | 레시피의 특정 도구(ToolId)가 어느 ModelVersion 을 쓰는지. 이력이 남는다 |
| 아티팩트 캐시 | 라인 PC 의 `%LocalAppData%\BODA VISION AI\models\{sha256}.onnx` |
| 모델 참조 | 레시피 파라미터 값 `model://{modelId}@{version}` (또는 `model://{modelId}@production`) |

---

## 3. 데이터 모델 (EF Core)

```
Model            : Id(Guid) · Name · TaskType(Detection|Classification|Anomaly|Segmentation|Ocr) · Classes(json string[])
                   · Description · CreatedBy · CreatedAt · IsArchived
ModelVersion     : Id(Guid) · ModelId(FK) · Number(int, Model 내 유일) · Sha256(64, 유일) · ArtifactKey · SizeBytes
                   · Format(dfine|yolo|yoloseg|classifier|anomaly|ppocr) · InputSize(int?) · Classes(json) · Metadata(json: names,imgsz,model_format,backbone,pretrained,license 등 ONNX metadata_props 전체)
                   · Metrics(json: map50,val_loss,epochs…) · Source(Upload|TrainingJob) · TrainingJobId(Guid?) · License(string)
                   · Stage(enum) · StageChangedBy · StageChangedAt · Notes · CreatedBy · CreatedAt
ModelBinding     : Id · RecipeId · ToolId(string, 레시피 내 도구 GUID) · ModelVersionId · Mode(Pinned|FollowProduction)
                   · BoundBy · BoundAt · PreviousBindingId(Guid?) · IsActive
ModelStageHistory: Id · ModelVersionId · FromStage · ToStage · ChangedBy · ChangedAt · Reason
InspectionRecord : (기존) + ModelVersionId(Guid?) ← 검사 시 사용한 버전 (VMS 가 업로드 페이로드에 포함)
```

제약: `(ModelId, Number)` 유일, `Sha256` 유일(같은 파일 재업로드는 기존 버전 반환), Production 은 Model 당 최대 1개(승격 시 이전 Production 은 Staging 으로 강등 — 롤백 편의).

스토리지: `{StorageRoot}\models\{sha256[0:2]}\{sha256}.onnx`. StorageRoot 는 AppSetup Web 초기 구성 카드의 설정값(기본 `%ProgramData%\BODA VMS Web\storage`). 파일 크기 상한 2GB.

---

## 4. 업로드 검증 파이프라인 (서버)

1. 스트리밍 저장(임시 파일) → SHA-256 계산 → 중복이면 기존 ModelVersion 반환(멱등).
2. **구조 검증**: `OnnxMetadataReader`(VMS.Core 로 승격된 공용 코드)로 metadata_props + 그래프 입출력 이름 읽기. 세션은 만들지 않는다.
3. **규약 판별**: `DetectionModelFormatProbe` 로 dfine/yolo, 출력 2개 + `output1` 4D 면 yoloseg, 단일 출력 `[N,nc]` 면 classifier … (판별 실패 = `unknown` 으로 등록 허용하되 경고).
4. **메타데이터 필수**: `names` 없으면 업로드 폼의 클래스 목록을 요구. `imgsz` 없으면 InputSize 입력 요구. `model_format=yolo` 이거나 판별 결과 yolo 면 **라이선스 경고 배너**("Ultralytics YOLO 파생 모델 — Enterprise License 확인") 표시 후 License 필드 필수.
5. Model 의 TaskType·Classes 와 불일치하면 거부(클래스 수·이름 순서).
6. ModelVersion(Candidate) 생성 + 감사 로그 `Model.VersionCreated`.

옵션(백그라운드): 격리 프로세스에서 InferenceSession 생성 → 입력 형상 확인 → `ValidationStatus=Loaded|Failed` 갱신.

---

## 5. API

| 메서드 · 경로 | 요청 | 응답 | 권한 |
|---|---|---|---|
| `GET /api/models?taskType=&archived=false` | — | Model 목록 + 최신/Production 버전 요약 | Viewer |
| `POST /api/models` | `{name, taskType, classes[], description}` | Model | Engineer |
| `PATCH /api/models/{id}` | name/description/archived | Model | Engineer |
| `GET /api/models/{id}/versions` | — | ModelVersion[] (Stage, Metrics, Sha, Size) | Viewer |
| `POST /api/models/{id}/versions` (multipart: file + `meta` json) | `{classes?, inputSize?, license?, notes?, metrics?}` | ModelVersion (201) / 기존 (200, 중복) | Engineer, Worker |
| `GET /api/model-versions/{id}` | — | 상세 (Metadata 전체, StageHistory) | Viewer |
| `GET /api/model-versions/{id}/artifact` | `If-None-Match: "{sha256}"` | 파일 스트림, `ETag: "{sha256}"`, `Content-Length` | Line, Engineer |
| `POST /api/model-versions/{id}/promote` | `{stage: Staging|Production|Retired, reason}` | ModelVersion | Staging: Engineer / Production·Retired: Admin |
| `GET /api/models/{id}/resolve?stage=production` | — | `{modelVersionId, number, sha256, artifactUrl}` | Line |
| `PUT /api/recipes/{recipeId}/tools/{toolId}/model` | `{modelVersionId?, mode: Pinned|FollowProduction}` | ModelBinding | Engineer |
| `GET /api/recipes/{recipeId}/model-bindings` | — | 활성 바인딩 + 이력 | Viewer |
| `POST /api/model-bindings/{id}/rollback` | `{reason}` | 새 ModelBinding(이전 버전) | Engineer |
| `GET /api/models/{id}/stats?from=&to=` | — | 버전별 검사 수·NG 율·평균 신뢰도 | Viewer |

SignalR 허브 `models`: `VersionCreated`, `StagePromoted`, `BindingChanged(recipeId, toolId, modelVersionId)`. VMS 는 기존 레시피 동기화 알림과 같은 채널을 구독한다.

오류 규약: 400(검증 실패, `code`: `ClassMismatch|MissingNames|UnsupportedFormat|TooLarge`), 409(스테이지 전이 불가), 413.

---

## 6. VMS / VisionSetup 연동 (VMS 리포 PR)

### 6.1 모델 참조 해석
- 레시피 파라미터 `ModelPath` 값이 `model://` 로 시작하면 `ModelReferenceResolver`(VMS.Core) 가 처리:
  1. 참조 파싱 → `{modelId, version|production}`.
  2. 로컬 바인딩 캐시(`models\bindings.json`: 참조 → sha256) 조회. 있으면 `models\{sha256}.onnx` 존재 시 즉시 그 경로 반환.
  3. 없거나 `production` 참조면 서버 `resolve` 호출(타임아웃 3초). 성공 시 아티팩트 다운로드(ETag 로 중복 방지, 임시 파일 → 해시 검증 → 원자적 rename) 후 바인딩 캐시 갱신.
  4. 서버 실패 시 캐시된 sha 로 폴백. 캐시도 없으면 도구가 `OnnxLoadException` 으로 실패(시퀀스 계속).
- 치환 시점: 레시피 로드 직후 `RecipeService.PrefetchDeepLearningModels` 와 같은 지점. 도구 객체의 `ModelPath` 는 실제 로컬 경로로 치환하되, 직렬화 시에는 원래 참조를 보존(`ModelReference` 필드 추가, Serializer·Settings VM·HelpContent 5종 세트 갱신 규칙 준수).
- 검사 결과 업로드 페이로드에 `modelVersionId` 추가 (InspectionRecord 연결).

### 6.2 VisionSetup 화면
- Detection/Classify/Anomaly/YOLO-seg/OCR 도구 설정의 Model Path 옆에 **[레지스트리…]** 버튼: 서버의 Model 목록 → 버전 선택(기본 Production 추종) → 참조 저장. 단독 모드(webServerUrl 빈 값)면 버튼 숨김.
- 상태 표시: 참조 모델이면 "model://… (v3, Production, 캐시됨)" 칩. 서버 미도달 시 "캐시 사용" 경고색.

### 6.3 VMS.DeepLearning (WPF)
- 학습 완료 후 [레지스트리에 업로드] 버튼: Model 선택/신규 → `best.onnx` + 학습 정보(`vms_train_info.json` 의 epoch·map50·pretrained·names) 업로드. 단독 모드면 비활성.

### 6.4 동기화 페이로드
- 기존 레시피 동기화 JSON 에 `modelBindings: [{toolId, modelVersionId, sha256, number, mode}]` 추가. VMS 는 이를 바인딩 캐시 선반영에 사용해 첫 검사 전에 다운로드를 끝낸다(프리페치).

---

## 7. Web 화면 (Blazor)

| 화면 | 내용 |
|---|---|
| Models 목록 | 카드/표: 이름·작업 유형·클래스 수·Production 버전·최근 업로드·바인딩된 레시피 수. 필터(작업 유형·라인). [새 모델] |
| Model 상세 | 버전 표(번호·스테이지 배지·형식·입력 크기·mAP·크기·업로더·날짜), [업로드] 드롭존, 버전 행 액션(승격·강등·다운로드·메모). 스테이지 이력 타임라인 |
| 버전 상세 드로어 | 메타데이터 전체(키/값), 지표, 학습 출처, 라이선스, 규약 판별 결과, 바인딩된 레시피 목록 |
| 레시피 편집(기존) | 도구별 "모델" 열 추가: 현재 버전·모드·[변경]·[롤백]. 변경 시 확인 다이얼로그(영향 라인 표시) |
| 모델 통계 | 기간·라인 필터, 버전별 검사 수·NG 율·평균 신뢰도 막대, 버전 전환 시점 마커 |

권한: Viewer(조회) / Engineer(등록·Staging·바인딩) / Admin(Production·Retired). 기존 Role 매트릭스(매뉴얼 §3.6)에 Engineer 추가.

---

## 8. 보안·감사
- 업로드: 확장자 `.onnx` + protobuf 파싱 성공 + 크기 상한. 파일명은 저장하지 않고 sha 로만 참조(경로 조작 방지).
- 다운로드: 라인 PC 토큰(기존 VMS↔Web 인증)으로만. ETag 검증으로 변조 감지(해시 불일치 시 폐기·재시도 1회).
- 감사 로그: `Model.Created/VersionCreated/Promoted/Bound/RolledBack` — 사용자·시각·사유·이전값.
- 라이선스 게이트: Format=yolo 는 License 필드 필수 + Production 승격 시 Admin 확인 체크박스.

---

## 9. 마이그레이션·호환
- 기존 레시피의 로컬 경로 ModelPath 는 그대로 동작(참조 아님). 참조로 바꾸는 것은 선택.
- 단독 모드·구버전 VMS(참조 미지원)가 참조 레시피를 열면 "model:// 참조는 v1.32 이상 필요" 경고 후 도구 실패 처리.
- DB 마이그레이션 1건(4 테이블 + InspectionRecord 열). 스토리지 폴더 생성은 서비스 시작 시.

---

## 10. 테스트
- 서버 단위: 업로드 검증(중복 sha 멱등, 클래스 불일치 400, yolo 라이선스 필수, 2GB 초과 413), 스테이지 전이(Production 단일성·강등), 바인딩 롤백.
- 규약 판별: VMS 리포의 스텁 ONNX(base64) 를 공유 테스트 자산으로 복제(deploy/raw/yolo/yoloseg/classifier).
- VMS 단위: `ModelReferenceResolver` — 캐시 적중·서버 폴백·해시 불일치 폐기·오프라인. 직렬화 왕복(참조 보존).
- E2E: 업로드 → Production 승격 → 레시피 바인딩 → VMS 동기화 → 캐시 파일 sha 일치 → Detection 도구 Run → 생산 이력 modelVersionId 기록.

---

## 11. 작업 분해 (순서)

| # | 작업 | 리포 | 산출물 |
|---|---|---|---|
| 1 | `OnnxMetadataReader`·`DetectionModelFormatProbe`·형식 판별 확장을 VMS.Core 로 이동 (+ VisionSetup 참조 정리) | VMS | PR, 테스트 이동 |
| 2 | DB 엔티티·마이그레이션·스토리지 서비스·업로드 검증 파이프라인 | Web | API + 단위 테스트 |
| 3 | Models 목록/상세/버전 드로어/스테이지 승격 화면 | Web | Blazor 페이지 |
| 4 | 레시피 바인딩 API·화면·SignalR·동기화 페이로드 확장 | Web | — |
| 5 | `ModelReferenceResolver` + 캐시 + 레시피 로드 치환 + 결과 업로드 modelVersionId + Serializer 5종 세트 | VMS | PR (feat=MINOR) |
| 6 | VisionSetup [레지스트리…] 버튼 · DeepLearning [업로드] 버튼 · 단독 모드 숨김 | VMS | 동일 PR 또는 후속 |
| 7 | 모델 통계 화면 + 생산 이력 modelVersion 열 | Web | — |
| 8 | 매뉴얼 §5 도구 설정·§7 Web 새 화면·별책 §1 업로드 절차, 배포 정책 §2.6 | VMS docs | — |

의존: 1 → 2 → (3, 4) → 5 → (6, 7) → 8. MVP 의 나머지 절반(학습 워커)은 Phase 3 명세에서 다룬다.

---

## 12. 미결
- Production 을 라인별로 다르게 둘 필요가 있는가 (라인별 스테이지 vs 모델 분리). 초안은 Model 당 단일 Production + 레시피 Pinned 로 해결.
- 아티팩트 서명(코드 서명 인증서 재사용) 도입 시점 — GS 보안 요구와 맞물려 Phase 1 말에 결정.
- 대용량(>500MB) 모델의 라인 PC 배포 시간 — 프리페치로 흡수 가능한지 현장 대역폭 확인.

## 변경 이력
| 버전 | 날짜 | 내용 |
|---|---|---|
| v0.1 | 2026-09-08 | 초안 |
