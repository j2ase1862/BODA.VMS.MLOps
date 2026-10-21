namespace BODA.VMS.MLOps.Core.Export;

/// <summary>
/// 밖에서 만든 PP-OCR 인식(rec) 데이터셋 zip 이 <c>train_ppocr.py</c> 가 읽는 모양인지 본다.
/// VisionSetup 의 OCR 합성 데이터(SynthData) 출력을 올려 학습하는 길이 이것이다 (개발 문서 §5.5 1b).
///
/// <para>스크립트는 데이터셋 폴더 바로 아래의 <c>train_rec.txt</c>·<c>val_rec.txt</c> 를 읽고,
/// 줄마다 적힌 상대 경로로 크롭을 찾는다. 그래서 올릴 때 이 셋을 확인하지 않으면 구조가 틀린 zip 이
/// 판으로 굳은 뒤 워커에서 학습이 시작되고 나서야 실패한다. 가장 흔한 것은 탐색기에서 폴더째 압축해
/// 한 겹 더 들어간 zip 이다.</para>
/// </summary>
public static class PpOcrLayout
{
    public const string TrainList = "train_rec.txt";
    public const string ValList = "val_rec.txt";
    public static readonly string[] DictFiles = ["dict.txt", "ppocr_keys_v1.txt"];

    /// <summary>
    /// 문제가 없으면 null, 있으면 사람이 읽을 이유.
    /// </summary>
    /// <param name="files">zip 안의 파일 경로 (구분자는 '/')</param>
    /// <param name="readText">그 경로의 내용을 UTF-8 로 읽는다</param>
    public static string? Problem(IReadOnlyCollection<string> files, Func<string, string> readText)
    {
        var set = new HashSet<string>(files.Select(f => f.Replace('\\', '/')), StringComparer.Ordinal);

        if (!set.Contains(TrainList))
        {
            var nested = set.FirstOrDefault(f => f.EndsWith("/" + TrainList, StringComparison.Ordinal));
            return nested is null
                ? $"{TrainList} 가 없습니다. PP-OCR 인식 데이터셋은 {TrainList} · {ValList} · dict.txt 와 크롭 폴더를 담아야 합니다."
                : $"폴더째 압축되어 {nested} 에 있습니다. 폴더를 열고 그 안의 파일과 폴더를 모두 골라 압축하세요 — zip 맨 위에 {TrainList} 가 있어야 합니다.";
        }
        if (!set.Contains(ValList))
            return $"{ValList} 가 없습니다. 검증 샘플이 없더라도 빈 파일로 있어야 합니다.";
        if (!DictFiles.Any(set.Contains))
            return "문자 사전(dict.txt 또는 ppocr_keys_v1.txt)이 없습니다.";

        var trainCount = 0;
        foreach (var list in new[] { TrainList, ValList })
        {
            var lineNo = 0;
            foreach (var raw in readText(list).Split('\n'))
            {
                lineNo++;
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;

                var tab = line.IndexOf('\t');
                if (tab <= 0 || tab == line.Length - 1)
                    return $"{list} {lineNo}번째 줄이 '경로<탭>글자' 형식이 아닙니다.";
                var path = line[..tab].Replace('\\', '/');
                if (!set.Contains(path))
                    return $"{list} {lineNo}번째 줄의 이미지 {path} 가 zip 에 없습니다.";
                if (list == TrainList) trainCount++;
            }
        }
        return trainCount == 0 ? $"{TrainList} 에 학습 샘플이 하나도 없습니다." : null;
    }
}
