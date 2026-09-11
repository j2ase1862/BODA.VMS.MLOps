# CLAUDE.md

이 파일은 Claude Code(claude.ai/code)가 이 리포에서 작업할 때 참고하는 지침입니다.

## 이 리포가 하는 일

BODA VMS MLOps 플랫폼. 검사 모델의 등록·배포(Phase 1), 데이터 관리(Phase 2),
웹에서 제출하는 학습(Phase 3), 브라우저 라벨링(Phase 4), 모니터링·재학습(Phase 5)을 구현합니다.
설계 근거는 `docs/` 의 문서 3종이고, 코드 주석은 그 문서의 절 번호를 인용합니다.
동작을 바꿀 때는 해당 절과 어긋나지 않는지 먼저 확인하세요.

관련 리포: VMS 런타임 `D:\Repo\VMS`, 운영 웹 `D:\Project\BODA.VMS.Web`.

## 빌드·테스트

```bash
dotnet build BODA.VMS.MLOps.slnx
dotnet test  BODA.VMS.MLOps.slnx
dotnet run --project src/BODA.VMS.MLOps.Server   # http://localhost:5310/swagger
```

테스트는 GPU 없이 전부 돕니다. 서버를 띄운 채로 빌드하면 실행 파일이 잠겨 실패하므로 먼저 종료하세요.

## 반드시 지킬 규약

**학습 스크립트 stdout 프로토콜은 계약입니다.** `[EPOCH] [LOSS] [ACC] [PROGRESS] [ONNX] [DONE] [ERROR]`.
워커와 WPF 도구와 CLI 가 같은 `train_*.py` 를 실행합니다. 프로토콜을 바꾸면
`VMS.Core.Contracts` 패키지, `scripts/` 의 스크립트 5종, VMS 리포의 소비자를 한 번에 갱신해야 합니다.

**모델 규약은 런타임이 정의합니다.** 레지스트리는 판별과 검증만 하고 변환하지 않습니다.
판별은 `InferenceSession` 없이 protobuf 만 읽습니다. 업로드 경로에서 ONNX 세션을 만들지 마세요.
서버가 `InferenceSession` 을 여는 곳은 SAM 보조(`SamAssistService`) 하나뿐이고, 그 모델은
관리자가 설치 폴더에 둔 것이라 업로드된 파일이 아닙니다.

**하이퍼파라미터는 화이트리스트 밖이면 거부합니다.** `HyperparamWhitelist` 가 서버(작업 생성)와
워커(실행 직전) 양쪽에서 같은 규칙으로 검사합니다. 스크립트 인자는 `ProcessStartInfo.ArgumentList` 로만
넘기고 문자열로 이어 붙이지 않습니다.

**라이선스 게이트.** 백본과 패키지는 상위 문서 §8 의 허용 목록 안에서만 씁니다.
YOLO 계열(AGPL)은 라이선스 필드 없이는 등록도 Production 승격도 되지 않습니다.
패키지를 추가하면 `scripts/requirements-allowlist.txt` 와 `PackageAllowlist` 를 함께 고칩니다.

**모니터링은 당겨 오기만 합니다.** 운영 웹(BODA.VMS.Web)에 MLOps 를 위한 쓰기 경로를 만들지 마세요.
그쪽은 멈추면 안 되는 시스템이고, 쓰기를 만들면 검사 라인의 안정성에 MLOps 가 얹힙니다.
못 당겨 와도 이 기능만 "볼 수 없음" 으로 남고 나머지는 그대로 돌아야 합니다.
판단 규칙은 `Core/Monitoring/ModelHealth.cs` 한 곳입니다 — 화면이나 서비스에서 따로 판단하지 마세요.
**이 값으로 모델을 자동으로 내리거나 학습을 자동으로 걸지 않습니다.** 사람이 봅니다.

**자동과 승인의 경계는 정해져 있습니다.** 사람이 한 번 누르면 후보 모델까지 자동으로 가고,
**라인에 나가는 결정(Staging·Production 승격·롤백)은 어떤 경우에도 사람이 합니다.**
학습에 무엇이 들어가는지도 사람이 정합니다 — 자동으로 뜨는 판은 `ReviewedOnly` 를 켜서
사람이 검토한 라벨만 담습니다. 끄는 것은 사람이 그 화면에서 고를 때뿐입니다.
사전 라벨을 검토 없이 학습에 흘리면 오염은 모델이 나온 뒤에야 드러납니다.
데이터 임계치로 사람 없이 도는 재학습은 넣지 마세요 — 이 1차 템플릿이 몇 사이클 돌아
데이터 품질 필터를 믿을 수 있게 된 뒤에 다시 볼 일입니다.

**작업 상태는 서버가 소유합니다.** 워커는 보고만 하고 전이는 `TrainingJobStateMachine` 이 검증합니다.
새 상태나 전이를 넣을 때는 이 클래스와 그 테스트를 먼저 고치세요. 전이를 이 클래스 밖에서 직접 대입하지 마세요.

**워커 토큰의 범위는 좁게 유지합니다.** 워커 역할은 `Viewer`·`Line` 정책에 넣지 않습니다.
워커가 읽어야 하는 것은 데이터셋 export, 사전학습 파일, 스크립트뿐이고 각 엔드포인트에 `WorkerOrEngineer` 로 명시합니다.
`WorkerScopeTests` 가 이 경계를 지킵니다.

**업로드된 ONNX 는 신뢰할 수 없는 입력입니다.** 파싱은 `OnnxSafeReader` 만 씁니다.
`VMS.Core.Contracts` 의 `OnnxMetadataReader`·`DetectionModelFormatProbe.Probe(path)` 는 길이 필드를
남은 바이트와 대조하지 않아 조작된 varint 하나로 파싱이 무한 반복됩니다. 경로를 받는 그 API 들은 부르지 마세요.
(VMS 리포는 PR #445 로 보강됐고 이 리포가 참조하는 `1.33.*` 패키지부터 그 보강이 들어 있습니다. 그래도 업로드 경로는 `OnnxSafeReader` 를 유지합니다 — 파서 두 벌을 한 곳으로 합치는 것은 별도 결정입니다.)

**작업·워커 행은 낙관적 동시성으로 보호됩니다.** `IConcurrencyStamped` 의 `Stamp` 를 `MlopsDbContext` 가 갱신합니다.
워커 보고는 `SaveWorkerReportAsync` 로 저장해 충돌 시 409 를 내고, 감독자는 충돌하면 그 주기를 건너뜁니다.
long-poll 안에서는 회전마다 `ChangeTracker.Clear()` 로 워커 상태를 다시 읽습니다.

## 코드 배치

`Core` 는 순수 로직만 담습니다. HTTP·EF·파일 시스템 의존을 넣지 마세요. 서버와 워커가 함께 씁니다.
`Contracts` 는 DTO 만 담습니다. 서버 전용 타입을 여기 두지 마세요.
서버는 최소 API 를 씁니다. 엔드포인트는 얇게 두고 규칙은 `Services/` 로 내립니다.

## 이 리포에서 밟았던 함정

**JWT 는 키·발급자·audience 세 값이 BODA.VMS.Web 과 같아야 합니다.** Web 은 **`BODA.VMS.Web.Client`** 를 audience 로 발급·검증합니다.
우리 `Jwt:Audience` 가 `BODA.VMS.Web` 이던 동안은 Web 로그인 토큰이 서명이 맞아도 401 이었습니다 (2026-09-10 dev PC 서비스 설치에서 발견,
기본값을 `BODA.VMS.Web.Client` 로 맞춤). 키는 **토큰을 발급하는 그 Web 인스턴스**의 키입니다 — 운영 웹과 dev 웹은 키가 다르므로
어느 Web 에 로그인해 들어올지에 따라 `Jwt__Key` 를 정합니다. 운영 웹으로 나가는 모니터링 토큰은 `ServiceTokenIssuer.Issue(..., audience:)` 에
`Monitoring:Audience` 를 명시합니다 — `Jwt:Audience` 를 누가 바꿔도 그 호출이 깨지지 않게.
그 토큰의 역할은 `Viewer` 하나로 둡니다 — 새더라도 운영 데이터를 고칠 수 없어야 합니다.

**운영 웹 역할은 `Core/Auth/WebRoleMapping.cs` 한 곳에서 옮깁니다.** 운영 웹은 Admin · User · Guest 만 싣고
MLOps 는 Viewer ⊂ Labeler ⊂ Engineer ⊂ Admin 을 봅니다 (User→Engineer, Guest→Viewer — 사용자 결정 2026-09-11).
**서버(JwtBearer `OnTokenValidated`)와 관리 화면(`TokenStore.Parse`)이 둘 다 이 규칙을 불러야 합니다.**
화면은 토큰을 브라우저에서 직접 읽어 버튼을 보일지 정하므로, 서버만 옮기면 권한은 있는데 버튼이 안 보입니다.
모르는 역할은 옮기지 않습니다 — 조용히 권한이 생기면 안 됩니다. `WebRoleMappingTests` 가 두 쪽을 함께 봅니다.
운영 웹에 역할을 더하는 쪽으로 풀지 마세요 — 운영 웹은 GS 인증 범위 안입니다.

**모델 식별자 규약은 두 리포에 두 벌 있습니다.** VMS 의 `DlModelIdentity` 와 여기의 `ModelVersionTag` 가
같은 `mv:{32자}` 를 만듭니다. 한쪽만 바꾸면 집계가 **조용히 빕니다** — 오류도 없이 모델 줄이 안 나타납니다.
`ModelVersionTagTests` 가 형식을 글자 그대로 못 박고 있으니 그 시험을 함께 고치지 않으면 못 지나갑니다.
VMS 쪽 `DlModelVersion` 열은 50자 제한이라, 여기서 형식을 늘리면 라인의 업로드가 400 으로 막힙니다.

**사진 파일은 우리가 열고 우리가 닫습니다.** `SKCodec.Create(경로)` 와 `SKBitmap.Decode(경로)` 에
경로를 그대로 넘기지 마세요. SkiaSharp 이 안에서 스트림을 만들고 그것이 언제 닫히는지 우리가 정할 수 없습니다.
한가할 때는 곧 닫히지만 부하가 있으면 늦어지고, 그 사이에 업로드가 임시 파일을 제자리로 옮기려다
"다른 프로세스가 사용 중" 으로 터집니다 — 그 요청은 500 이고, 재현이 어려워
"가끔 업로드가 실패한다" 로만 보입니다. `ImageProcessor.OpenImage` 로 열어 스트림을 넘기세요.
이 리포의 시험 묶음이 다섯 번에 한 번씩 흔들리던 원인이 이것이었습니다.

**내용 주소 저장소는 같은 키가 동시에 들어옵니다.** 경로가 sha 라, 같은 사진을 두 라인이 같은 순간에
올리면 두 요청이 같은 자리를 노립니다. 있는지 보고 옮기는 사이에 남이 끼어들 수 있으므로
`CommitTempAsync` 는 실패를 그냥 올리지 않고 다시 보고, 계속 막히면 복사로 넘어갑니다.
같은 키면 내용이 같으니 진 쪽은 이긴 파일을 그대로 쓰면 됩니다. 이 자리를 고칠 때는
`ArtifactStorageTests` 의 동시 커밋 시험을 함께 보세요.

**열거형 라우트·쿼리 바인딩은 대소문자를 구분합니다.** 응답 JSON 은 camelCase 라 그 값을 URL 에
되돌려 넣으면 기본 바인딩이 실패합니다. `EnumBinding.Parse` 를 쓰세요.

**바인딩 실패는 `BadHttpRequestException` 입니다.** `RouteHandlerOptions.ThrowOnBadRequest` 를 켜 두었고
`ApiExceptionMiddleware` 가 `ApiError` JSON 으로 바꿉니다. 이 예외를 500 으로 흘리지 마세요.

**테스트의 가짜 시계를 토큰 발급에 쓰면 안 됩니다.** JWT 검증은 실제 시각을 봅니다.

**Windows PowerShell 5.1 은 네이티브 exe 인자의 따옴표를 지웁니다.** curl 로 JSON 을 보낼 때는
본문을 파일에 쓰고 `--data-binary @file` 로 넘기세요.

**Windows 경로는 대소문자를 구분하지 않습니다.** 개발 산출물 `storage` 를 지우려다 소스 폴더 `Storage\` 를
통째로 날린 적이 있습니다. 그래서 개발 스토리지 경로를 `.dev-storage` 로 두었습니다.
무엇이든 지우기 전에 대상이 실제로 무엇인지 먼저 확인하세요.

**Razor 는 `v@expr` 을 이메일 주소로 봅니다.** `v@version.Number` 는 식으로 평가되지 않고 글자 그대로 나갑니다.
`v@(version.Number)` 로 감싸세요. 한글 접미사도 같습니다 — `@count개` 는 `개` 를 멤버로 읽으므로 `@(count)개` 로 씁니다.
둘 다 컴파일은 통과하고 화면에서만 드러나므로, 화면을 고쳤으면 브라우저로 한 번 띄워 보세요.

**MudBlazor 7 은 provider 네 개를 모두 요구합니다.** `MudThemeProvider`, `MudPopoverProvider`,
`MudDialogProvider`, `MudSnackbarProvider`. 팝오버 provider 가 빠지면 `MudSelect`·`MudTooltip` 이
렌더 중 예외를 던져 화면 일부가 죽습니다. 빌드는 통과합니다.

**System.Text.Json 은 기본 인코더로 한글을 `\uXXXX` 로 바꿉니다.** DB 에 넣은 JSON 문자열과
사용자가 입력한 문자열이 달라져 태그·클래스 검색이 어긋나고, 오류 메시지도 읽을 수 없게 나갑니다.
`MlopsJson.Options` 와 서버의 JSON 설정이 `UnsafeRelaxedJsonEscaping` 을 쓰는 이유입니다.

**브라우저의 `<img>` 는 Authorization 헤더를 붙이지 못합니다.** 이미지 엔드포인트는
`/api/auth/image-cookie` 가 내려 주는 쿠키로도 인증합니다. 로그인 흐름을 바꿀 때 이 호출을 빠뜨리면
썸네일과 캔버스가 전부 401 이 됩니다.

**색·서체·모서리는 `MlopsTheme.cs` 한 곳에서 정합니다.** 화면마다 색을 직접 쓰지 마세요.
강조색 `#FF4052` 는 지금 누를 것과 켜져 있는 것에만 씁니다.
라벨 색 팔레트는 그 강조색과 겹치지 않게 파랑부터 시작하고,
`label-canvas.js` 의 `PALETTE` 와 `Labeling.razor`·`DatasetDetail.razor` 의 `ColorOf` 가 같은 순서여야 합니다.

**라벨 좌표는 어디서나 0~1 정규화입니다.** 캔버스 안, API, DB, 내보내기 매니페스트가 모두 같습니다.
픽셀 좌표로 바꾸는 곳은 COCO 내보내기 하나뿐입니다.

**클래스 선택(숫자키·클래스 버튼)은 다음에 그릴 것만 정합니다.** 선택된 라벨을 함께 바꾸면
방금 그린 박스가 조용히 다른 클래스가 됩니다. 이미 붙은 라벨은 `setAnnotationClass` 로만 바꿉니다.

**품질 지표로 사진을 자동으로 버리지 않습니다.** 업로드할 때 흐림(라플라시안 분산)과
노출(평균 밝기·날아간 화소 비율)을 재어 `Image` 에 남기고, 목록에서 `maxSharpness`·
`minClippedRatio`·`blurriestFirst` 로 좁혀 볼 수 있게만 했습니다.
**흐림 값은 절대 기준이 아닙니다** — 무늬가 촘촘한 부품은 흐려도 크고, 매끈한 면은 또렷해도 작습니다.
해상도에도 비례해, 같은 사진을 원본 해상도로 재면 축소본의 135% 가 나옵니다.
그리고 **노출 과다는 흐림 값이 오히려 오릅니다** (날아간 화소가 딱딱한 경계를 만들어서).
그래서 두 지표를 함께 둡니다. 문턱 하나로 자동 판정하면 멀쩡한 샘플이 조용히 사라집니다.
지표는 축소본(긴 변 2048) 기준이라 원본 해상도가 다른 사진끼리 견주면 안 됩니다.
이 값이 생기기 전에 올라온 이미지는 null 이고, 0 으로 채우지 마세요 — "가장 흐린 사진" 으로 보입니다.

**사전 라벨링은 검출만 지원합니다.** `PrelabelService` 는 YOLO·D-FINE 만 후처리합니다.
분류·이상탐지·OCR 을 넣으려면 각 규약의 후처리와 그것을 확인할 실제 모델이 함께 필요합니다.
확인하지 못한 후처리는 "그럴듯한" 라벨을 만들고, 사람이 검토로 승인하면 다음 학습 데이터가 오염됩니다.
좌표는 레터박스 규약을 씁니다 — `LetterBox` 가 배율·여백과 되돌리기를 한 곳에서 정합니다.

**세그멘테이션은 RF-DETR 규약(`rfdetrseg`)입니다.** 검출에서 D-FINE 을 고른 것과 같은 이유입니다 —
Ultralytics(AGPL-3.0) 없이 상용 배포를 하기 위해서입니다. YOLO-Seg 도 규약으로 남아 있지만
학습 스크립트는 RF-DETR 쪽만 있습니다.
출력은 `dets[N,Q,4]` 정규화 cxcywh · `labels[N,Q,C]` 로짓(시그모이드) · `masks[N,Q,mh,mw]` 마스크 로짓입니다.
**`labels` 의 마지막 열은 배경입니다** (rf-detr 의 `lwdetr.py`: "background slot (index detection_num_classes-1)").
그 열을 빼지 않으면 배경이 최고 점수인 질의가 물체로 나옵니다.
질의마다 최고 클래스 하나만 고르지 마세요 — 한 질의가 두 클래스에서 문턱을 넘을 때 하나가 조용히 사라집니다.
전처리는 레터박스가 아니라 **늘려 맞추는 리사이즈 + ImageNet 정규화**입니다.
RF-DETR 의 export 는 `metadata_props` 를 하나도 쓰지 않으므로 `train_rfdetr_seg.py` 가 새깁니다.
그것이 없으면 클래스 이름과 배경 열 위치가 사라집니다.

**클래스 순서는 RF-DETR 에게 물어봅니다.** 그쪽이 카테고리를 한 번 거르고(주석이 하나도 없는 상위 분류를
버립니다) 0 부터 다시 번호를 매깁니다. 그 규칙을 흉내 내면 언젠가 어긋나고, 어긋나면 라벨이 통째로
한 칸 밀린 채 학습이 끝납니다 — 아무 오류도 나지 않습니다. `RFDETR._load_classes(dataset_dir)` 를 쓰세요.

**입력 변은 모델마다 다른 배수여야 합니다.** `patch_size × num_windows` 입니다 — nano 는 12, preview 는 56.
값을 고정하지 말고 `model_config` 에서 읽으세요.

**SAM 모델 파일은 저장소에 없습니다.** `src/BODA.VMS.MLOps.Server/models/sam` 에 4개 파일
(`mobile_sam_{encoder,decoder}.onnx` 와 각각의 `.onnx.data`)이 한 세트로 있어야 켜집니다.
없으면 기능만 꺼진 채 서버가 그대로 뜨므로, "SAM 버튼이 안 보인다" 는 대개 파일 문제입니다.
`/api/sam/status` 의 `message` 가 이유를 말해 줍니다. 테스트는 기본으로 SAM 을 끄고 돌리고
(`MlopsApiFactory` 가 경로를 비웁니다), 모델이 있는 PC 에서만 `SamInferenceTests` 가 실제 추론을 확인합니다.

**SAM 클릭은 확정 전까지 라벨이 아닙니다.** 미리보기는 캔버스 안에만 있고 Enter 로 확정해야
`annotations` 에 들어갑니다. 이미지를 바꾸거나 모드를 바꾸면 `clearSam` 이 미리보기를 버립니다 —
남겨 두면 다음 사진에 이전 사진의 폴리곤이 붙습니다.

**SAM 후보의 실제 자리는 캔버스가 들고 있습니다.** Tab 은 JS 안에서만 처리되므로,
`cycleSam` 이 `OnSamIndexChanged` 로 알려 주지 않으면 화면 옆의 "후보 n/N" 표시가 어긋납니다.
Blazor 쪽 `_samIndex` 를 스스로 계산하지 마세요.

**디코더 후보 출력은 그래프 패치로 만듭니다.** `mobile_sam_decoder_multi.onnx` 는
`scripts/patch_sam_decoder_multimask.py` 가 원본 디코더에 `all_low_res_masks`·`all_iou_predictions`
출력을 더한 것입니다. 그 출력이 없으면 서버가 알아서 후보 하나로 돕니다 (`SamAssistService.MultiMask`).
`/api/sam/status` 의 `multiMask` 는 모델을 올린 뒤에만 참이므로, 시험에서 이 값으로 갈래를 나누지 마세요.

**클릭한 자리를 담은 덩어리를 고릅니다.** `MaskContour` 에 씨앗 점을 주면 그 점을 담은 덩어리를
돌려줍니다. 이 인자를 빠뜨리면 가장 큰 덩어리로 돌아가고, 배경 점으로 끊긴 물체에서
사용자가 집은 조각이 버려집니다.

**허용 목록의 버전 범위는 wheel 인덱스가 정합니다.** `requirements-allowlist.txt` 의 torch 는 cu124 인덱스에서 받는데,
그 인덱스의 Windows cp312 wheel 은 2.6.0 까지뿐입니다. 상한을 `<2.8` 로 두면 pip 가 PyPI 의 2.7.1(CPU 빌드)을 고르고
진단은 `cuda_available=false` 로 워커를 Disabled 로 만듭니다. 반대로 transformers 는 `<5` 로 막으면 rfdetr≥1.9(`transformers>=5.1`)와
ResolutionImpossible 입니다. 범위를 바꾸면 `py -3.12 -m venv` 로 빈 환경에 실제로 설치해 `worker_diag.py` 가 `+cu124` 를 보고하는지
확인하세요 (2026-09-10 실증). `worker_diag.py` 는 CPU 빌드(`torch.version.cuda is None`)를 실패 항목으로 냅니다.

**워커 설치 패키지는 솔루션 밖입니다.** `src/BODA.VMS.MLOps.TrainWorker.Setup`(WiX 6)은 빌드마다 워커를 self-contained 로
publish 하므로 slnx 에 넣지 않았습니다. `dotnet build src/BODA.VMS.MLOps.TrainWorker.Setup -c Release` 로 따로 만듭니다.
MSI 문자열은 코드페이지 949 라 `—`·`▸`·`…` 같은 문자를 속성값·대화상자 텍스트에 넣으면 WIX0311 로 막힙니다(주석은 괜찮습니다).
대화상자 텍스트의 `[…]` 는 속성 참조로 읽히므로 "[토큰 발급]" 같은 표기는 ICE03 입니다.

## 손대면 안 되는 것

`scripts/train_*.py` 와 `scripts/export_mobile_sam.py` 는 VMS 리포(`VMS.DeepLearning/scripts`)의 복사본입니다.
여기서 고치지 말고 VMS 리포에서 고친 뒤 가져오세요. `train_fake.py` 와 `worker_diag.py` 는 이 리포 것입니다.
