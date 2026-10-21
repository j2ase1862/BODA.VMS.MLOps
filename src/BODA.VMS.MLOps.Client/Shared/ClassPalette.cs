namespace BODA.VMS.MLOps.Client.Shared;

/// <summary>
/// 라벨·예측을 클래스 순서대로 칠하는 색. 화면마다 따로 두면 같은 클래스가 화면마다 다른 색이 된다.
/// <c>wwwroot/js/label-canvas.js</c> 의 <c>PALETTE</c> 와 순서가 같아야 한다 — 캔버스는 JS 안에서 칠한다.
/// </summary>
public static class ClassPalette
{
    // 라벨 색은 화면 강조색(#FF4052)과 겹치지 않게 파랑부터 시작한다.
    // 빨강이 앞에 있으면 "지금 고른 UI 요소" 와 "라벨" 이 같은 색이 되어 구분이 안 된다.
    private static readonly string[] Colors =
        ["#1e88e5", "#43a047", "#fb8c00", "#8e24aa", "#00acc1", "#d81b60", "#6d4c41", "#3949ab", "#c62828", "#00897b"];

    public static string ColorOf(int index) => Colors[(index < 0 ? 0 : index) % Colors.Length];
}
