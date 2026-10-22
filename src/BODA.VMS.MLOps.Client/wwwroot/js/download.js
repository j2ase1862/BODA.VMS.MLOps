// 받은 바이트를 파일로 저장한다.
// API 는 Bearer 토큰으로만 열리므로 <a href> 나 새 탭으로는 받을 수 없다 (토큰이 실리지 않아 401).
// 화면이 HttpClient 로 받아 여기로 넘긴다.
export function saveBytes(fileName, contentType, bytes) {
    const blob = new Blob([bytes], { type: contentType || "application/octet-stream" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10000);
}
