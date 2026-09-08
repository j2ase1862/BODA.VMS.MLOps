# CLAUDE.md

이 파일은 Claude Code(claude.ai/code)가 이 리포에서 작업할 때 참고하는 지침입니다.

## 이 리포가 하는 일

BODA VMS MLOps 플랫폼. 검사 모델의 등록·배포(Phase 1), 데이터 관리(Phase 2),
웹에서 제출하는 학습(Phase 3), 브라우저 라벨링(Phase 4)을 구현합니다.
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

**작업 상태는 서버가 소유합니다.** 워커는 보고만 하고 전이는 `TrainingJobStateMachine` 이 검증합니다.
새 상태나 전이를 넣을 때는 이 클래스와 그 테스트를 먼저 고치세요. 전이를 이 클래스 밖에서 직접 대입하지 마세요.

**워커 토큰의 범위는 좁게 유지합니다.** 워커 역할은 `Viewer`·`Line` 정책에 넣지 않습니다.
워커가 읽어야 하는 것은 데이터셋 export, 사전학습 파일, 스크립트뿐이고 각 엔드포인트에 `WorkerOrEngineer` 로 명시합니다.
`WorkerScopeTests` 가 이 경계를 지킵니다.

**업로드된 ONNX 는 신뢰할 수 없는 입력입니다.** 파싱은 `OnnxSafeReader` 만 씁니다.
`VMS.Core.Contracts` 의 `OnnxMetadataReader`·`DetectionModelFormatProbe.Probe(path)` 는 길이 필드를
남은 바이트와 대조하지 않아 조작된 varint 하나로 파싱이 무한 반복됩니다. 경로를 받는 그 API 들은 부르지 마세요.
(같은 보강이 VMS 리포에도 필요합니다.)

**작업·워커 행은 낙관적 동시성으로 보호됩니다.** `IConcurrencyStamped` 의 `Stamp` 를 `MlopsDbContext` 가 갱신합니다.
워커 보고는 `SaveWorkerReportAsync` 로 저장해 충돌 시 409 를 내고, 감독자는 충돌하면 그 주기를 건너뜁니다.
long-poll 안에서는 회전마다 `ChangeTracker.Clear()` 로 워커 상태를 다시 읽습니다.

## 코드 배치

`Core` 는 순수 로직만 담습니다. HTTP·EF·파일 시스템 의존을 넣지 마세요. 서버와 워커가 함께 씁니다.
`Contracts` 는 DTO 만 담습니다. 서버 전용 타입을 여기 두지 마세요.
서버는 최소 API 를 씁니다. 엔드포인트는 얇게 두고 규칙은 `Services/` 로 내립니다.

## 이 리포에서 밟았던 함정

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

## 손대면 안 되는 것

`scripts/train_*.py` 와 `scripts/export_mobile_sam.py` 는 VMS 리포(`VMS.DeepLearning/scripts`)의 복사본입니다.
여기서 고치지 말고 VMS 리포에서 고친 뒤 가져오세요. `train_fake.py` 와 `worker_diag.py` 는 이 리포 것입니다.
