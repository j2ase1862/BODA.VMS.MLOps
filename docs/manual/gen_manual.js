// BODA VMS MLOps 사용자 매뉴얼 docx 생성기
//
//   node docs/manual/gen_manual.js
//   → docs/manual/BODA_VMS_MLOps_사용자매뉴얼_v1.0.docx
//
// 입력: manual.json (본문) + screenshots/ (capture.js 가 만든 그림)
// docx 를 직접 편집하지 마세요 — 다시 만들면 사라집니다. 본문은 manual.json 을 고칩니다.
const path = require("path");
const fs = require("fs");

const GLOBAL = "C:/Users/vinos/AppData/Roaming/npm/node_modules";
const {
  Document, Packer, Paragraph, TextRun, Table, TableRow, TableCell,
  AlignmentType, HeadingLevel, BorderStyle, WidthType, ShadingType,
  VerticalAlign, PageNumber, Header, Footer, ImageRun, TableOfContents,
} = require(path.join(GLOBAL, "docx"));

const DIR = __dirname;
const SHOT = path.join(DIR, "screenshots");
const manual = JSON.parse(fs.readFileSync(path.join(DIR, "manual.json"), "utf-8"));
const OUT = path.join(DIR, `BODA_VMS_MLOps_사용자매뉴얼_${manual.version}.docx`);

// A4 세로, 1인치 여백
const PAGE_W = 11906, MARGIN = 1440;
const CONTENT_W = PAGE_W - 2 * MARGIN;          // 9026 twip
const CONTENT_PX = Math.round(CONTENT_W / 15);  // 96dpi 기준 약 602px

const FONT = "맑은 고딕";
const MONO = "D2Coding";
const INK = "101010";
const MUTED = "5F5F5F";
const ACCENT = "FF4052";

const thin = { style: BorderStyle.SINGLE, size: 1, color: "D0D0D0" };
const borders = { top: thin, bottom: thin, left: thin, right: thin };
const cellMargins = { top: 60, bottom: 60, left: 120, right: 120 };

function text(value, opts = {}) {
  // 줄바꿈을 TextRun 의 break 로 옮긴다
  return String(value).split("\n").map((line, i) =>
    new TextRun({ text: line, font: FONT, size: 20, color: INK, break: i ? 1 : 0, ...opts }));
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
    // 서식 수준을 줘야 목차에 소제목까지 올라온다
    case "h2": return [new Paragraph({
      children: text(block.text, { bold: true, size: 24 }),
      heading: HeadingLevel.HEADING_2,
      spacing: { before: 280, after: 140 },
    })];
    case "note": return [callout("알림", block.text, "1F6FEB"), new Paragraph({ text: "", spacing: { after: 160 } })];
    case "warn": return [callout("주의", block.text, ACCENT), new Paragraph({ text: "", spacing: { after: 160 } })];
    case "steps": return steps(block.items);
    case "table": return table(block);
    case "figure": return figure(block);
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
  body.push(new Paragraph({
    children: text(section.heading, { bold: true, size: 32 }),
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
  console.log(`장 ${manual.sections.length}개 · 그림 ${fs.readdirSync(SHOT).filter(f => f.endsWith(".png")).length}장`);
});
