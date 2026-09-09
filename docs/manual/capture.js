// MLOps 사용자 매뉴얼용 화면 캡처 — Chrome CDP (Node 24 내장 fetch/WebSocket, npm 불필요)
//
// 서버를 띄워 둔 채로 실행한다:
//   dotnet run --project src/BODA.VMS.MLOps.Server --urls http://localhost:5310
//   node docs/manual/capture.js
//
// 로그인은 개발 토큰(Auth:EnableDevTokens)으로 한다. 운영 서버에서는 그 버튼이 없으므로
// 이 스크립트도 돌지 않는다 — 매뉴얼 그림은 개발 서버에서 만든다.
const { spawn } = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

const CHROME = "C:/Program Files/Google/Chrome/Application/chrome.exe";
const PORT = 9223;
const BASE = process.env.MLOPS_URL || "http://localhost:5310";
const SHOT = path.join(__dirname, "screenshots");
const userDir = path.join(os.tmpdir(), "mlopsdoc_" + Date.now());

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// 캡처할 화면. settle 은 그 화면이 데이터를 받아 그려질 때까지 기다리는 시간(ms).
const PAGES = [
  { file: "01_dashboard", path: "/", settle: 2500 },
  { file: "02_datasets", path: "/datasets", settle: 2000 },
  { file: "04_training", path: "/training", settle: 2000 },
  { file: "05_dataset_versions", path: "/dataset-versions", settle: 2000 },
  { file: "06_workers", path: "/workers", settle: 2000 },
  { file: "07_pretrained", path: "/pretrained", settle: 2000 },
  { file: "08_models", path: "/models", settle: 2000 },
  { file: "09_bindings", path: "/bindings", settle: 2000 },
  { file: "10_line_clients", path: "/line-clients", settle: 2000 },
  // 모니터링은 운영 웹까지 다녀오므로 넉넉히 기다린다. 설정이 없으면 "볼 수 없습니다" 가 찍힌다.
  { file: "13_monitoring", path: "/monitoring", settle: 6000 },
];

async function cdp() {
  const res = await fetch(`http://127.0.0.1:${PORT}/json/list`);
  const targets = await res.json();
  const page = targets.find((t) => t.type === "page");
  if (!page) throw new Error("페이지 대상을 찾지 못했습니다");
  return page.webSocketDebuggerUrl;
}

function connect(url) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(url);
    let id = 0;
    const waiting = new Map();
    ws.addEventListener("message", (e) => {
      const msg = JSON.parse(e.data);
      if (msg.id && waiting.has(msg.id)) {
        const { resolve: done, reject: fail } = waiting.get(msg.id);
        waiting.delete(msg.id);
        msg.error ? fail(new Error(msg.error.message)) : done(msg.result);
      }
    });
    ws.addEventListener("open", () =>
      resolve({
        send: (method, params = {}) =>
          new Promise((res, rej) => {
            const n = ++id;
            waiting.set(n, { resolve: res, reject: rej });
            ws.send(JSON.stringify({ id: n, method, params }));
          }),
        close: () => ws.close(),
      }));
    ws.addEventListener("error", reject);
  });
}

async function evaluate(client, expression) {
  const r = await client.send("Runtime.evaluate", {
    expression, awaitPromise: true, returnByValue: true,
  });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.text + " " + (r.result?.description ?? ""));
  return r.result?.value;
}

async function shoot(client, file) {
  const { data } = await client.send("Page.captureScreenshot", { format: "png", captureBeyondViewport: true });
  const out = path.join(SHOT, file + ".png");
  fs.writeFileSync(out, Buffer.from(data, "base64"));
  const kb = (fs.statSync(out).size / 1024).toFixed(0);
  console.log(`  ${file}.png (${kb} KB)`);
}

async function main() {
  fs.mkdirSync(SHOT, { recursive: true });

  const chrome = spawn(CHROME, [
    "--headless=new", "--disable-gpu", "--hide-scrollbars",
    `--remote-debugging-port=${PORT}`, `--user-data-dir=${userDir}`,
    "--window-size=1600,1000", "--no-first-run", "--no-default-browser-check",
    BASE,
  ], { detached: false, stdio: "ignore" });
  chrome.on("error", (e) => console.log("chrome 실행 실패:", e.message));

  await sleep(3000);
  const client = await connect(await cdp());
  await client.send("Page.enable");
  await client.send("Runtime.enable");

  // 로그인 — 개발 토큰을 받아 브라우저 저장소에 넣는다 (화면의 [Admin 로 시작] 과 같은 일)
  console.log("로그인…");
  const token = await evaluate(client, `
    (async () => {
      const res = await fetch('${BASE}/api/auth/dev-token', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ user: '관리자', roles: ['Admin'] })
      });
      if (!res.ok) return null;
      const body = await res.json();
      localStorage.setItem('mlops.token', body.token);
      // 이미지 엔드포인트는 <img> 가 헤더를 못 붙여 쿠키로도 인증한다
      await fetch('${BASE}/api/auth/image-cookie', { headers: { Authorization: 'Bearer ' + body.token } });
      return body.token.slice(0, 12);
    })()`);
  if (!token) throw new Error("개발 토큰을 받지 못했습니다 — Auth:EnableDevTokens 를 확인하세요");
  console.log("  토큰 " + token + "…");

  for (const page of PAGES) {
    process.stdout.write(`${page.path}\n`);
    await client.send("Page.navigate", { url: BASE + page.path });
    await sleep(page.settle);
    await shoot(client, page.file);
  }

  // 데이터셋 상세와 라벨링 화면은 실제 데이터가 있어야 의미가 있다.
  // 상세는 라벨이 붙은 것이 보기 좋고, 라벨링 화면은 미라벨이 남아 있어야 캔버스에 사진이 뜬다 —
  // 다 라벨링된 데이터셋을 열면 "라벨링할 이미지가 없습니다" 만 나온다.
  const picked = await evaluate(client, `
    (async () => {
      const token = localStorage.getItem('mlops.token');
      const res = await fetch('${BASE}/api/datasets?archived=false', { headers: { Authorization: 'Bearer ' + token } });
      const list = await res.json();
      const detail = list.find(d => (d.stats?.labeled ?? 0) > 0) || list[0];
      const unlabeled = d => (d.stats?.unlabeled ?? 0) > 0;
      // 검출은 그리기 도구(사각형·폴리곤·SAM)가 보이고, 분류는 클래스만 고른다 — 둘 다 담는다
      const detection = list.find(d => unlabeled(d) && d.taskType === 'detection');
      const classification = list.find(d => unlabeled(d) && d.taskType === 'classification');
      return {
        detail: detail ? detail.id : null,
        detection: detection ? detection.id : null,
        classification: classification ? classification.id : null,
      };
    })()`);

  if (picked?.detail) {
    await client.send("Page.navigate", { url: `${BASE}/datasets/${picked.detail}` });
    await sleep(3000);
    await shoot(client, "03_dataset_detail");
  } else {
    console.log("  데이터셋이 없어 상세 화면은 건너뜁니다");
  }

  for (const [key, file] of [["detection", "11_labeling_detection"], ["classification", "12_labeling_classification"]]) {
    if (!picked?.[key]) {
      console.log(`  ${key}: 미라벨 이미지가 없어 건너뜁니다`);
      continue;
    }
    await client.send("Page.navigate", { url: `${BASE}/datasets/${picked[key]}/label` });
    await sleep(4500);
    await shoot(client, file);
  }

  // 재학습 창은 눌러야 나온다. 학습에서 나온 버전이 라인에서 나빠져 있어야 버튼이 생기므로,
  // 없으면 조용히 건너뛴다 — 그림이 없으면 문서 생성기가 자리만 비운다.
  await client.send("Page.navigate", { url: BASE + "/monitoring" });
  await sleep(6000);
  const retrain = await evaluate(client, `
    (() => {
      const b = Array.from(document.querySelectorAll('button'))
                     .find(e => e.innerText.trim() === '재학습');
      if (!b) return false;
      b.click();
      return true;
    })()`);
  if (retrain) {
    await sleep(4000);
    await shoot(client, "14_retrain");
  } else {
    console.log("  재학습을 권할 모델이 없어 그 그림은 건너뜁니다");
  }

  client.close();
  chrome.kill();
  console.log(`\n완료 → ${SHOT}`);
}

main().catch((e) => {
  console.error("실패:", e.message);
  process.exit(1);
});
