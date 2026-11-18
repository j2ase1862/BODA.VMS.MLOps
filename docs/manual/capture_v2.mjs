// MLOps 사용자 매뉴얼 그림을 데모 서버에서 찍는다 — Playwright. 왼쪽 메뉴가 보이는 그림(01~16 · 18 · 19 · 23 · 26 · 27 · 30 · 33)도
// 여기서 찍는다(Admin 으로 로그인하므로 메뉴에 [감사 로그] 까지 보인다). 17(MSI 마법사)만 따로다.
//
//   set PLAYWRIGHT_MJS=file:///<playwright 설치 폴더>/index.mjs
//   set MLOPS_URL=http://localhost:5611        (데모 서버 — 운영 서버에 돌리지 마세요)
//   set MLOPS_DOC_USER=admin & set MLOPS_DOC_PW=<데모 관리자 비밀번호>
//   set MLOPS_DOC_OCR_IMG=<글자가 찍힌 사진 경로>   (21 사진 올리기용)
//   node docs/manual/capture_v2.mjs [그림 이름 앞부분 …]
//
// 데모 데이터(데이터셋 '검출 테스트' · '각인 OCR' · '피팅 외관 분류', 모델 'GS-A 검출' · '피팅 검출', 운영 웹 레시피
// 'A동 1호 조립검사 표준' 의 바인딩, 라인 모니터링 집계)가 있어야 그림이 의미가 있다 — README 의 "데모 서버" 참고.
// 이 스크립트는 데모 데이터를 바꾼다 — OCR 데이터셋을 만들고, 사진을 올리고, 라벨을 저장하고, 라인 PC 를 하나 등록한다.
// 화면을 바꾸는 그림(12 · 32 · 21 · 23 · 29)은 뒤쪽에 돈다. 찍은 뒤에는 데모 DB 를 기준 사본으로 되돌린다.
// 토큰 · 임시 비밀번호처럼 그 자리에서만 보이는 비밀 값은 찍기 전에 화면에서 가린다.
import path from "node:path";
import { fileURLToPath } from "node:url";

const PW_MJS = process.env.PLAYWRIGHT_MJS || "file:///D:/Repo/VMS/tools/webcap/node_modules/playwright/index.mjs";
const { chromium } = await import(PW_MJS);
const BASE = process.env.MLOPS_URL || "http://localhost:5611";
if (/:5310\b/.test(BASE)) throw new Error("운영 포트(5310)에는 돌리지 않습니다 — 데모 서버 주소를 주세요.");
const SHOT = path.join(path.dirname(fileURLToPath(import.meta.url)), "screenshots");
const OCR_IMG = process.env.MLOPS_DOC_OCR_IMG;
const OCR_NAME = "라인C 각인 OCR";
// 그림마다 고정해 두는 데모 데이터 이름 — 없으면 그 그림만 실패(_fail_*.png)로 남는다
const DET_DATASET = process.env.MLOPS_DOC_DETAIL || "검출 테스트";
const CLS_DATASET = process.env.MLOPS_DOC_CLS_DATASET || "피팅 외관 분류";
const OCR_LABEL_DATASET = process.env.MLOPS_DOC_OCR_DATASET || "각인 OCR";
const OCR_LABEL_TEXT = process.env.MLOPS_DOC_OCR_TEXT || "REV C 2026/10/13";
const TRY_MODEL = process.env.MLOPS_DOC_TRY_MODEL || "GS-A 검출";
const RECIPE_PICK = process.env.MLOPS_DOC_RECIPE || "A동 1호 조립검사 표준";
const ONLY = process.argv.slice(2);
const want = (f) => ONLY.length === 0 || ONLY.some((o) => f.startsWith(o));

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 });
const wait = (ms) => page.waitForTimeout(ms);
const dlg = () => page.locator(".mud-dialog").last();
const field = (root, label) => root.locator(".mud-input-control").filter({ hasText: label }).first();
const btn = (root, name) => root.getByRole("button", { name }).first();
const popItem = (text) => page.locator(".mud-popover-open .mud-list-item").filter({ hasText: text }).first();

async function go(p, text) {
  await page.goto(BASE + p, { waitUntil: "domcontentloaded" });
  if (text) await page.getByText(text).first().waitFor({ timeout: 30000 });
  await wait(1500);
}
async function pick(root, label, option) {
  await field(root, label).click();
  await wait(400);
  await popItem(option).click();
  await wait(400);
}
/** 빨간 상자로 짚기 — 찍은 뒤 지운다 */
async function mark(locators, pad = 4) {
  const boxes = [];
  for (const l of locators) { const b = await l.boundingBox(); if (b) boxes.push(b); }
  await page.evaluate(({ boxes, pad }) => {
    for (const b of boxes) {
      const d = document.createElement("div");
      d.className = "doc-mark";
      Object.assign(d.style, { position: "fixed", left: b.x - pad + "px", top: b.y - pad + "px", width: b.width + pad * 2 + "px",
        height: b.height + pad * 2 + "px", border: "3px solid #e8384a", borderRadius: "6px", zIndex: 99999, pointerEvents: "none" });
      document.body.appendChild(d);
    }
  }, { boxes, pad });
}
const unmark = () => page.evaluate(() => document.querySelectorAll(".doc-mark").forEach((d) => d.remove()));
/** 토큰 · 비밀번호 원문을 가린다 */
const maskSecrets = () => page.evaluate(() => {
  const walk = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  for (let n; (n = walk.nextNode());) n.nodeValue = n.nodeValue.replace(/\b(ln|wk)_[A-Za-z0-9_-]{8,}/g, "$1_••••••••••••••••••••••••");
  for (const i of document.querySelectorAll("input, textarea")) if (/^(ln|wk)_/.test(i.value)) i.value = i.value.slice(0, 3) + "••••••••••••••••••••••••";
});

async function shot(file, target) {
  const out = path.join(SHOT, file + ".png");
  if (target) await target.screenshot({ path: out }); else await page.screenshot({ path: out });
  await unmark();
  console.log("✔", file);
}

// ── 로그인 화면 (로그인 전) ──
await go("/");
await page.waitForSelector("input[type=password]", { timeout: 60000 });
await wait(800);
if (want("20_login")) {
  await page.locator("input:not([type=password])").first().fill(process.env.MLOPS_DOC_USER || "admin");
  await page.locator("input[type=password]").first().fill("••••••••");
  await mark([page.getByText("로그인 유지").first()]);
  await shot("20_login");
}
await page.locator("input:not([type=password])").first().fill(process.env.MLOPS_DOC_USER || "admin");
await page.locator("input[type=password]").first().fill(process.env.MLOPS_DOC_PW);
await btn(page, "로그인").click();
await page.getByText("모델 계열").first().waitFor({ timeout: 60000 });

const full = (file) => shot(file);

const SCENES = {
  // ── 왼쪽 메뉴가 보이는 화면 (데이터를 바꾸지 않음) ──
  async "01_dashboard"() {
    await go("/", "최근 학습 작업"); await wait(2500);
    await full("01_dashboard");
  },
  async "02_datasets"() {
    await go("/datasets", DET_DATASET);
    await full("02_datasets");
  },
  async "03_dataset_detail"() {
    await openDataset(DET_DATASET); await wait(1500);
    await full("03_dataset_detail");
  },
  async "04_training"() {
    await go("/training", "작업 제출");
    await full("04_training");
  },
  async "05_dataset_versions"() {
    await go("/dataset-versions", "버전 업로드");
    await full("05_dataset_versions");
  },
  async "06_workers"() {
    await go("/workers", "워커 등록");
    await full("06_workers");
  },
  async "07_pretrained"() {
    // 접힌 채로 찍는다 — 펼치면 자산의 출처 주소(바깥 사이트)가 그림에 남는다
    await go("/pretrained", "자산 추가");
    await full("07_pretrained");
  },
  async "08_models"() {
    await go("/models", "새 모델");
    await full("08_models");
  },
  async "09_bindings"() {
    // 운영 웹 레시피 목록에서 골라 바로 조회한다 — 레시피 ID 를 손으로 넣지 않는다
    await go("/bindings", "운영 웹 레시피에서 고르기");
    await field(page, "운영 웹 레시피에서 고르기").click(); await wait(500);
    await popItem(RECIPE_PICK).click();
    await page.getByText(/활성 바인딩/).first().waitFor({ timeout: 30000 }); await wait(1200);
    await mark([field(page, "운영 웹 레시피에서 고르기")]);
    await full("09_bindings");
  },
  async "10_line_clients"() {
    await go("/line-clients", "라인 PC 등록");
    await full("10_line_clients");
  },
  async "13_monitoring"() {
    await go("/monitoring", /모델 \d+개를 보고/); await wait(1000);
    await full("13_monitoring");
  },
  async "14_retrain"() {
    await go("/monitoring", /모델 \d+개를 보고/);
    await btn(page, "재학습").click();
    await page.getByText("재학습 제출").first().waitFor({ timeout: 30000 }); await wait(2000);
    await full("14_retrain");
    await btn(dlg(), "취소").click().catch(() => {}); await wait(400);
  },
  async "15_image_pool"() {
    // 라인이 보낸 사진만, 데이터셋에 담긴 것도 함께 — 한 장 골라 [데이터셋에 담기] 가 나온 상태
    await go("/images", "데이터셋에 안 담긴 것만");
    await pick(page, "출처", "라인 NG"); await wait(1500);
    await page.getByText("데이터셋에 안 담긴 것만").first().click(); await wait(2500);
    await page.locator(".mud-main-content img").first().click(); await wait(1200);
    await full("15_image_pool");
  },
  async "16_worker_register"() {
    // 이름만 채운 상태 — [발급] 을 누르면 실제 워커가 생기고 토큰이 그림에 남는다
    await go("/workers", "워커 등록");
    await btn(page, "워커 등록").click(); await wait(800);
    await dlg().locator("input[type=text]").first().fill("GPU-01"); await wait(600);
    await full("16_worker_register");
    await btn(dlg(), "취소").click().catch(() => {}); await wait(400);
  },
  async "18_model_try"() {
    // 운영 버전의 [돌려 보기] — 검증 · 시험 사진 12장
    await go("/models", TRY_MODEL);
    const card = page.locator(".mud-card, .mud-paper").filter({ hasText: TRY_MODEL }).filter({ hasText: "열기" }).last();
    await card.getByText("열기").first().click(); await wait(2000);
    await page.locator("tr").filter({ hasText: "운영" }).first().getByText("돌려 보기").first().click();
    await btn(page, /돌려 보기|다른 사진으로/).waitFor({ timeout: 30000 }); await wait(1500);
    await btn(page, /돌려 보기|다른 사진으로/).click();
    await page.locator('.mud-main-content img[src*="/thumb"]').first().waitFor({ timeout: 120000 }); await wait(2500);
    await full("18_model_try");
  },
  async "19_active_learning"() {
    // 라인이 보낸 사진만 훑는다 — 모델이 자신 없어 한 사진부터 나온다
    await openDataset(DET_DATASET);
    await page.goto(page.url().replace(/\/$/, "") + "/active-learning", { waitUntil: "domcontentloaded" });
    await btn(page, "훑기").waitFor({ timeout: 30000 }); await wait(2000);
    await pick(page, "수집 경로", "라인 NG");
    await btn(page, "훑기").click();
    await page.locator('.mud-main-content img[src*="/thumb"]').first().waitFor({ timeout: 180000 }); await wait(2500);
    await full("19_active_learning");
  },
  async "33_audit_log"() {
    // 동작으로 거른 모습 — 스테이지를 바꾼 기록(Promoted)과 그때 적은 사유
    await go("/audit", /건 중/);
    await field(page, "동작 (일부)").locator("input").fill("Promoted");
    await btn(page, "조회").click(); await wait(2000);
    await full("33_audit_log");
  },
  async "11_labeling_detection"() {
    await openLabeling(DET_DATASET);
    await full("11_labeling_detection");
  },

  // ── 여기부터 데모 데이터를 바꾼다 ──
  async "12_labeling_classification"() {
    // 숫자키 1 로 이 사진에 첫 클래스를 붙인 모습. 찍은 뒤 저장하고 넘어가 화면을 떠날 때 묻지 않게 한다
    await openLabeling(CLS_DATASET);
    // 제목에 가 있는 포커스 테두리가 그림에 남지 않게 먼저 푼다 (단축키는 문서 전체에서 받는다)
    await page.evaluate(() => document.activeElement?.blur());
    await page.keyboard.press("1"); await wait(800);
    await mark([page.locator(".mud-paper").filter({ hasText: /^라벨 \(1\)/ }).first()]);
    await full("12_labeling_classification");
    await btn(page, "저장하고 다음").click(); await wait(1500);
  },
  async "23_labeling_ocr"() {
    await openLabeling(OCR_LABEL_DATASET);
    // 글자 줄을 감싸는 사각형 — 1440×900 화면에서 데모 사진(OCR10.png)의 'REV C 2026/10/13' 줄 자리
    await page.locator(".mud-button-root").filter({ hasText: "글자" }).first().click(); await wait(300);
    await page.mouse.move(546, 476); await page.mouse.down();
    await page.mouse.move(680, 500, { steps: 8 }); await page.mouse.move(802, 524, { steps: 8 }); await page.mouse.up();
    await wait(800);
    const d = dlg();
    await field(d, "인식 문자열").locator("input").fill(OCR_LABEL_TEXT);
    await wait(400);
    await btn(d, "적용").click(); await wait(800);
    // 창은 사각형을 가리므로, 적용한 뒤 사각형과 라벨 목록(적은 글자)이 함께 보이게 찍는다
    await mark([page.locator(".mud-paper").filter({ hasText: /^라벨 \(1\)/ }).first()]);
    await shot("23_labeling_ocr");
    await btn(page, "저장하고 다음").click(); await wait(1500);
  },
  async "32_new_dataset"() {
    await go("/datasets", "새 데이터셋");
    await btn(page, "새 데이터셋").click(); await wait(600);
    const d = dlg();
    await field(d, "이름").locator("input").fill(OCR_NAME);
    await pick(d, "작업 유형", "OCR");
    await field(d, "클래스").locator("input, textarea").first().fill("text");
    await field(d, "설명").locator("input, textarea").first().fill("명판의 로트 번호 각인");
    await wait(400);
    await shot("32_new_dataset", d);
    await btn(d, "만들기").click(); await wait(1500);
  },
  async "21_upload_dialog"() {
    await openDataset(OCR_NAME);
    await btn(page, "사진 올리기").click(); await wait(600);
    const d = dlg();
    await d.locator("input[type=file]").first().setInputFiles(OCR_IMG);
    await wait(800);
    await field(d, "태그").locator("input").fill("명판, 각인");
    await field(d, "라인 ID").locator("input").fill("LINE-C");
    await wait(400);
    await shot("21_upload_dialog", d);
    await btn(d, /올리기/).click();
    await page.getByText(/올렸습니다/).first().waitFor({ timeout: 60000 }).catch(() => {});
    await wait(1500);
  },
  async "22_roi_crop"() {
    await go("/images", "데이터셋에 안 담긴 것만");
    const imgs = page.locator(".mud-main-content img");
    for (const i of [0, 1, 2]) { await imgs.nth(i).click(); await wait(250); }
    await btn(page, /ROI 로 자르기/).click(); await wait(1500);
    const d = dlg();
    const cv = await d.locator("canvas").first().boundingBox();
    await page.mouse.move(cv.x + cv.width * 0.30, cv.y + cv.height * 0.28); await page.mouse.down();
    await page.mouse.move(cv.x + cv.width * 0.5, cv.y + cv.height * 0.5, { steps: 6 });
    await page.mouse.move(cv.x + cv.width * 0.70, cv.y + cv.height * 0.72, { steps: 6 }); await page.mouse.up();
    await wait(800);
    await mark([d.getByText(/자른 크기/).first()]);
    await shot("22_roi_crop", d);
    await btn(d, /취소|닫기/).click().catch(() => {}); await wait(400);
  },
  async "31_prelabel"() {
    await openDataset("검출 테스트");
    await btn(page, "모델로 미리 채우기").click(); await wait(1000);
    const d = dlg();
    await field(d, "모델 버전").click(); await wait(400);
    await page.locator(".mud-popover-open .mud-list-item").first().click(); await wait(500);
    await shot("31_prelabel", d);
    await btn(d, "닫기").click(); await wait(400);
  },
  async "24_version_create"() {
    await openDataset("검출 테스트");
    await btn(page, "버전 만들기").click(); await wait(800);
    const d = dlg();
    await mark([d.getByText("검토를 마친 이미지만 담기").first(), d.getByText(/라벨 없는 이미지도 담기/).first()]);
    await shot("24_version_create", d);
    await btn(d, "취소").click(); await wait(400);
  },
  async "25_train_submit"() {
    await go("/training", "작업 제출");
    await btn(page, "작업 제출").click(); await wait(1000);
    const d = dlg();
    await field(d, "데이터셋 버전").click(); await wait(400);
    await page.locator(".mud-popover-open .mud-list-item").filter({ hasText: /Detection|검출/ }).first().click(); await wait(400);
    await field(d, "결과를 등록할 모델 계열").click(); await wait(400);
    await popItem("피팅 검출").click(); await wait(400);
    await field(d, "사전학습 가중치").click(); await wait(400);
    await page.locator(".mud-popover-open .mud-list-item").filter({ hasText: /dfine/i }).first().click(); await wait(600);
    await shot("25_train_submit", d);
    await btn(d, "취소").click(); await wait(400);
  },
  async "26_training_detail"() {
    await go("/training", "작업 제출");
    const row = page.locator("tr").filter({ hasText: "성공" }).first();
    await row.getByText("상세", { exact: true }).first().click();
    await page.getByText("재현성").first().waitFor({ timeout: 30000 }); await wait(2500);
    // 로그 보관 폴더는 데모 서버의 임시 경로라 설치 기본값 모양으로 바꿔 보인다
    await page.evaluate(() => {
      const walk = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
      for (let n; (n = walk.nextNode());) n.nodeValue = n.nodeValue.replace(/[A-Z]:[\\/][^ ]*job-logs/g, "<데이터 폴더>\\job-logs");
    });
    // fullPage 로 찍으면 본문이 왼쪽 메뉴 밑으로 밀려 찍힌다 — 창을 본문 높이만큼 늘려 한 화면으로 찍고 되돌린다
    const h = await page.evaluate(() => Math.ceil(document.querySelector(".mud-main-content").getBoundingClientRect().height));
    await page.setViewportSize({ width: 1440, height: Math.max(900, h) }); await wait(1500);
    await page.screenshot({ path: path.join(SHOT, "26_training_detail.png") });
    await page.setViewportSize({ width: 1440, height: 900 }); await wait(500);
    console.log("✔ 26_training_detail");
  },
  async "27_model_detail"() {
    await go("/models", "새 모델");
    const card = page.locator(".mud-card, .mud-paper").filter({ hasText: "피팅 검출" }).filter({ hasText: "열기" }).last();
    await card.getByText("열기").first().click(); await wait(2000);
    await page.locator("tr").nth(1).getByRole("button", { name: /상세/ }).first().click(); await wait(1500);
    await shot("27_model_detail");
    await page.keyboard.press("Escape"); await wait(400);
  },
  async "28_stage_change"() {
    await go("/models", "새 모델");
    const card = page.locator(".mud-card, .mud-paper").filter({ hasText: "피팅 검출" }).filter({ hasText: "열기" }).last();
    await card.getByText("열기").first().click(); await wait(2000);
    const row = page.locator("tr").filter({ hasText: "후보" }).first();
    await row.getByRole("button", { name: /스테이지/ }).first().click(); await wait(800);
    const d = dlg();
    await field(d, "사유").locator("input, textarea").first().fill("돌려 보기 확인 — 검증·시험 12장 모두 찾음");
    await wait(300);
    await shot("28_stage_change", d);
    await btn(d, "취소").click(); await wait(400);
  },
  async "29_line_token"() {
    await go("/line-clients", "라인 PC 등록");
    await btn(page, "라인 PC 등록").click(); await wait(600);
    const d = dlg();
    await field(d, "이름").locator("input").fill("라인C 1호기");
    await field(d, "라인 ID").locator("input").fill("LINE-C");
    await btn(d, "발급").click();
    await d.getByText(/한 번만/).first().waitFor({ timeout: 15000 }); await wait(600);
    await maskSecrets(); await wait(200);
    await shot("29_line_token", d);
    await btn(d, /닫기|확인/).click().catch(() => {}); await wait(400);
  },
  async "30_members"() {
    await go("/members", "역할 주기");
    await mark([btn(page, "역할 주기")]);
    await shot("30_members");
  },
};

async function openDataset(name) {
  await go("/datasets", name);
  const card = page.locator(".mud-card, .mud-paper").filter({ hasText: name }).filter({ hasText: "열기" }).last();
  await card.getByText("열기").first().click();
  await page.getByText(name).first().waitFor();
  await wait(1500);
}
async function openLabeling(name) {
  await go("/datasets", name);
  const card = page.locator(".mud-card, .mud-paper").filter({ hasText: name }).filter({ hasText: "라벨링" }).last();
  await card.getByText("라벨링", { exact: true }).first().click();
  await page.locator("canvas").first().waitFor({ timeout: 30000 });
  await wait(3000);
}

for (const [name, fn] of Object.entries(SCENES)) {
  if (!want(name)) continue;
  try { await fn(); } catch (e) { console.log("✘", name, "—", e.message.split("\n")[0]); await page.screenshot({ path: path.join(SHOT, "_fail_" + name + ".png") }); }
}
await browser.close();
