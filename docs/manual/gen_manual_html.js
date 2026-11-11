// BODA VMS MLOps 매뉴얼 html 생성기 — 같은 json 에서 브라우저로 보는 한 파일을 만듭니다.
//
//   node docs/manual/gen_manual_html.js                → 사용자 매뉴얼 (manual.json)
//   node docs/manual/gen_manual_html.js install.json   → 설치 가이드
//
// 그림은 base64 로 넣어 파일 하나로 보냅니다. 번호 · 링크 규칙은 lib/model.js (docx 와 같음).
// docx 가 정본이고, html 은 화면에서 읽기 · 링크 따라가기 · 영상 원고 작업용입니다.
const path = require("path");
const fs = require("fs");
const model = require("./lib/model");

const DIR = __dirname;
const SHOT = path.join(DIR, "screenshots");
const SRC = process.argv[2] || "manual.json";
const { manual, anchors, videos } = model.load(path.resolve(DIR, SRC));
const OUT = path.join(DIR, `BODA_VMS_MLOps_${manual.docName || "사용자매뉴얼"}_${manual.version}.html`);

const esc = (s) => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");

function inline(value) {
  return String(value).split("\n").map((line) => model.inline(line, anchors).map((t) => {
    const body = t.mono ? `<code>${esc(t.text)}</code>` : esc(t.text);
    if (!t.href) return body;
    const ext = !t.href.startsWith("#");
    return `<a href="${esc(t.href)}"${ext ? ' target="_blank" rel="noopener" class="ext"' : ' class="int"'}>${body}</a>`;
  }).join("")).join("<br>");
}

const badge = (id) => (id ? ` <span class="video" title="이 절을 따라 하는 영상">▶ 영상 ${esc(id)}</span>` : "");
let missing = 0;

function figure(b) {
  const file = path.join(SHOT, b.file + ".png");
  if (!fs.existsSync(file)) {
    missing++;
    return `<figure class="missing"><div>그림 준비 중: ${esc(b.file)}</div><figcaption>${inline(b.caption)}</figcaption></figure>`;
  }
  const data = fs.readFileSync(file).toString("base64");
  return `<figure><img src="data:image/png;base64,${data}" alt="${esc(b.file)}" loading="lazy"><figcaption>${inline(b.caption)}</figcaption></figure>`;
}

function render(b) {
  switch (b.type) {
    case "p": return `<p>${inline(b.text)}</p>`;
    case "h2": {
      const tag = b.level === 3 ? "h4" : "h3";
      return `<${tag}${b.id ? ` id="${b.id}"` : ""}>${esc(b.title)}${badge(b.video)}</${tag}>`;
    }
    case "note": return `<div class="box note"><b>알림</b> ${inline(b.text)}</div>`;
    case "warn": return `<div class="box warn"><b>주의</b> ${inline(b.text)}</div>`;
    case "steps": return `<ol class="steps">${b.items.map((i) => `<li>${inline(i)}</li>`).join("")}</ol>`;
    case "ul": return `<ul>${b.items.map((i) => `<li>${inline(i)}</li>`).join("")}</ul>`;
    case "table": return `<div class="tw"><table><thead><tr>${b.header.map((h) => `<th>${inline(h)}</th>`).join("")}</tr></thead><tbody>${
      b.rows.map((r) => `<tr>${r.map((c) => `<td>${inline(c)}</td>`).join("")}</tr>`).join("")}</tbody></table></div>`;
    case "figure": return figure(b);
    case "code": return `${b.caption ? `<div class="cap">${inline(b.caption)}</div>` : ""}<pre>${esc([].concat(b.text).join("\n"))}</pre>`;
    case "terms": return `<dl class="terms">${b.items.map((t) => `<dt id="${t.id}">${esc(t.term)}${t.en ? ` <span class="en">${esc(t.en)}</span>` : ""}</dt><dd><p>${inline(t.desc)}</p>${
      t.where ? `<p class="where">화면: ${inline(t.where)}</p>` : ""}${
      t.links && t.links.length ? `<p class="more">더 읽기: ${t.links.map((l) => `<a href="${esc(l.url)}" target="_blank" rel="noopener" class="ext">${esc(l.text)}</a>`).join(" · ")}</p>` : ""}</dd>`).join("")}</dl>`;
    default: throw new Error("모르는 블록: " + b.type);
  }
}

// 목차 — 장과 절(x.y)까지
const toc = [];
const body = [];
for (const s of manual.sections) {
  if (s.part) {
    toc.push(`<li class="part">${esc(s.part.title)}</li>`);
    body.push(`<section class="part"><h1>${esc(s.part.title)}</h1>${[].concat(s.part.intro || []).map((t) => `<p>${inline(t)}</p>`).join("")}</section>`);
  }
  const subs = s.blocks.filter((b) => b.type === "h2" && b.level !== 3 && b.id);
  toc.push(`<li><a href="#${s.id}">${esc(s.title)}</a>${subs.length ? `<ul>${subs.map((b) => `<li><a href="#${b.id}">${esc(b.title)}</a>${b.video ? ' <span class="v">▶</span>' : ""}</li>`).join("")}</ul>` : ""}</li>`);
  body.push(`<section><h2 id="${s.id}">${esc(s.title)}${badge(s.video)}</h2>${s.blocks.map(render).join("\n")}</section>`);
}

const html = `<!doctype html>
<html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>${esc(manual.title)} ${esc(manual.version)}</title>
<style>
:root{--bg:#fff;--panel:#f6f7f9;--ink:#16181d;--muted:#5f6670;--line:#dde1e6;--accent:#e8384a;--link:#1f6feb;--note:#1f6feb;--warn:#e8384a;--code:#f3f4f6}
@media (prefers-color-scheme:dark){:root:not([data-theme="light"]){--bg:#15171b;--panel:#1d2026;--ink:#e7e9ed;--muted:#9aa2ad;--line:#30353d;--accent:#ff6372;--link:#6aa6ff;--note:#6aa6ff;--warn:#ff6372;--code:#22262d}}
:root[data-theme="dark"]{--bg:#15171b;--panel:#1d2026;--ink:#e7e9ed;--muted:#9aa2ad;--line:#30353d;--accent:#ff6372;--link:#6aa6ff;--note:#6aa6ff;--warn:#ff6372;--code:#22262d}
*{box-sizing:border-box}html{scroll-padding-top:16px}
body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.7 "Malgun Gothic","맑은 고딕",system-ui,sans-serif}
nav{position:fixed;top:0;left:0;bottom:0;width:300px;overflow:auto;padding:20px 16px;background:var(--panel);border-right:1px solid var(--line);font-size:13px}
nav h1{font-size:15px;margin:0 0 4px}nav .ver{color:var(--muted);margin-bottom:12px}
nav ul{list-style:none;margin:0;padding:0}nav ul ul{padding-left:12px;margin:2px 0 6px}nav li{margin:2px 0}
nav li.part{margin-top:14px;font-weight:700;color:var(--accent)}nav a{color:var(--ink);text-decoration:none}nav a:hover{color:var(--link)}nav .v{color:var(--accent);font-size:10px}
main{margin-left:300px;padding:24px 40px 80px;max-width:1040px}
header.cover{padding:40px 0 24px;border-bottom:1px solid var(--line);margin-bottom:24px}header.cover h1{font-size:30px;margin:0 0 8px}header.cover p{color:var(--muted);margin:0}
section.part{margin:56px 0 0;padding:28px 0;border-top:3px solid var(--accent)}section.part h1{color:var(--accent);margin:0 0 8px;font-size:26px}section.part p{color:var(--muted)}
h2{font-size:23px;margin:48px 0 12px;padding-bottom:6px;border-bottom:1px solid var(--line)}h3{font-size:18px;margin:32px 0 8px}h4{font-size:16px;margin:24px 0 6px}
.video{display:inline-block;font-size:11px;font-weight:700;color:var(--accent);border:1px solid var(--accent);border-radius:10px;padding:0 8px;vertical-align:middle;margin-left:6px}
a{color:var(--link)}a.int{text-decoration:underline dotted}code{font-family:Consolas,monospace;background:var(--code);padding:0 4px;border-radius:3px;font-size:.92em}
.tw{overflow-x:auto;margin:12px 0}table{border-collapse:collapse;width:100%;font-size:14px}th,td{border:1px solid var(--line);padding:6px 10px;text-align:left;vertical-align:top}th{background:var(--panel)}
.box{margin:12px 0;padding:10px 14px;background:var(--panel);border-left:4px solid var(--note);border-radius:2px}.box.warn{border-left-color:var(--warn)}.box b{color:var(--note);margin-right:6px}.box.warn b{color:var(--warn)}
ol.steps{padding-left:24px}ol.steps li{margin:4px 0}ol.steps li::marker{color:var(--accent);font-weight:700}
figure{margin:16px 0;text-align:center}figure img{max-width:100%;border:1px solid var(--line);border-radius:4px}figcaption{color:var(--muted);font-size:13px;margin-top:4px}
figure.missing div{padding:40px;border:2px dashed var(--line);color:var(--muted)}
pre{background:var(--code);padding:12px;overflow-x:auto;border-radius:4px;font:13px/1.5 Consolas,monospace}.cap{color:var(--muted);font-size:13px;margin-top:12px}
dl.terms dt{font-weight:700;margin-top:18px;font-size:16px}dl.terms dt .en{font-weight:400;color:var(--muted);font-size:13px}dl.terms dd{margin:2px 0 0 16px}dl.terms dd p{margin:2px 0}dl.terms .where,dl.terms .more{font-size:13px;color:var(--muted)}
:target{animation:hl 2s}@keyframes hl{from{background:rgba(232,56,74,.18)}to{background:transparent}}
@media (max-width:900px){nav{position:static;width:auto;max-height:none;border-right:0;border-bottom:1px solid var(--line)}main{margin:0;padding:16px}}
</style></head><body>
<nav><h1>${esc(manual.title)}</h1><div class="ver">${esc(manual.version)} · 영상 ${videos.length}편</div><ul>${toc.join("")}</ul></nav>
<main><header class="cover"><h1>${esc(manual.title)}</h1><p>${esc(manual.subtitle)}</p><p>${esc(manual.version)} · ${new Date().toISOString().slice(0, 10)} · 주식회사 바심</p></header>
${body.join("\n")}
</main></body></html>`;

fs.writeFileSync(OUT, html);
console.log(`${path.basename(OUT)}  (${(Buffer.byteLength(html) / 1024 / 1024).toFixed(1)} MB) · 그림 없음 ${missing}장`);
