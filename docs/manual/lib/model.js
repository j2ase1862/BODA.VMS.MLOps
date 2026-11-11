// 매뉴얼 json 을 읽어 번호 · 책갈피 · 본문 속 링크를 풀어 두는 곳.
// docx 생성기(gen_manual.js)와 html 생성기(gen_manual_html.js)가 같이 씁니다 — 두 출력의 번호와 링크가 어긋나지 않게.
//
// json 에 "autoNumber": true 가 있으면 장 · 절 번호를 여기서 붙입니다. 그때 heading · h2 의 text 에는 번호를 쓰지 않습니다.
//   장(section)   : { heading, id?, video?, part?, appendix?, blocks }
//                   part     — 이 장 앞에 부(部) 표지를 넣습니다. { title, intro? }
//                   appendix — 부록. 번호 대신 A · B · C … 가 붙습니다.
//                   front    — 머리말. 번호를 붙이지 않고 장 번호도 세지 않습니다.
//   절(h2 블록)    : { type: "h2", text, id?, video?, level? }   level 3 이면 x.y.z
//
// 본문 어디서나 쓸 수 있는 인라인 표기:
//   `…`              고정폭 (경로 · 옵션 · 값)
//   [보이는 글](#id)  이 문서 안의 책갈피로 — 용어(g-…)나 절 id
//   [보이는 글](https://…)  바깥 링크
//   [[id]]           그 절 · 장의 번호로 바뀝니다 (예: [[s-roi]] → 9.4)
// 화면의 버튼 이름 [저장하고 다음] 처럼 대괄호 뒤에 (#… 나 (http 가 바로 붙지 않으면 그냥 글자입니다.
const fs = require("fs");
const path = require("path");

const LETTERS = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

function load(file) {
  const manual = JSON.parse(fs.readFileSync(file, "utf-8"));
  const anchors = new Map();   // id → { number, title }
  const videos = [];           // { id, number, title }
  let chapter = 0, appendix = 0;

  const add = (id, info) => {
    if (!id) return;
    if (anchors.has(id)) throw new Error("책갈피 id 가 겹칩니다: " + id);
    anchors.set(id, info);
  };

  for (const s of manual.sections) {
    if (manual.autoNumber && s.front) {
      s.number = null; s.title = s.heading;   // 머리말 — 번호 없이
    } else if (manual.autoNumber) {
      s.number = s.appendix ? LETTERS[appendix++] : String(++chapter);
      s.title = s.appendix ? `부록 ${s.number}. ${s.heading}` : `${s.number}. ${s.heading}`;
    } else {
      s.title = s.heading;
    }
    add(s.id, { number: s.appendix ? `부록 ${s.number}` : s.number, title: s.title });
    if (s.video) videos.push({ id: s.video, number: s.number, title: s.heading });

    let h2 = 0, h3 = 0;
    for (const b of s.blocks) {
      if (b.type === "h2") {
        if (manual.autoNumber) {
          if (!s.number) b.number = null;
          else if (b.level === 3) b.number = `${s.number}.${h2}.${++h3}`;
          else { b.number = `${s.number}.${++h2}`; h3 = 0; }
          b.title = b.number ? `${b.number} ${b.text}` : b.text;
        } else {
          b.title = b.text;
        }
        add(b.id, { number: b.number, title: b.title });
        if (b.video) videos.push({ id: b.video, number: b.number, title: b.text });
      }
      if (b.type === "terms") for (const t of b.items) add(t.id, { number: null, title: t.term });
    }
  }
  return { manual, anchors, videos };
}

/** 인라인 표기를 토막으로 나눈다. 토막: { text, mono?, href?, ref? } — href 는 "#id" 또는 "https://…" */
function inline(value, anchors) {
  const out = [];
  String(value).split("`").forEach((seg, i) => {
    if (i % 2) { out.push({ text: seg, mono: true }); return; }
    const re = /\[\[([a-z0-9-]+)\]\]|\[([^\]]+)\]\((#[a-z0-9-]+|https?:\/\/[^\s)]+)\)/g;
    let last = 0, m;
    while ((m = re.exec(seg))) {
      if (m.index > last) out.push({ text: seg.slice(last, m.index) });
      if (m[1]) {
        const a = anchors.get(m[1]);
        if (!a || !a.number) throw new Error("번호를 찾을 수 없는 참조: [[" + m[1] + "]]");
        out.push({ text: a.number, href: "#" + m[1] });
      } else {
        if (m[3].startsWith("#") && !anchors.has(m[3].slice(1))) throw new Error("없는 책갈피로 가는 링크: " + m[0]);
        out.push({ text: m[2], href: m[3] });
      }
      last = re.lastIndex;
    }
    if (last < seg.length) out.push({ text: seg.slice(last) });
  });
  return out;
}

/** 블록 안의 글 전부 — 링크 검사와 통계에 쓴다 */
function texts(b) {
  switch (b.type) {
    case "p": case "note": case "warn": return [b.text];
    case "h2": return [b.text];
    case "steps": case "ul": return b.items;
    case "table": return [...b.header, ...b.rows.flat()];
    case "figure": return [b.caption];
    case "terms": return b.items.flatMap((t) => [t.desc, t.where || ""]);
    default: return [];
  }
}

/** 모든 글을 한 번씩 풀어 깨진 링크를 미리 잡는다 */
function check(model) {
  const { manual, anchors } = model;
  let links = 0, ext = 0;
  for (const s of manual.sections) for (const b of s.blocks) for (const t of texts(b)) {
    for (const tok of inline(t, anchors)) if (tok.href) { links++; if (!tok.href.startsWith("#")) ext++; }
  }
  return { links, ext };
}

module.exports = { load, inline, texts, check, ROOT: path.resolve(__dirname, "..") };
