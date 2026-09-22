// 사진 격자의 단축키 — Ctrl+A(전부 선택)와 Esc(선택 해제).
//
// 문서 전체에서 듣는다. 격자에 focus 를 요구하면 사람이 먼저 빈 자리를 한 번 눌러야 하는데,
// 그것을 아는 사람은 없다. 대신 글자를 치는 중에는 비켜선다 — 검색 칸에서 Ctrl+A 는
// 친 글자를 고르는 것이지 사진을 고르는 것이 아니다.

const handlers = new Map();

function isTyping(target) {
    if (!target) return false;
    if (target.isContentEditable) return true;
    const tag = target.tagName;
    return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT';
}

function onKeyDown(event) {
    if (isTyping(event.target)) return;

    // 대화상자가 떠 있으면 그 안의 일이다. 뒤에 깔린 격자가 가로채면
    // Esc 로 창을 닫으려던 것이 선택만 풀고 창은 그대로 남는다.
    if (document.querySelector('.mud-dialog-container .mud-dialog')) return;

    const ctrl = event.ctrlKey || event.metaKey;
    let name = null;
    if (ctrl && (event.key === 'a' || event.key === 'A')) name = 'SelectAll';
    else if (event.key === 'Escape') name = 'ClearSelection';
    if (!name) return;

    // 듣는 곳이 없으면 브라우저 기본 동작을 그대로 둔다.
    if (handlers.size === 0) return;
    event.preventDefault();
    for (const dotNet of handlers.values()) dotNet.invokeMethodAsync(name);
}

document.addEventListener('keydown', onKeyDown);

export function register(key, dotNet) {
    handlers.set(key, dotNet);
}

export function unregister(key) {
    handlers.delete(key);
}
