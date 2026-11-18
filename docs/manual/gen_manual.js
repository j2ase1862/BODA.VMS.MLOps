// BODA VMS MLOps 문서 docx 생성기
//
//   node docs/manual/gen_manual.js                → 사용자 매뉴얼 (manual.json)
//   node docs/manual/gen_manual.js install.json   → 설치 가이드 (install.json)
//
// 입력: 본문 json + screenshots/ (capture.js 가 만든 그림)
// docx 를 직접 편집하지 마세요 — 다시 만들면 사라집니다. 본문은 json 을 고칩니다.
//
// 독자가 다릅니다. manual.json 은 라벨링·학습·승격을 하는 사용자,
// install.json 은 서버와 워커를 세우는 관리자입니다. 개정 주기도 반출 단위도 달라 파일을 나눴습니다.
const path = require("path");
const fs = require("fs");

const GLOBAL = "C:/Users/vinos/AppData/Roaming/npm/node_modules";
const {
  Document, Packer, Paragraph, TextRun, Table, TableRow, TableCell,
  AlignmentType, HeadingLevel, BorderStyle, WidthType, ShadingType,
  VerticalAlign, PageNumber, Header, Footer, ImageRun, TableOfContents,
  ExternalHyperlink, InternalHyperlink, Bookmark,
} = require(path.join(GLOBAL, "docx"));
const model = require("./lib/model");

const DIR = __dirname;
const SHOT = path.join(DIR, "screenshots");
const SRC = process.argv[2] || "manual.json";
// 번호 · 책갈피 · 인라인 링크는 lib/model.js 가 풉니다 (html 생성기와 같은 규칙)
const { manual, anchors, videos } = model.load(path.resolve(DIR, SRC));
const linkStats = model.check({ manual, anchors });
const OUT = path.join(DIR, `BODA_VMS_MLOps_${manual.docName || "사용자매뉴얼"}_${manual.version}.docx`);

// A4 세로, 1인치 여백
const PAGE_W = 11906, MARGIN = 1440;
const CONTENT_W = PAGE_W - 2 * MARGIN;          // 9026 twip
const CONTENT_PX = Math.round(CONTENT_W / 15);  // 96dpi 기준 약 602px

const FONT = "맑은 고딕";
// 받는 PC 에 반드시 있는 고정폭이라야 합니다. D2Coding 은 따로 설치해야 하고,
// 없으면 Word 가 비례 글꼴로 바꿔 명령이 한 줄에 안 맞습니다.
const MONO = "Consolas";
const INK = "101010";
const MUTED = "5F5F5F";
const ACCENT = "FF4052";
const LINK = "1F6FEB";

// Word 책갈피 이름은 글자 · 숫자 · 밑줄만, 40자까지입니다 — 문서 id 의 하이픈을 밑줄로 바꿉니다
const bm = (id) => id.replace(/-/g, "_").slice(0, 40);

const thin = { style: BorderStyle.SINGLE, size: 1, color: "D0D0D0" };
const borders = { top: thin, bottom: thin, left: thin, right: thin };
const cellMargins = { top: 60, bottom: 60, left: 120, right: 120 };

function text(value, opts = {}) {
  // 줄바꿈을 TextRun 의 break 로 옮기고, `…` 로 감싼 토막은 고정폭으로 바꾼다.
  // 설치 문서는 문장 안에 경로·옵션·서비스 이름이 섞이는데, 본문 글꼴로 쓰면 어디까지가 값인지 안 보인다.
  // [글](#id) · [글](https://…) · [[id]] 는 링크가 된다 (lib/model.js).
  const runs = [];
  String(value).split("\n").forEach((line, i) => {
    model.inline(line, anchors).forEach((tok, j) => {
      const run = (extra = {}) => new TextRun({
        text: tok.text, font: tok.mono ? MONO : FONT, size: 20, color: INK,
        break: i && j === 0 ? 1 : 0, ...opts, ...extra,
      });
      if (!tok.href) runs.push(run());
      else if (tok.href.startsWith("#")) runs.push(new InternalHyperlink({ anchor: bm(tok.href.slice(1)), children: [run({ color: LINK, underline: {} })] }));
      else runs.push(new ExternalHyperlink({ link: tok.href, children: [run({ color: LINK, underline: {} })] }));
    });
  });
  return runs;
}

/** 제목 글자 — id 가 있으면 책갈피로 감싸 다른 곳에서 링크로 올 수 있게 한다 */
function titleRuns(id, value, opts) {
  const runs = [new TextRun({ text: value, font: FONT, color: INK, ...opts })];
  return id ? [new Bookmark({ id: bm(id), children: runs })] : runs;
}

/** 영상 배지 — 이 절을 따라 하는 영상 번호. 영상 주소는 영상을 올린 뒤 따로 알립니다 */
function videoBadge(id) {
  return new TextRun({ text: `   ▶ 영상 ${id}`, font: FONT, size: 17, bold: true, color: ACCENT });
}

function para(value, opts = {}) {
  const { spacing, ...runOpts } = opts;
  return new Paragraph({
    children: text(value, runOpts),
    spacing: { after: 120, line: 300, ...spacing },
  });
}

/** PNG 헤더에서 크기를 읽는다 — 비율을 지켜 넣기 위해서다. */
function pngSize(file) {
  const buf = fs.readFileSync(file);
  if (buf.length < 24 || buf.readUInt32BE(0) !== 0x89504e47) throw new Error("PNG 가 아닙니다: " + file);
  return { width: buf.readUInt32BE(16), height: buf.readUInt32BE(20) };
}

function figure(block) {
  const file = path.join(SHOT, block.file + ".png");
  if (!fs.existsSync(file)) {
    console.log(`  (그림 없음: ${block.file}.png — 자리만 비웁니다)`);
    return [para(`[그림 없음: ${block.file}]`, { color: MUTED, italics: true })];
  }
  const { width, height } = pngSize(file);
  const w = CONTENT_PX;
  const h = Math.round((height / width) * w);

  return [
    new Paragraph({
      // type 을 주지 않으면 docx 8 이상이 media 를 .undefined 로 넣어 Word 가 파일을 못 연다
      children: [new ImageRun({ type: "png", data: fs.readFileSync(file), transformation: { width: w, height: h } })],
      alignment: AlignmentType.CENTER,
      spacing: { before: 160, after: 60 },
    }),
    new Paragraph({
      children: text(block.caption, { size: 17, color: MUTED }),
      alignment: AlignmentType.CENTER,
      spacing: { after: 200 },
    }),
  ];
}

/** 알림·주의 상자 — 왼쪽에 색 띠를 둔 한 칸 표로 만든다. */
function callout(label, value, color) {
  return new Table({
    width: { size: CONTENT_W, type: WidthType.DXA },
    borders: {
      top: { style: BorderStyle.NONE }, bottom: { style: BorderStyle.NONE },
      right: { style: BorderStyle.NONE },
      left: { style: BorderStyle.SINGLE, size: 18, color },
      insideHorizontal: { style: BorderStyle.NONE }, insideVertical: { style: BorderStyle.NONE },
    },
    rows: [new TableRow({
      children: [new TableCell({
        margins: { top: 100, bottom: 100, left: 180, right: 120 },
        shading: { type: ShadingType.CLEAR, fill: "F7F7F7" },
        children: [new Paragraph({
          children: [
            new TextRun({ text: label + "  ", font: FONT, size: 20, bold: true, color }),
            ...text(value, { size: 20 }),
          ],
          spacing: { line: 300 },
        })],
      })],
    })],
    margins: cellMargins,
  });
}

function table(block) {
  const cols = block.header.length;
  const width = Math.floor(CONTENT_W / cols);

  const head = new TableRow({
    tableHeader: true,
    children: block.header.map((h) => new TableCell({
      width: { size: width, type: WidthType.DXA },
      shading: { type: ShadingType.CLEAR, fill: "F2F2F2" },
      margins: cellMargins,
      verticalAlign: VerticalAlign.CENTER,
      children: [new Paragraph({ children: text(h, { bold: true, size: 19 }) })],
    })),
  });

  const body = block.rows.map((row) => new TableRow({
    children: row.map((cell) => new TableCell({
      width: { size: width, type: WidthType.DXA },
      margins: cellMargins,
      verticalAlign: VerticalAlign.TOP,
      children: [new Paragraph({ children: text(cell, { size: 19 }), spacing: { line: 280 } })],
    })),
  }));

  return [
    new Table({ width: { size: CONTENT_W, type: WidthType.DXA }, borders, rows: [head, ...body] }),
    new Paragraph({ text: "", spacing: { after: 200 } }),
  ];
}

/** 명령·설정·로그 출력 — 한 칸 표에 고정폭으로 넣는다. 줄을 문단마다 끊어 긴 줄이 잘리지 않게 한다. */
function code(block) {
  const out = [];
  if (block.caption) {
    out.push(new Paragraph({
      children: text(block.caption, { size: 17, color: MUTED }),
      spacing: { before: 140, after: 40 },
    }));
  }
  out.push(new Table({
    width: { size: CONTENT_W, type: WidthType.DXA },
    borders: {
      top: thin, bottom: thin, left: thin, right: thin,
      insideHorizontal: { style: BorderStyle.NONE }, insideVertical: { style: BorderStyle.NONE },
    },
    rows: [new TableRow({
      children: [new TableCell({
        margins: { top: 120, bottom: 120, left: 160, right: 160 },
        shading: { type: ShadingType.CLEAR, fill: "F7F7F7" },
        // 줄을 배열로 써도 된다 — json 에서 \n 을 이어 붙이는 것보다 고치기 쉽다
        children: (Array.isArray(block.text) ? block.text : String(block.text).split("\n")).map((line) => new Paragraph({
          children: [new TextRun({ text: line, font: MONO, size: 17, color: INK })],
          spacing: { line: 260 },
        })),
      })],
    })],
  }));
  out.push(new Paragraph({ text: "", spacing: { after: 200 } }));
  return out;
}

/** 용어집 — 용어마다 책갈피를 달아 본문에서 링크로 온다. 쉬운 뜻 · 화면 어디 · 더 읽기 순서 */
function terms(block) {
  const out = [];
  for (const t of block.items) {
    out.push(new Paragraph({
      children: [
        ...titleRuns(t.id, t.term, { bold: true, size: 21 }),
        ...(t.en ? [new TextRun({ text: `  ${t.en}`, font: FONT, size: 18, color: MUTED })] : []),
      ],
      keepNext: true,
      spacing: { before: 160, after: 40 },
    }));
    out.push(new Paragraph({ children: text(t.desc, { size: 20 }), indent: { left: 240 }, spacing: { after: 40, line: 290 } }));
    const tail = [];
    if (t.where) tail.push(...text("화면: " + t.where, { size: 18, color: MUTED }));
    (t.links || []).forEach((l, i) => {
      const sep = i ? " · " : (tail.length ? "   더 읽기: " : "더 읽기: ");
      tail.push(new TextRun({ text: sep, font: FONT, size: 18, color: MUTED }));
      tail.push(new ExternalHyperlink({ link: l.url, children: [new TextRun({ text: l.text, font: FONT, size: 18, color: LINK, underline: {} })] }));
    });
    if (tail.length) out.push(new Paragraph({ children: tail, indent: { left: 240 }, spacing: { after: 60 } }));
  }
  return out;
}

function steps(items) {
  return items.map((item, i) => new Paragraph({
    children: [
      new TextRun({ text: `${i + 1}. `, font: FONT, size: 20, bold: true, color: ACCENT }),
      ...text(item, { size: 20 }),
    ],
    indent: { left: 360, hanging: 260 },
    spacing: { after: 80, line: 300 },
  }));
}

function render(block) {
  switch (block.type) {
    case "p": return [para(block.text)];
    // 서식 수준을 줘야 목차에 소제목까지 올라온다. x.y.z(level 3)는 목차에 넣지 않는다
    case "h2": return [new Paragraph({
      children: [
        ...titleRuns(block.id, block.title, { bold: true, size: block.level === 3 ? 22 : 24 }),
        ...(block.video ? [videoBadge(block.video)] : []),
      ],
      heading: block.level === 3 ? HeadingLevel.HEADING_3 : HeadingLevel.HEADING_2,
      spacing: { before: 280, after: 140 },
    })];
    case "ul": return block.items.map((item) => new Paragraph({
      children: [new TextRun({ text: "•  ", font: FONT, size: 20, color: MUTED }), ...text(item, { size: 20 })],
      indent: { left: 360, hanging: 220 },
      spacing: { after: 60, line: 300 },
    }));
    case "terms": return terms(block);
    case "note": return [callout("알림", block.text, "1F6FEB"), new Paragraph({ text: "", spacing: { after: 160 } })];
    case "warn": return [callout("주의", block.text, ACCENT), new Paragraph({ text: "", spacing: { after: 160 } })];
    case "steps": return steps(block.items);
    case "table": return table(block);
    case "figure": return figure(block);
    case "code": return code(block);
    default: throw new Error("모르는 블록: " + block.type);
  }
}

// ── 표지 ──
const cover = [
  new Paragraph({ text: "", spacing: { before: 2600 } }),
  new Paragraph({
    children: [new TextRun({ text: manual.title, font: FONT, size: 52, bold: true, color: INK })],
    spacing: { after: 200 },
  }),
  new Paragraph({
    children: [new TextRun({ text: manual.subtitle, font: FONT, size: 24, color: MUTED })],
    spacing: { after: 900 },
  }),
  new Paragraph({
    children: [new TextRun({ text: manual.version, font: FONT, size: 22, bold: true, color: ACCENT })],
    spacing: { after: 60 },
  }),
  new Paragraph({
    children: [new TextRun({
      text: new Date().toISOString().slice(0, 10) + " · 주식회사 바심",
      font: FONT, size: 20, color: MUTED,
    })],
    pageBreakBefore: false,
  }),
];

// ── 목차 ──
const toc = [
  new Paragraph({
    children: text("목차", { bold: true, size: 32 }),
    pageBreakBefore: true,
    spacing: { after: 240 },
  }),
  new TableOfContents("목차", { hyperlink: true, headingStyleRange: "1-2" }),
];

// ── 본문 ──
const body = [];
for (const section of manual.sections) {
  // 부 표지 — 큰 제목과 안내 한두 문단만 둔 쪽. 목차에는 넣지 않는다
  if (section.part) {
    body.push(new Paragraph({ text: "", pageBreakBefore: true, spacing: { before: 2400 } }));
    body.push(new Paragraph({
      children: [new TextRun({ text: section.part.title, font: FONT, size: 44, bold: true, color: ACCENT })],
      spacing: { after: 300 },
    }));
    for (const t of [].concat(section.part.intro || [])) body.push(para(t, { size: 22, color: MUTED }));
  }
  body.push(new Paragraph({
    children: [
      ...titleRuns(section.id, section.title, { bold: true, size: 32 }),
      ...(section.video ? [videoBadge(section.video)] : []),
    ],
    heading: HeadingLevel.HEADING_1,
    pageBreakBefore: true,
    spacing: { after: 200 },
  }));
  for (const block of section.blocks) body.push(...render(block));
}

const doc = new Document({
  creator: "BODA VMS MLOps",
  title: manual.title,
  description: manual.subtitle,
  styles: {
    default: { document: { run: { font: FONT, size: 20, color: INK } } },
    paragraphStyles: [
      { id: "Heading1", name: "Heading 1", basedOn: "Normal", next: "Normal", quickFormat: true,
        run: { font: FONT, size: 32, bold: true, color: INK } },
      { id: "Heading2", name: "Heading 2", basedOn: "Normal", next: "Normal", quickFormat: true,
        run: { font: FONT, size: 24, bold: true, color: INK } },
      { id: "Heading3", name: "Heading 3", basedOn: "Normal", next: "Normal", quickFormat: true,
        run: { font: FONT, size: 22, bold: true, color: INK } },
    ],
  },
  sections: [{
    properties: { page: { margin: { top: MARGIN, bottom: MARGIN, left: MARGIN, right: MARGIN } } },
    headers: {
      default: new Header({
        children: [new Paragraph({
          children: [new TextRun({ text: manual.title, font: FONT, size: 16, color: MUTED })],
          alignment: AlignmentType.RIGHT,
        })],
      }),
    },
    footers: {
      default: new Footer({
        children: [new Paragraph({
          children: [new TextRun({ children: [PageNumber.CURRENT], font: FONT, size: 16, color: MUTED })],
          alignment: AlignmentType.CENTER,
        })],
      }),
    },
    children: [...cover, ...toc, ...body],
  }],
});

Packer.toBuffer(doc).then((buf) => {
  fs.writeFileSync(OUT, buf);
  console.log(`${path.basename(OUT)}  (${(buf.length / 1024).toFixed(0)} KB)`);
  const figures = manual.sections.reduce((n, s) => n + s.blocks.filter(b => b.type === "figure").length, 0);
  console.log(`${SRC} · 장 ${manual.sections.length}개 · 그림 ${figures}장 · 영상 배지 ${videos.length}개 · 링크 ${linkStats.links}개(바깥 ${linkStats.ext})`);
});
