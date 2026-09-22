// 사진 올리기 대화상자: 어디에 놓아도 파일이 들어오게 한다.
//
// 파일 입력(<input type=file>)은 점선 상자만 덮는다. 그런데 사람은 대화상자의 빈 자리에도 놓는다 —
// 그러면 브라우저는 아무 일도 하지 않고(파일이 여러 개면 넘어가지도 않는다), 화면은 그대로이며
// [올리기] 는 회색으로 남는다. 눌러도 아무 일이 없으니 "올렸는데 안 된다" 로만 보인다.
// 그래서 대화상자 전체에서 drop 을 받아 입력에 넣어 준다. 들어가는 길은 한 갈래로 유지한다 —
// 상자 안에 놓든 밖에 놓든 여기를 지나 InputFile 의 change 로 들어간다.

const HIGHLIGHT = "mlops-drop-over";

/// root 가 속한 대화상자 전체를 드롭 영역으로 만든다. 떼어내는 함수를 돌려준다.
export function attach(root) {
    if (!root) return null;
    const zone = root.closest(".mud-dialog") ?? root;
    const input = root.querySelector('input[type="file"]');
    if (!input) return null;

    // dragleave 는 자식으로 옮겨 갈 때도 뜬다. 들어온 횟수를 세어야 깜빡이지 않는다.
    let depth = 0;

    const enter = (e) => {
        if (!hasFiles(e)) return;
        e.preventDefault();
        depth++;
        zone.classList.add(HIGHLIGHT);
    };
    const over = (e) => {
        if (!hasFiles(e)) return;
        e.preventDefault();               // 이것이 없으면 drop 자체가 오지 않는다
        e.dataTransfer.dropEffect = "copy";
    };
    const leave = () => {
        if (--depth <= 0) { depth = 0; zone.classList.remove(HIGHLIGHT); }
    };
    const drop = (e) => {
        if (!hasFiles(e)) return;
        e.preventDefault();               // 상자 안에 놓였더라도 우리가 받는다 (길을 하나로 둔다)
        depth = 0;
        zone.classList.remove(HIGHLIGHT);
        const files = e.dataTransfer.files;
        if (!files || files.length === 0) return;
        input.files = files;
        input.dispatchEvent(new Event("change", { bubbles: true }));
    };

    zone.addEventListener("dragenter", enter);
    zone.addEventListener("dragover", over);
    zone.addEventListener("dragleave", leave);
    zone.addEventListener("drop", drop);

    return {
        detach: () => {
            zone.removeEventListener("dragenter", enter);
            zone.removeEventListener("dragover", over);
            zone.removeEventListener("dragleave", leave);
            zone.removeEventListener("drop", drop);
            zone.classList.remove(HIGHLIGHT);
        },
    };
}

/// 파일을 끄는 중일 때만 반응한다 — 글자를 끌어다 놓는 것까지 가로채면 입력칸이 이상해진다
function hasFiles(e) {
    const types = e.dataTransfer?.types;
    return !!types && Array.prototype.indexOf.call(types, "Files") >= 0;
}
