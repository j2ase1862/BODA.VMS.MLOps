/**
 * 라벨링 캔버스 (개발 문서 §5.4).
 *
 * Blazor 는 캔버스 상호작용에 불리해 이 부분만 순수 JS 로 두고 JS interop 으로 감싼다.
 * TypeScript 대신 ES 모듈로 쓴 이유는 .NET 빌드에 npm·tsc 단계를 더하지 않기 위해서다.
 * 커지면 그때 빌드 단계를 붙이면 된다.
 *
 * 좌표는 안에서도 밖에서도 0~1 정규화다. 원본 해상도가 달라도 라벨이 그대로 유효하고,
 * 축소본으로 그리다 원본으로 바꿔도 값이 변하지 않는다.
 *
 * 도형: box · polygon · classification · text
 * 모드: 위 도형들 + sam (클릭하면 서버가 그 객체의 폴리곤을 만들어 준다)
 */

const HANDLE_SIZE = 8;          // 화면 픽셀 기준 조절점 크기
const HIT_SLOP = 6;             // 선을 집을 때 허용 오차
const MIN_BOX = 0.002;          // 이보다 작은 사각형은 실수로 본다

// 라벨 색. 화면 강조색(#FF4052)과 겹치지 않게 파랑부터 시작한다 —
// 빨강이 앞에 있으면 "지금 고른 UI 요소" 와 "라벨" 이 같은 색이 되어 눈으로 갈라지지 않는다.
// 순서는 Labeling.razor·DatasetDetail.razor 의 ColorOf 와 같아야 한다.
const PALETTE = [
    '#1e88e5', '#43a047', '#fb8c00', '#8e24aa', '#00acc1',
    '#d81b60', '#6d4c41', '#3949ab', '#c62828', '#00897b',
];

/** 활성 인스턴스. Blazor 가 element 대신 id 로 부르기 때문에 여기 담아 둔다. */
const instances = new Map();

class LabelCanvas {
    constructor(canvas, dotNet, options) {
        this.canvas = canvas;
        this.dotNet = dotNet;
        this.mode = options.mode || 'box';       // box | polygon | classification | text | sam
        this.classes = options.classes || [];
        this.readOnly = !!options.readOnly;
        // SAM 이 만들어 준 도형을 폴리곤으로 붙일지 박스로 붙일지 — 데이터셋이 허용하는 도형에 맞춘다
        this.samShape = options.samShape || 'polygon';

        this.image = null;
        this.annotations = [];
        this.selected = -1;
        this.activeClass = this.classes[0] || '';

        // SAM 보조: 확정 전까지는 라벨이 아니라 '미리보기'다. 확정해야 annotations 로 들어간다.
        this.samPoints = [];        // [{x, y, foreground}]
        this.samCandidates = [];    // 크기가 다른 해석들 — 작은 것부터
        this.samIndex = 0;          // 지금 보고 있는 후보
        this.samPending = false;
        this.samSeq = 0;
        this.samMessage = null;

        // 화면 변환 (이미지 정규화 좌표 → 캔버스 픽셀)
        this.scale = 1;
        this.offsetX = 0;
        this.offsetY = 0;

        this.drag = null;        // 진행 중인 조작
        this.draftPolygon = null;
        this.hoverIndex = -1;

        this._onPointerDown = this.onPointerDown.bind(this);
        this._onPointerMove = this.onPointerMove.bind(this);
        this._onPointerUp = this.onPointerUp.bind(this);
        this._onWheel = this.onWheel.bind(this);
        this._onKeyDown = this.onKeyDown.bind(this);
        this._onResize = () => this.resize();

        canvas.addEventListener('pointerdown', this._onPointerDown);
        canvas.addEventListener('pointermove', this._onPointerMove);
        window.addEventListener('pointerup', this._onPointerUp);
        canvas.addEventListener('wheel', this._onWheel, { passive: false });
        window.addEventListener('keydown', this._onKeyDown);
        window.addEventListener('resize', this._onResize);
        canvas.addEventListener('contextmenu', e => e.preventDefault());
        canvas.tabIndex = 0;

        this.resize();
    }

    dispose() {
        this.canvas.removeEventListener('pointerdown', this._onPointerDown);
        this.canvas.removeEventListener('pointermove', this._onPointerMove);
        window.removeEventListener('pointerup', this._onPointerUp);
        this.canvas.removeEventListener('wheel', this._onWheel);
        window.removeEventListener('keydown', this._onKeyDown);
        window.removeEventListener('resize', this._onResize);
    }

    // ───────────── 이미지·라벨 ─────────────

    async loadImage(url) {
        const image = new Image();
        image.decoding = 'async';
        await new Promise((resolve, reject) => {
            image.onload = resolve;
            image.onerror = () => reject(new Error('이미지를 불러오지 못했습니다.'));
            image.src = url;
        });
        this.image = image;
        this.fit();
        return { width: image.naturalWidth, height: image.naturalHeight };
    }

    setAnnotations(annotations) {
        this.annotations = (annotations || []).map(a => ({ ...a }));
        this.selected = -1;
        this.draftPolygon = null;
        // 이미지를 갈아 끼울 때도 이 경로를 지난다. 이전 사진의 SAM 미리보기가 남으면 엉뚱한 곳에 확정된다.
        this.clearSam(false);
        this.draw();
    }

    getAnnotations() {
        return this.annotations.map(a => ({ ...a }));
    }

    setMode(mode) {
        this.mode = mode;
        this.draftPolygon = null;
        this.clearSam(false);
        this.draw();
    }

    setSamShape(shape) {
        this.samShape = shape === 'box' ? 'box' : 'polygon';
    }

    /**
     * 다음에 그릴 도형의 클래스만 바꾼다. 이미 있는 라벨은 건드리지 않는다.
     * 예전에는 선택된 라벨의 클래스까지 함께 바꿨는데, 방금 그린 박스가 선택된 채로 남아 있어
     * 다음 클래스를 고르는 순간 먼저 그린 박스가 조용히 바뀌었다. 오라벨을 만드는 함정이라 분리했다.
     * 이미 붙은 라벨의 클래스는 setAnnotationClass 로만 바꾼다.
     */
    setActiveClass(className) {
        this.activeClass = className;
        this.draw();
    }

    /** 이미 붙은 라벨의 클래스를 바꾼다 (목록에서 명시적으로 고를 때만) */
    setAnnotationClass(index, className) {
        const a = this.annotations[index];
        if (!a) return;
        a.className = className;
        this.notifyChanged();
        this.draw();
    }

    setReadOnly(readOnly) {
        this.readOnly = !!readOnly;
        this.draw();
    }

    // ───────────── 보기 ─────────────

    resize() {
        const rect = this.canvas.getBoundingClientRect();
        const dpr = window.devicePixelRatio || 1;
        this.canvas.width = Math.max(1, Math.round(rect.width * dpr));
        this.canvas.height = Math.max(1, Math.round(rect.height * dpr));
        this.dpr = dpr;
        this.viewWidth = rect.width;
        this.viewHeight = rect.height;
        this.draw();
    }

    /** 이미지 전체가 보이도록 맞춘다 */
    fit() {
        if (!this.image) return;
        const scaleX = this.viewWidth / this.image.naturalWidth;
        const scaleY = this.viewHeight / this.image.naturalHeight;
        this.scale = Math.min(scaleX, scaleY) * 0.98;
        this.offsetX = (this.viewWidth - this.image.naturalWidth * this.scale) / 2;
        this.offsetY = (this.viewHeight - this.image.naturalHeight * this.scale) / 2;
        this.draw();
    }

    zoomBy(factor, centerX, centerY) {
        if (!this.image) return;
        const cx = centerX ?? this.viewWidth / 2;
        const cy = centerY ?? this.viewHeight / 2;
        const before = this.toImage(cx, cy);
        this.scale = Math.min(40, Math.max(0.02, this.scale * factor));
        const after = this.toImage(cx, cy);
        // 확대 중심이 화면에서 움직이지 않도록 보정한다
        this.offsetX += (after.x - before.x) * this.image.naturalWidth * this.scale;
        this.offsetY += (after.y - before.y) * this.image.naturalHeight * this.scale;
        this.draw();
    }

    /** 캔버스 픽셀 → 0~1 정규화 이미지 좌표 */
    toImage(px, py) {
        if (!this.image) return { x: 0, y: 0 };
        return {
            x: (px - this.offsetX) / (this.image.naturalWidth * this.scale),
            y: (py - this.offsetY) / (this.image.naturalHeight * this.scale),
        };
    }

    /** 0~1 정규화 → 캔버스 픽셀 */
    toCanvas(nx, ny) {
        if (!this.image) return { x: 0, y: 0 };
        return {
            x: this.offsetX + nx * this.image.naturalWidth * this.scale,
            y: this.offsetY + ny * this.image.naturalHeight * this.scale,
        };
    }

    eventPos(e) {
        const rect = this.canvas.getBoundingClientRect();
        return { x: e.clientX - rect.left, y: e.clientY - rect.top };
    }

    // ───────────── 입력 ─────────────

    onPointerDown(e) {
        this.canvas.focus();
        const pos = this.eventPos(e);

        // 가운데 버튼이나 공백 키 조합은 항상 이동
        if (e.button === 1 || e.altKey) {
            this.drag = { type: 'pan', startX: pos.x, startY: pos.y, offsetX: this.offsetX, offsetY: this.offsetY };
            return;
        }
        if (this.readOnly || !this.image) {
            this.drag = { type: 'pan', startX: pos.x, startY: pos.y, offsetX: this.offsetX, offsetY: this.offsetY };
            return;
        }

        const image = this.toImage(pos.x, pos.y);

        if (this.mode === 'sam') {
            // 방금 확정한 폴리곤을 바로 손볼 수 있어야 한다. 꼭짓점이나 선을 집었으면 그것이 먼저다.
            // (둘 다 선택된 도형에만 반응하므로, 빈 곳을 누르면 평소처럼 SAM 점이 찍힌다.)
            const grabbed = this.beginVertexEdit(pos, image, e);
            if (grabbed) return;

            // 오른쪽 버튼과 Shift 는 '여기는 빼라'는 뜻이다 (배경 점)
            this.addSamPoint(image, !(e.button === 2 || e.shiftKey));
            return;
        }

        if (this.mode === 'polygon') {
            if (e.button === 2) { this.finishPolygon(); return; }
            this.addPolygonPoint(image);
            return;
        }

        // 이미 있는 라벨을 집었는지 먼저 본다 (조절점 → 선 → 안쪽 순)
        if (this.beginVertexEdit(pos, image, e)) return;
        const hit = this.hitAnnotation(image);
        if (hit >= 0) {
            this.selected = hit;
            this.drag = { type: 'move', index: hit, start: image, origin: { ...this.annotations[hit] } };
            this.notifySelected();
            this.draw();
            return;
        }

        if (this.mode === 'box' || this.mode === 'text') {
            if (!this.activeClass) return;
            this.selected = -1;
            this.drag = { type: 'create', start: image, current: image };
            this.draw();
            return;
        }

        // 분류 모드에는 그릴 것이 없다 — 빈 곳을 끌면 이동
        this.drag = { type: 'pan', startX: pos.x, startY: pos.y, offsetX: this.offsetX, offsetY: this.offsetY };
        this.selected = -1;
        this.notifySelected();
        this.draw();
    }

    /**
     * 선택된 도형의 조절점이나 선을 집었으면 그 편집을 시작한다.
     * 잡았으면 true — 부르는 쪽은 거기서 멈춘다.
     */
    beginVertexEdit(pos, image, e) {
        const handle = this.hitHandle(pos);
        if (handle) {
            this.selected = handle.index;
            if (handle.vertex !== undefined) {
                // Ctrl+클릭은 그 꼭짓점을 뺀다
                if (e.ctrlKey || e.metaKey) { this.removeVertex(handle.index, handle.vertex); return true; }
                this.drag = { type: 'vertex', index: handle.index, vertex: handle.vertex };
            } else {
                this.drag = {
                    type: 'resize', index: handle.index, corner: handle.corner,
                    origin: { ...this.annotations[handle.index] },
                };
            }
            this.draw();
            return true;
        }

        // 폴리곤 선 위를 누르면 그 자리에 꼭짓점을 하나 끼우고 바로 끌 수 있게 한다
        const edge = this.hitEdge(pos);
        if (edge) {
            const vertex = this.insertVertex(edge.index, edge.after, image);
            if (vertex >= 0) {
                this.drag = { type: 'vertex', index: edge.index, vertex };
                this.draw();
                return true;
            }
        }
        return false;
    }

    onPointerMove(e) {
        const pos = this.eventPos(e);
        if (!this.drag) {
            if (this.mode === 'polygon' && this.draftPolygon) { this.draftCursor = this.toImage(pos.x, pos.y); this.draw(); }
            const previous = this.hoverIndex;
            this.hoverIndex = this.readOnly ? -1 : this.hitAnnotation(this.toImage(pos.x, pos.y));
            if (previous !== this.hoverIndex) this.draw();
            this.canvas.style.cursor = this.cursorFor(pos);
            return;
        }

        if (this.drag.type === 'pan') {
            this.offsetX = this.drag.offsetX + (pos.x - this.drag.startX);
            this.offsetY = this.drag.offsetY + (pos.y - this.drag.startY);
            this.draw();
            return;
        }

        const image = this.toImage(pos.x, pos.y);
        if (this.drag.type === 'create') {
            this.drag.current = image;
            this.draw();
        } else if (this.drag.type === 'move') {
            this.moveAnnotation(this.drag.index, this.drag.origin, image.x - this.drag.start.x, image.y - this.drag.start.y);
            this.draw();
        } else if (this.drag.type === 'resize') {
            this.resizeAnnotation(this.drag.index, this.drag.origin, this.drag.corner, image);
            this.draw();
        } else if (this.drag.type === 'vertex') {
            const a = this.annotations[this.drag.index];
            if (a?.points?.[this.drag.vertex]) {
                a.points[this.drag.vertex] = [clamp01(image.x), clamp01(image.y)];
                this.draw();
            }
        }
    }

    onPointerUp() {
        if (!this.drag) return;
        const drag = this.drag;
        this.drag = null;

        if (drag.type === 'create') {
            const box = normalizeBox(drag.start, drag.current);
            // 클릭만 하고 끌지 않았으면 무시한다
            if (box.w >= MIN_BOX && box.h >= MIN_BOX) {
                const annotation = {
                    shape: this.mode === 'text' ? 'text' : 'box',
                    className: this.activeClass,
                    x: box.x, y: box.y, w: box.w, h: box.h,
                };
                if (this.mode === 'text') annotation.text = '';
                this.annotations.push(annotation);
                this.selected = this.annotations.length - 1;
                this.notifyChanged();
                this.notifySelected();
                // OCR 은 만들자마자 텍스트를 받아야 쓸모가 있다
                if (this.mode === 'text') this.dotNet?.invokeMethodAsync('OnTextRequested', this.selected);
            }
        } else if (drag.type === 'move' || drag.type === 'resize' || drag.type === 'vertex') {
            this.clampAnnotation(drag.index);
            this.notifyChanged();
        }
        this.draw();
    }

    onWheel(e) {
        e.preventDefault();
        const pos = this.eventPos(e);
        this.zoomBy(e.deltaY < 0 ? 1.15 : 1 / 1.15, pos.x, pos.y);
    }

    onKeyDown(e) {
        // 입력란에 타이핑 중이면 캔버스 단축키를 잡지 않는다
        const tag = document.activeElement?.tagName;
        if (tag === 'INPUT' || tag === 'TEXTAREA' || document.activeElement?.isContentEditable) return;

        if (e.key === 'Escape') {
            if (this.samPoints.length || this.samPreview) { this.clearSam(); e.preventDefault(); return; }
            if (this.draftPolygon) { this.draftPolygon = null; this.draw(); e.preventDefault(); return; }
            this.selected = -1; this.notifySelected(); this.draw(); return;
        }
        if (this.readOnly) return;

        if (e.key === 'Delete' || e.key === 'Backspace') {
            // SAM 으로 찍는 중이면 마지막 점만 무른다 — 라벨을 지우는 것이 아니다
            if (this.samPoints.length) { this.undoSamPoint(); e.preventDefault(); return; }
            if (this.selected >= 0) {
                this.annotations.splice(this.selected, 1);
                this.selected = -1;
                this.notifyChanged();
                this.notifySelected();
                this.draw();
                e.preventDefault();
            }
            return;
        }
        if (e.key === 'Tab' && this.samCandidates.length > 1) {
            this.cycleSam(e.shiftKey ? -1 : 1);
            e.preventDefault();
            return;
        }
        if (e.key === 'Enter' && this.samPreview) { this.confirmSam(); e.preventDefault(); return; }
        if (e.key === 'Enter' && this.draftPolygon) { this.finishPolygon(); e.preventDefault(); return; }

        // 숫자키로 클래스 선택 (1~9)
        if (/^[1-9]$/.test(e.key) && !e.ctrlKey && !e.altKey) {
            const index = parseInt(e.key, 10) - 1;
            if (index < this.classes.length) {
                this.setActiveClass(this.classes[index]);
                this.dotNet?.invokeMethodAsync('OnClassChanged', this.classes[index]);
                e.preventDefault();
            }
        }
    }

    cursorFor(pos) {
        if (this.readOnly) return 'grab';
        const handle = this.hitHandle(pos);
        if (handle) return handle.vertex !== undefined ? 'pointer' : 'nwse-resize';
        if (this.hitEdge(pos)) return 'copy';        // 여기를 누르면 꼭짓점이 하나 생긴다
        if (this.mode === 'polygon' || this.mode === 'sam') return 'crosshair';
        if (this.hoverIndex >= 0) return 'move';
        return this.mode === 'classification' ? 'grab' : 'crosshair';
    }

    // ───────────── 도형 조작 ─────────────

    addPolygonPoint(point) {
        if (!this.activeClass) return;
        if (!this.draftPolygon) this.draftPolygon = { className: this.activeClass, points: [] };
        this.draftPolygon.points.push({ x: clamp01(point.x), y: clamp01(point.y) });
        this.draw();
    }

    finishPolygon() {
        if (!this.draftPolygon) return;
        if (this.draftPolygon.points.length >= 3) {
            this.annotations.push({
                shape: 'polygon',
                className: this.draftPolygon.className,
                points: this.draftPolygon.points.map(p => [p.x, p.y]),
            });
            this.selected = this.annotations.length - 1;
            this.notifyChanged();
            this.notifySelected();
        }
        this.draftPolygon = null;
        this.draw();
    }

    // ───────────── SAM 보조 ─────────────

    /**
     * 클릭 한 점을 더하고 서버에 다시 물어본다.
     * 요청마다 번호를 매겨, 먼저 보낸 응답이 늦게 도착해도 최신 결과를 덮어쓰지 않게 한다.
     */
    addSamPoint(point, foreground) {
        if (!this.activeClass) return;
        this.samPoints.push({ x: clamp01(point.x), y: clamp01(point.y), foreground });
        this.requestSam();
    }

    undoSamPoint() {
        this.samPoints.pop();
        if (this.samPoints.length === 0) { this.clearSam(); return; }
        this.requestSam();
    }

    requestSam() {
        this.samSeq++;
        this.samPending = true;
        this.samMessage = null;
        this.draw();
        // 지금 보고 있는 후보를 함께 알린다 — 서버가 그 마스크에서 출발해 다음 점을 반영한다
        this.dotNet?.invokeMethodAsync('OnSamPointsRequested', this.samSeq, this.samPoints.map(p => ({
            x: p.x, y: p.y, foreground: p.foreground,
        })), this.samCandidates.length ? this.samIndex : null);
    }

    /** 서버 응답. seq 가 최신이 아니면 늦게 온 것이라 버린다. */
    applySamPreview(seq, candidates, best, message) {
        if (seq !== this.samSeq) return;
        this.samPending = false;
        this.samCandidates = candidates || [];
        // 모델이 고른 것을 먼저 보여 주고, 사람이 필요하면 넘긴다
        this.samIndex = Math.min(Math.max(best ?? 0, 0), Math.max(0, this.samCandidates.length - 1));
        this.samMessage = this.samCandidates.length ? null : (message || null);
        this.draw();
    }

    /** 지금 보고 있는 후보를 진짜 라벨로 굳힌다 */
    confirmSam() {
        const preview = this.samPreview;
        if (!preview || !this.activeClass || this.readOnly) return;

        this.annotations.push(this.samShape === 'box'
            ? { shape: 'box', className: this.activeClass, x: preview.x, y: preview.y, w: preview.w, h: preview.h }
            : { shape: 'polygon', className: this.activeClass, points: preview.points.map(([x, y]) => [x, y]) });

        this.selected = this.annotations.length - 1;
        this.clearSam(false);
        this.notifyChanged();
        this.notifySelected();
        this.draw();
    }

    clearSam(redraw = true) {
        this.samPoints = [];
        this.samCandidates = [];
        this.samIndex = 0;
        this.samPending = false;
        this.samMessage = null;
        this.samSeq++;              // 이미 나간 요청의 응답을 무효로 만든다
        if (redraw) this.draw();
    }

    /** 지금 보고 있는 후보 */
    get samPreview() {
        return this.samCandidates[this.samIndex] || null;
    }

    /**
     * 다른 후보로 넘어간다. SAM 은 클릭 하나에 크기가 다른 해석을 여럿 내놓는데,
     * 사선을 집었을 때 "사선만" 과 "사선이 놓인 판" 중 무엇을 원했는지는 사람만 안다.
     */
    cycleSam(delta) {
        if (this.samCandidates.length < 2) return;
        const count = this.samCandidates.length;
        this.samIndex = ((this.samIndex + delta) % count + count) % count;
        this.draw();
        // 화면 쪽 표시도 같이 움직여야 한다 (Tab 은 여기서만 처리되므로 알려 주지 않으면 어긋난다)
        this.dotNet?.invokeMethodAsync('OnSamIndexChanged', this.samIndex, count);
    }

    setClassification(className) {
        // 이미지당 하나뿐이므로 갈아 끼운다
        this.annotations = className ? [{ shape: 'classification', className }] : [];
        this.selected = this.annotations.length ? 0 : -1;
        this.notifyChanged();
        this.draw();
    }

    setText(index, text) {
        const a = this.annotations[index];
        if (!a) return;
        a.text = text;
        this.notifyChanged();
        this.draw();
    }

    deleteSelected() {
        if (this.selected < 0) return;
        this.annotations.splice(this.selected, 1);
        this.selected = -1;
        this.notifyChanged();
        this.notifySelected();
        this.draw();
    }

    selectAnnotation(index) {
        this.selected = index;
        this.notifySelected();
        this.draw();
    }

    moveAnnotation(index, origin, dx, dy) {
        const a = this.annotations[index];
        if (!a) return;
        if (a.shape === 'polygon') {
            a.points = origin.points.map(([x, y]) => [clamp01(x + dx), clamp01(y + dy)]);
        } else if (origin.x !== undefined) {
            a.x = clamp01(origin.x + dx, 1 - origin.w);
            a.y = clamp01(origin.y + dy, 1 - origin.h);
        }
    }

    resizeAnnotation(index, origin, corner, point) {
        const a = this.annotations[index];
        if (!a || origin.x === undefined) return;
        let left = origin.x, top = origin.y, right = origin.x + origin.w, bottom = origin.y + origin.h;
        if (corner.includes('w')) left = point.x;
        if (corner.includes('e')) right = point.x;
        if (corner.includes('n')) top = point.y;
        if (corner.includes('s')) bottom = point.y;
        const box = normalizeBox({ x: left, y: top }, { x: right, y: bottom });
        a.x = box.x; a.y = box.y; a.w = box.w; a.h = box.h;
    }

    clampAnnotation(index) {
        const a = this.annotations[index];
        if (!a) return;
        if (a.shape === 'polygon') {
            a.points = a.points.map(([x, y]) => [clamp01(x), clamp01(y)]);
        } else if (a.x !== undefined) {
            a.x = clamp01(a.x);
            a.y = clamp01(a.y);
            a.w = Math.min(a.w, 1 - a.x);
            a.h = Math.min(a.h, 1 - a.y);
        }
    }

    hitAnnotation(point) {
        // 위에 그려진 것부터 집는다
        for (let i = this.annotations.length - 1; i >= 0; i--) {
            const a = this.annotations[i];
            if (a.shape === 'classification') continue;
            if (a.shape === 'polygon') {
                if (pointInPolygon(point, a.points)) return i;
            } else if (a.x !== undefined) {
                if (point.x >= a.x && point.x <= a.x + a.w && point.y >= a.y && point.y <= a.y + a.h) return i;
            }
        }
        return -1;
    }

    hitHandle(pos) {
        if (this.selected < 0) return null;
        const a = this.annotations[this.selected];
        if (!a) return null;

        // 폴리곤은 꼭짓점 하나하나가 조절점이다. SAM 이 만들어 준 경계를 손보는 유일한 길이라
        // 사각형과 같은 무게로 다룬다.
        if (a.shape === 'polygon') {
            for (let i = 0; i < (a.points?.length || 0); i++) {
                const c = this.toCanvas(a.points[i][0], a.points[i][1]);
                if (Math.abs(c.x - pos.x) <= HANDLE_SIZE && Math.abs(c.y - pos.y) <= HANDLE_SIZE)
                    return { index: this.selected, vertex: i };
            }
            return null;
        }

        if (a.x === undefined) return null;
        const corners = [
            ['nw', a.x, a.y], ['ne', a.x + a.w, a.y],
            ['sw', a.x, a.y + a.h], ['se', a.x + a.w, a.y + a.h],
        ];
        for (const [corner, nx, ny] of corners) {
            const c = this.toCanvas(nx, ny);
            if (Math.abs(c.x - pos.x) <= HANDLE_SIZE && Math.abs(c.y - pos.y) <= HANDLE_SIZE)
                return { index: this.selected, corner };
        }
        return null;
    }

    /** 선분 위를 집었는지 — 꼭짓점을 새로 끼울 자리를 찾는다 */
    hitEdge(pos) {
        if (this.selected < 0) return null;
        const a = this.annotations[this.selected];
        if (!a || a.shape !== 'polygon' || !a.points || a.points.length < 3) return null;

        for (let i = 0; i < a.points.length; i++) {
            const p1 = this.toCanvas(a.points[i][0], a.points[i][1]);
            const j = (i + 1) % a.points.length;
            const p2 = this.toCanvas(a.points[j][0], a.points[j][1]);
            if (distanceToSegment(pos, p1, p2) <= HIT_SLOP + 2) return { index: this.selected, after: i };
        }
        return null;
    }

    /** 선분 가운데를 눌러 꼭짓점을 하나 끼운다 */
    insertVertex(index, after, point) {
        const a = this.annotations[index];
        if (!a || a.shape !== 'polygon') return -1;
        a.points.splice(after + 1, 0, [clamp01(point.x), clamp01(point.y)]);
        this.notifyChanged();
        this.draw();
        return after + 1;
    }

    /** 꼭짓점 하나를 뺀다. 삼각형 아래로는 줄이지 않는다. */
    removeVertex(index, vertex) {
        const a = this.annotations[index];
        if (!a || a.shape !== 'polygon' || a.points.length <= 3) return;
        a.points.splice(vertex, 1);
        this.notifyChanged();
        this.draw();
    }

    // ───────────── 그리기 ─────────────

    draw() {
        const ctx = this.canvas.getContext('2d');
        if (!ctx) return;
        ctx.save();
        ctx.setTransform(this.dpr || 1, 0, 0, this.dpr || 1, 0, 0);
        ctx.clearRect(0, 0, this.viewWidth, this.viewHeight);
        // 사진 바깥 바탕. 화면 전체가 밝은 톤이라 여기만 새까맣게 두면 눈이 튄다.
        ctx.fillStyle = '#3a3d42';
        ctx.fillRect(0, 0, this.viewWidth, this.viewHeight);

        if (this.image) {
            ctx.imageSmoothingEnabled = this.scale < 4;   // 크게 확대하면 픽셀을 그대로 보여 준다
            ctx.drawImage(this.image, this.offsetX, this.offsetY,
                this.image.naturalWidth * this.scale, this.image.naturalHeight * this.scale);
        }

        this.annotations.forEach((a, i) => this.drawAnnotation(ctx, a, i));
        if (this.drag?.type === 'create') this.drawDraftBox(ctx);
        if (this.draftPolygon) this.drawDraftPolygon(ctx);
        if (this.mode === 'sam') this.drawSam(ctx);
        ctx.restore();
    }

    drawAnnotation(ctx, a, index) {
        const color = this.colorFor(a.className);
        const selected = index === this.selected;
        const hovered = index === this.hoverIndex;

        if (a.shape === 'classification') {
            // 이미지 전체에 붙는 라벨이라 테두리로만 표시한다
            ctx.strokeStyle = color;
            ctx.lineWidth = 6;
            ctx.strokeRect(2, 2, this.viewWidth - 4, this.viewHeight - 4);
            this.drawLabelChip(ctx, 12, 12, a.className, color);
            return;
        }

        ctx.lineWidth = selected ? 3 : hovered ? 2.5 : 2;
        ctx.strokeStyle = color;
        ctx.fillStyle = withAlpha(color, selected ? 0.22 : 0.12);

        if (a.shape === 'polygon') {
            if (!a.points?.length) return;
            ctx.beginPath();
            a.points.forEach(([x, y], i) => {
                const c = this.toCanvas(x, y);
                if (i === 0) ctx.moveTo(c.x, c.y); else ctx.lineTo(c.x, c.y);
            });
            ctx.closePath();
            ctx.fill();
            ctx.stroke();
            if (selected) a.points.forEach(([x, y]) => this.drawHandle(ctx, this.toCanvas(x, y), color));
            const first = this.toCanvas(a.points[0][0], a.points[0][1]);
            this.drawLabelChip(ctx, first.x, first.y - 22, a.className, color);
            return;
        }

        const topLeft = this.toCanvas(a.x, a.y);
        const bottomRight = this.toCanvas(a.x + a.w, a.y + a.h);
        const w = bottomRight.x - topLeft.x, h = bottomRight.y - topLeft.y;
        ctx.fillRect(topLeft.x, topLeft.y, w, h);
        ctx.strokeRect(topLeft.x, topLeft.y, w, h);

        const caption = a.shape === 'text' && a.text ? `${a.className}: ${a.text}` : a.className;
        this.drawLabelChip(ctx, topLeft.x, topLeft.y - 22, caption, color);

        if (selected) {
            [[a.x, a.y], [a.x + a.w, a.y], [a.x, a.y + a.h], [a.x + a.w, a.y + a.h]]
                .forEach(([nx, ny]) => this.drawHandle(ctx, this.toCanvas(nx, ny), color));
        }
    }

    drawDraftBox(ctx) {
        const box = normalizeBox(this.drag.start, this.drag.current);
        const topLeft = this.toCanvas(box.x, box.y);
        const bottomRight = this.toCanvas(box.x + box.w, box.y + box.h);
        ctx.setLineDash([6, 4]);
        ctx.strokeStyle = this.colorFor(this.activeClass);
        ctx.lineWidth = 2;
        ctx.strokeRect(topLeft.x, topLeft.y, bottomRight.x - topLeft.x, bottomRight.y - topLeft.y);
        ctx.setLineDash([]);
    }

    drawDraftPolygon(ctx) {
        const color = this.colorFor(this.draftPolygon.className);
        const points = this.draftPolygon.points;
        if (!points.length) return;

        ctx.strokeStyle = color;
        ctx.lineWidth = 2;
        ctx.beginPath();
        points.forEach(({ x, y }, i) => {
            const c = this.toCanvas(x, y);
            if (i === 0) ctx.moveTo(c.x, c.y); else ctx.lineTo(c.x, c.y);
        });
        // 커서까지 미리 이어 보여 준다
        if (this.draftCursor) {
            const c = this.toCanvas(this.draftCursor.x, this.draftCursor.y);
            ctx.setLineDash([5, 4]);
            ctx.lineTo(c.x, c.y);
        }
        ctx.stroke();
        ctx.setLineDash([]);
        points.forEach(({ x, y }) => this.drawHandle(ctx, this.toCanvas(x, y), color));
    }

    /** SAM 미리보기 — 아직 라벨이 아니므로 점선과 반투명으로 그려 확정된 라벨과 구분한다 */
    drawSam(ctx) {
        const color = this.colorFor(this.activeClass);

        if (this.samPreview?.points?.length) {
            ctx.save();
            ctx.beginPath();
            this.samPreview.points.forEach(([x, y], i) => {
                const c = this.toCanvas(x, y);
                if (i === 0) ctx.moveTo(c.x, c.y); else ctx.lineTo(c.x, c.y);
            });
            ctx.closePath();
            ctx.fillStyle = withAlpha(color, 0.25);
            ctx.fill();
            ctx.setLineDash([7, 4]);
            ctx.lineWidth = 2.5;
            ctx.strokeStyle = color;
            ctx.stroke();
            ctx.restore();

            const first = this.toCanvas(this.samPreview.points[0][0], this.samPreview.points[0][1]);
            const score = this.samPreview.score ? ` ${Math.round(this.samPreview.score * 100)}%` : '';
            const many = this.samCandidates.length > 1
                ? ` · 후보 ${this.samIndex + 1}/${this.samCandidates.length} (Tab)` : '';
            const split = this.samPreview.partCount > 1 ? ` · 조각 ${this.samPreview.partCount}개 중 1개` : '';
            this.drawLabelChip(ctx, first.x, first.y - 22,
                `${this.activeClass}${score}${many}${split} · Enter 확정`, color);
        }

        // 클릭한 자리를 남겨 둔다 — 어디를 집었는지 보여야 점을 더 찍을지 판단할 수 있다
        for (const point of this.samPoints) {
            const c = this.toCanvas(point.x, point.y);
            ctx.beginPath();
            ctx.arc(c.x, c.y, 6, 0, Math.PI * 2);
            ctx.fillStyle = point.foreground ? '#2e7d32' : '#c62828';
            ctx.fill();
            ctx.lineWidth = 2;
            ctx.strokeStyle = '#fff';
            ctx.stroke();
            if (!point.foreground) {
                // 배경 점은 가운데 가로줄로 '빼기'를 표시한다
                ctx.beginPath();
                ctx.moveTo(c.x - 3, c.y);
                ctx.lineTo(c.x + 3, c.y);
                ctx.strokeStyle = '#fff';
                ctx.lineWidth = 2;
                ctx.stroke();
            }
        }

        if (this.samPending) this.drawStatusChip(ctx, '분할 중…', '#1e88e5');
        else if (this.samMessage) this.drawStatusChip(ctx, this.samMessage, '#e53935');
    }

    drawStatusChip(ctx, text, color) {
        ctx.font = '13px "Segoe UI", "Malgun Gothic", sans-serif';
        const width = ctx.measureText(text).width + 20;
        const x = (this.viewWidth - width) / 2;
        ctx.fillStyle = color;
        ctx.fillRect(x, 12, width, 26);
        ctx.fillStyle = '#fff';
        ctx.fillText(text, x + 10, 30);
    }

    drawHandle(ctx, pos, color) {
        ctx.fillStyle = '#fff';
        ctx.strokeStyle = color;
        ctx.lineWidth = 2;
        ctx.fillRect(pos.x - HANDLE_SIZE / 2, pos.y - HANDLE_SIZE / 2, HANDLE_SIZE, HANDLE_SIZE);
        ctx.strokeRect(pos.x - HANDLE_SIZE / 2, pos.y - HANDLE_SIZE / 2, HANDLE_SIZE, HANDLE_SIZE);
    }

    drawLabelChip(ctx, x, y, text, color) {
        if (!text) return;
        ctx.font = '12px "Segoe UI", "Malgun Gothic", sans-serif';
        const width = ctx.measureText(text).width + 10;
        ctx.fillStyle = color;
        ctx.fillRect(x, Math.max(0, y), width, 18);
        ctx.fillStyle = '#fff';
        ctx.fillText(text, x + 5, Math.max(0, y) + 13);
    }

    colorFor(className) {
        const index = this.classes.indexOf(className);
        return PALETTE[(index < 0 ? 0 : index) % PALETTE.length];
    }

    // ───────────── Blazor 통지 ─────────────

    notifyChanged() {
        this.dotNet?.invokeMethodAsync('OnAnnotationsChanged', this.getAnnotations());
    }

    notifySelected() {
        this.dotNet?.invokeMethodAsync('OnSelectionChanged', this.selected);
    }
}

// ───────────── 도우미 ─────────────

function clamp01(v, max = 1) {
    return Math.min(Math.max(v, 0), max);
}

function normalizeBox(a, b) {
    const x = clamp01(Math.min(a.x, b.x));
    const y = clamp01(Math.min(a.y, b.y));
    const right = clamp01(Math.max(a.x, b.x));
    const bottom = clamp01(Math.max(a.y, b.y));
    return { x, y, w: right - x, h: bottom - y };
}

/** 짝수-홀수 규칙. 폴리곤 안을 집었는지 판단한다. */
function pointInPolygon(point, points) {
    let inside = false;
    for (let i = 0, j = points.length - 1; i < points.length; j = i++) {
        const [xi, yi] = points[i];
        const [xj, yj] = points[j];
        const intersects = (yi > point.y) !== (yj > point.y)
            && point.x < ((xj - xi) * (point.y - yi)) / (yj - yi || 1e-12) + xi;
        if (intersects) inside = !inside;
    }
    return inside;
}

/** 점에서 선분까지의 거리 (화면 픽셀). 폴리곤 선을 집었는지 판단한다. */
function distanceToSegment(point, a, b) {
    const dx = b.x - a.x, dy = b.y - a.y;
    const lengthSquared = dx * dx + dy * dy;
    if (lengthSquared < 1e-9) return Math.hypot(point.x - a.x, point.y - a.y);
    let t = ((point.x - a.x) * dx + (point.y - a.y) * dy) / lengthSquared;
    t = Math.min(1, Math.max(0, t));
    return Math.hypot(point.x - (a.x + t * dx), point.y - (a.y + t * dy));
}

function withAlpha(hex, alpha) {
    const value = parseInt(hex.slice(1), 16);
    return `rgba(${(value >> 16) & 255}, ${(value >> 8) & 255}, ${value & 255}, ${alpha})`;
}

// ───────────── Blazor interop ─────────────

export function create(canvasId, dotNet, options) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) throw new Error(`캔버스를 찾을 수 없습니다: ${canvasId}`);
    destroy(canvasId);
    instances.set(canvasId, new LabelCanvas(canvas, dotNet, options || {}));
}

export function destroy(canvasId) {
    const instance = instances.get(canvasId);
    if (instance) { instance.dispose(); instances.delete(canvasId); }
}

const call = (canvasId, fn) => {
    const instance = instances.get(canvasId);
    return instance ? fn(instance) : null;
};

export const loadImage = (id, url) => call(id, i => i.loadImage(url));
export const setAnnotations = (id, annotations) => call(id, i => i.setAnnotations(annotations));
export const getAnnotations = (id) => call(id, i => i.getAnnotations()) || [];
export const setMode = (id, mode) => call(id, i => i.setMode(mode));
export const setActiveClass = (id, cls) => call(id, i => i.setActiveClass(cls));
export const setAnnotationClass = (id, index, cls) => call(id, i => i.setAnnotationClass(index, cls));
export const setReadOnly = (id, readOnly) => call(id, i => i.setReadOnly(readOnly));
export const setClassification = (id, cls) => call(id, i => i.setClassification(cls));
export const setText = (id, index, text) => call(id, i => i.setText(index, text));
export const deleteSelected = (id) => call(id, i => i.deleteSelected());
export const selectAnnotation = (id, index) => call(id, i => i.selectAnnotation(index));
export const fit = (id) => call(id, i => i.fit());
export const zoom = (id, factor) => call(id, i => i.zoomBy(factor));
export const finishPolygon = (id) => call(id, i => i.finishPolygon());
export const setSamShape = (id, shape) => call(id, i => i.setSamShape(shape));
export const setSamPreview = (id, seq, candidates, best, message) =>
    call(id, i => i.applySamPreview(seq, candidates, best, message));
export const cycleSam = (id, delta) => call(id, i => i.cycleSam(delta));
export const confirmSam = (id) => call(id, i => i.confirmSam());
export const clearSam = (id) => call(id, i => i.clearSam());
export const resize = (id) => call(id, i => i.resize());
