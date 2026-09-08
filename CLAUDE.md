# CLAUDE.md

이 파일은 Claude Code(claude.ai/code)가 이 리포에서 작업할 때 참고하는 지침입니다.

## 이 리포가 하는 일

BODA VMS MLOps 플랫폼. 검사 모델의 등록·배포(Phase 1)와 웹에서 제출하는 학습(Phase 3)을 구현합니다.
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

## 손대면 안 되는 것

`scripts/train_*.py` 는 VMS 리포(`VMS.DeepLearning/scripts`)의 복사본입니다.
여기서 고치지 말고 VMS 리포에서 고친 뒤 가져오세요. `train_fake.py` 와 `worker_diag.py` 는 이 리포 것입니다.
