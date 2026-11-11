// MLOps 사용자 매뉴얼 v2.0 에서 새로 넣은 그림(20~32)을 찍는다 — Playwright.
//
//   set PLAYWRIGHT_MJS=file:///<playwright 설치 폴더>/index.mjs
//   set MLOPS_URL=http://localhost:5611        (데모 서버 — 운영 서버에 돌리지 마세요)
//   set MLOPS_DOC_USER=admin & set MLOPS_DOC_PW=<데모 관리자 비밀번호>
//   set MLOPS_DOC_OCR_IMG=<글자가 찍힌 사진 경로>   (23 OCR 라벨링용, 예: LOT 260928-A01 이 찍힌 시험 사진)
//   node docs/manual/capture_v2.mjs [그림 이름 앞부분 …]
//
// 기존 그림(01~19)은 capture.js 가 찍는다. 이 스크립트는 데모 데이터를 바꾼다 — OCR 데이터셋을 만들고,
// 사진을 올리고, 라인 PC 를 하나 등록한다. 찍은 뒤에는 데모 DB 를 기준 사본으로 되돌린다.
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

const SCENES = {
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
  async "23_labeling_ocr"() {
    await openDataset(OCR_NAME);
    await btn(page, /라벨링 시작|라벨링/).click();
    await page.locator("canvas").first().waitFor(); await wait(2500);
    // 글자 줄을 감싸는 사각형 — 1440×900 화면에서 시험 사진의 LOT 줄 자리
    await page.locator(".mud-button-root").filter({ hasText: "text" }).first().click(); await wait(300);
    await page.mouse.move(588, 474); await page.mouse.down();
    await page.mouse.move(700, 495, { steps: 8 }); await page.mouse.move(792, 514, { steps: 8 }); await page.mouse.up();
    await wait(800);
    const d = dlg();
    await field(d, "인식 문자열").locator("input").fill("LOT 260928-A01");
    await wait(400);
    await btn(d, "적용").click(); await wait(800);
    // 창은 사각형을 가리므로, 적용한 뒤 사각형과 라벨 목록(적은 글자)이 함께 보이게 찍는다
    await mark([page.locator(".mud-paper").filter({ hasText: /^라벨 \(1\)/ }).first()]);
    await shot("23_labeling_ocr");
    await btn(page, "저장하고 다음").click(); await wait(1500);
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
    await page.screenshot({ path: path.join(SHOT, "26_training_detail.png"), fullPage: true });
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

for (const [name, fn] of Object.entries(SCENES)) {
  if (!want(name)) continue;
  try { await fn(); } catch (e) { console.log("✘", name, "—", e.message.split("\n")[0]); await page.screenshot({ path: path.join(SHOT, "_fail_" + name + ".png") }); }
}
await browser.close();
