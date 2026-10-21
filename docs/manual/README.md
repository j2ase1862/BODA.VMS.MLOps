# 매뉴얼

docx 두 개를 만드는 곳입니다. **독자가 다릅니다.**

| 문서 | 본문 | 독자 |
|---|---|---|
| `BODA_VMS_MLOps_사용자매뉴얼_v<판>.docx` | `manual.json` | 라벨링·학습·승격을 하는 사용자 |
| `BODA_VMS_MLOps_설치가이드_v<판>.docx` | `install.json` | 서버·워커를 세우는 관리자 |

나눈 이유는 개정 주기와 반출 단위가 다르기 때문입니다. 사용자 매뉴얼에 PowerShell 명령을 섞으면
둘 다 읽기 어려워집니다. 사용자 매뉴얼의 부록 B 는 "무엇이 어디에 서나" 개요만 남기고
실제 절차는 설치 가이드로 보냅니다.

## 다시 만들기

```bash
# 1. 서버를 띄운다 (그림은 살아 있는 화면에서 찍습니다)
dotnet run --project src/BODA.VMS.MLOps.Server --urls http://localhost:5310

# 2. 화면 캡처
node docs/manual/capture.js

# 3. docx 생성
node docs/manual/gen_manual.js                # 사용자 매뉴얼
node docs/manual/gen_manual.js install.json   # 설치 가이드
```

## 파일

| 무엇 | 어디 |
|---|---|
| 사용자 매뉴얼 본문 | `manual.json` |
| 설치 가이드 본문 | `install.json` |
| 그림 | `screenshots/` (`capture.js` 가 만듭니다) |
| 화면 캡처 | `capture.js` — Chrome CDP, npm 의존 없음 |
| docx 생성 | `gen_manual.js` — 전역 `docx` 패키지를 씁니다 |

**docx 를 직접 편집하지 마세요.** 다시 만들면 사라집니다. 본문은 json 을 고칩니다.

## 블록 종류

`p` · `h2` · `steps` · `table` · `figure` · `note` · `warn` · `code`.
`code` 의 `text` 는 문자열도 되고 줄 배열도 됩니다(배열이 고치기 쉽습니다). `caption` 은 선택입니다.
본문 어디서나 `` `…` `` 로 감싼 토막은 고정폭으로 나갑니다 — 경로·옵션·서비스 이름에 씁니다.

**고정폭은 Consolas 입니다.** 받는 PC 에 반드시 있는 글꼴이라야 합니다. D2Coding 은 따로 설치해야 하고,
없는 PC 에서는 Word 가 비례 글꼴로 바꿔 명령이 한 줄에 안 맞습니다.

## 다시 찍으면 다시 가려야 하는 것

`17_msi_wizard.png` 의 "Python 3.12" 줄에는 **그 PC 의 사용자 이름이 찍힙니다**
(`C:\Users\<이름>\AppData\...`). 고객에게 나가는 문서라 그 부분만 덮어 두었습니다 —
원본 PNG 를 다시 만들면 가림도 사라지므로 다시 칠하세요.

`06_workers.png` 는 **위쪽 줄(GPU-01 · GPU-02)만 남기고 잘라 두었습니다.** 개발 DB 에 남은 시험 워커
(`dev-rtx4060` 두 대 · `msi-test`)가 아래 줄에 찍히는데, 그 카드에는 개발 PC 이름이 나옵니다.
다시 찍으면 다시 자르세요.

**docx 에서 가리지 마세요.** Word 에서 이미지 위에 도형을 얹으면 그 문서에서는 지워지지만,
`gen_manual.js` 를 다시 돌리는 순간 없어집니다. 가림은 `screenshots/` 의 PNG 에 넣습니다.

**판을 올릴 때는 json 의 `version` 과 맨 끝 "개정 이력" 부록을 함께 고칩니다.** 파일 이름이 `version` 에서 나오므로 옛 판 docx·pdf 는 그대로 남습니다.

**그림을 찍는 대상을 고정해 둡니다.** 데이터셋 상세(`03`)는 `MLOPS_DOC_DETAIL`(기본 `라인A 스크래치 검출`), 모델 돌려 보기(`18`)는
`MLOPS_DOC_TRY_MODEL`(기본 `라인C 각인 검출(실모델)`)·`MLOPS_DOC_TRY_CONF`(기본 0.15) 로 고릅니다. 이름이 없으면 예전처럼 첫 것을 고르는데,
DB 상태에 따라 그림이 바뀝니다. 새 사진 훑기(`19`)는 `MLOPS_DOC_AL_DATASET`(기본 `라인C 명판 사진`)·`MLOPS_DOC_AL_TAG`(기본 `명판-신규`) 로 고르고,
시험용 가짜 모델 계열(`e2e-live-…`)은 사본에서 보관해 두어야 기본 후보 모델로 잡히지 않습니다. 2026-10-21 판은 명판 사진을
밝기·확대·기울임·흐림으로 바꾼 여섯 장에 그 태그를 달아 라인 NG 로 올려 찍었습니다. 돌려 보기 그림은 **실제로 무언가를 찾는 검출 모델**이어야 상자가 보입니다 — 2026-10-21 판은
실모델(object·logo)에 명판 사진과 T자 피팅 사진을 섞어 "찾음" · "후보 없음 (최고 0.12)" · "0.05 미만" 이 한 장에 보이게 했습니다.

MSI 마법사 화면(`17_msi_wizard`)은 워커가 **설치되지 않은** PC 에서만 찍힙니다.
설치된 상태에서는 `WelcomeDlg` 의 [다음] 이 `NOT Installed` 조건이라 유지 관리 모드로 빠집니다.
같은 PC 에서 찍어야 한다면 버전만 올려 MSI 를 따로 빌드하고(`-p:Version=<다음 버전>`),
설정 화면까지 간 뒤 **[설치] 전에 취소**하세요. "시스템이 수정되지 않았습니다" 가 나오면 그대로입니다.

## 설치 절차를 고칠 때

**절차의 정본은 `install.json` 한 벌입니다.** `install-server-service.ps1` 의 옵션이나 워커 MSI 의
설치 흐름을 바꾸면 여기를 고치세요.

`docs/서버 설치 가이드.md`·`docs/워커 설치 가이드.md` 는 얇은 포인터로 줄여 두었습니다.
거기 남은 것은 고객 문서에 넣지 않는 저장소 전용 내용(개발 PC 겸용 주의, 패키지 빌드, 실측 기록)뿐이므로
절차를 그쪽에 다시 적지 마세요 — 두 벌이 되는 순간 한쪽이 조용히 낡습니다
(`-WebApiKey` 가 md 옵션 표에서 한동안 빠져 있었습니다).

## 알아 둘 것

**그림은 개발 서버에서 찍습니다.** `capture.js` 는 개발 토큰(`Auth:EnableDevTokens`)으로 로그인합니다.
운영 서버에서는 그 길이 막혀 있어 돌지 않습니다.

**시드 데이터가 있어야 그림이 의미가 있습니다.** 데이터셋·모델·워커가 비어 있으면 빈 화면만 찍힙니다.
라벨링 화면은 **미라벨 이미지가 남아 있는 데이터셋**을 골라야 "라벨 없음" 상태의 캔버스가 찍힙니다
(다 라벨링된 데이터셋도 열리기는 합니다 — 첫 장이 열립니다). 사진이 아예 없는 데이터셋만
빈 화면입니다. `capture.js` 가 검출·분류에서 각각 미라벨이 남은 데이터셋을 골라 씁니다.

**예제 데이터는 개발 DB 의 복사본으로 씁니다.** `src/BODA.VMS.MLOps.Server/mlops-dev.db` 와 `.dev-storage` 를
임시 폴더에 복사하고, 그 복사본을 가리켜 다른 포트로 띄웁니다 — 5310 은 그 PC 의 MLOps 서비스가 쓰고 있을 수 있고,
그림을 맞추느라 보관·삭제한 것이 원본 개발 DB 에 남지 않게 하려는 것입니다.

```bash
ASPNETCORE_ENVIRONMENT=Development \
ConnectionStrings__DefaultConnection="Data Source=<복사본>\mlops-dev.db" \
Mlops__StorageRoot="<복사본>\storage" \
Monitoring__ProductionWebUrl="http://127.0.0.1:5399" \
dotnet run --project src/BODA.VMS.MLOps.Server --urls http://localhost:5311

MLOPS_URL=http://localhost:5311 node docs/manual/capture.js 01_dashboard 02_datasets …
```

개발 DB 에는 통합 시험이 남긴 것(`it-…`·`e2e-live-…`·`재현 A …` 데이터셋·모델·판)이 섞여 있습니다.
찍기 전에 API 로 데이터셋·모델은 보관(`PATCH … {"isArchived":true}`)하고, 판은 지웁니다.
수집 사진(`15_image_pool`)에는 라인 사진이 있어야 하므로 `POST /api/images/line-ng` 로 `lineId`·`verdict:` 태그를
붙여 몇 장 올립니다. 2026-10-20 판은 GS 시험 자료의 이상 탐지 사진(T자 피팅)을 썼습니다.

**모니터링 그림(13 · 14)은 운영 웹이 있어야 찍힙니다.** 운영 웹 대신 `/api/history/model-outcomes` 하나만
답하는 작은 가짜 서버를 띄워 `Monitoring:ProductionWebUrl` 로 가리킵니다. `modelVersion` 은 `mv:<버전 id 32자리>`,
요청의 `endDate` 가 지금이면 최근 구간, 아니면 기준 구간으로 답합니다. 재학습 창(14)은 **학습 작업에서 나온 버전**이
나빠져 있어야 [재학습] 버튼이 생깁니다.

**목차는 Word 가 채웁니다.** `gen_manual.js` 는 목차 *필드*만 넣습니다. 필드를 갱신하지 않으면
받는 사람 화면에 항목이 비어 보이므로, 배포 전에 Word 로 한 번 열어 `Ctrl+A` → `F9` 로 갱신한 뒤
저장하세요. 지금 저장된 파일은 갱신된 상태입니다.

**이미지에 `type` 을 반드시 줍니다.** `docx` 9.x 의 `ImageRun` 은 `type` 이 없으면 미디어 파일을
`.undefined` 확장자로 넣고, 그러면 **Word 가 "파일이 손상되었습니다" 로 열지 못합니다.**
`gen_manual.js` 는 `type: "png"` 를 넘깁니다.
