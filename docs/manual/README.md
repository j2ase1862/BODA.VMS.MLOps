# 사용자 매뉴얼

`BODA_VMS_MLOps_사용자매뉴얼_v1.0.docx` 를 만드는 곳입니다.

## 다시 만들기

```bash
# 1. 서버를 띄운다 (그림은 살아 있는 화면에서 찍습니다)
dotnet run --project src/BODA.VMS.MLOps.Server --urls http://localhost:5310

# 2. 화면 캡처
node docs/manual/capture.js

# 3. docx 생성
node docs/manual/gen_manual.js
```

## 파일

| 무엇 | 어디 |
|---|---|
| 본문 | `manual.json` |
| 그림 | `screenshots/` (`capture.js` 가 만듭니다) |
| 화면 캡처 | `capture.js` — Chrome CDP, npm 의존 없음 |
| docx 생성 | `gen_manual.js` — 전역 `docx` 패키지를 씁니다 |

**docx 를 직접 편집하지 마세요.** 다시 만들면 사라집니다. 본문은 `manual.json` 을 고칩니다.

## 알아 둘 것

**그림은 개발 서버에서 찍습니다.** `capture.js` 는 개발 토큰(`Auth:EnableDevTokens`)으로 로그인합니다.
운영 서버에서는 그 길이 막혀 있어 돌지 않습니다.

**시드 데이터가 있어야 그림이 의미가 있습니다.** 데이터셋·모델·워커가 비어 있으면 빈 화면만 찍힙니다.
라벨링 화면은 특히 **미라벨 이미지가 남아 있는 데이터셋**이 있어야 캔버스에 사진이 뜹니다 —
다 라벨링된 것을 열면 "라벨링할 이미지가 없습니다" 만 나옵니다. `capture.js` 가 검출·분류에서
각각 그런 데이터셋을 골라 씁니다.

**목차는 Word 가 채웁니다.** `gen_manual.js` 는 목차 *필드*만 넣습니다. 필드를 갱신하지 않으면
받는 사람 화면에 항목이 비어 보이므로, 배포 전에 Word 로 한 번 열어 `Ctrl+A` → `F9` 로 갱신한 뒤
저장하세요. 지금 저장된 파일은 갱신된 상태입니다.

**이미지에 `type` 을 반드시 줍니다.** `docx` 9.x 의 `ImageRun` 은 `type` 이 없으면 미디어 파일을
`.undefined` 확장자로 넣고, 그러면 **Word 가 "파일이 손상되었습니다" 로 열지 못합니다.**
`gen_manual.js` 는 `type: "png"` 를 넘깁니다.
