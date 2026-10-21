using BODA.VMS.MLOps.Core.Export;
using FluentAssertions;

namespace BODA.VMS.MLOps.Tests.Core;

/// <summary>
/// 밖에서 만든 PP-OCR zip 이 train_ppocr.py 가 읽는 모양인지. 여기서 놓치면 판으로 굳은 뒤
/// 워커에서 학습이 시작되고 나서야 실패한다.
/// </summary>
public class PpOcrLayoutTests
{
    /// <summary>VisionSetup SynthData(DatasetWriter) 가 내는 모양 그대로</summary>
    private static Dictionary<string, string> SynthData() => new(StringComparer.Ordinal)
    {
        ["train_crops/000001.png"] = "",
        ["train_crops/000002.png"] = "",
        ["val_crops/000003.png"] = "",
        ["train_rec.txt"] = "train_crops/000001.png\tAB12\ntrain_crops/000002.png\t가나\n",
        ["val_rec.txt"] = "val_crops/000003.png\tLOT\n",
        ["dict.txt"] = "1\n2\nA\nB",
        ["ppocr_keys_v1.txt"] = "1\n2\nA\nB",
        ["char_labels.txt"] = "",
    };

    private static string? Check(Dictionary<string, string> files) =>
        PpOcrLayout.Problem(files.Keys, name => files[name]);

    [Fact]
    public void SynthData_output_is_accepted()
    {
        Check(SynthData()).Should().BeNull();
    }

    [Fact]
    public void Our_own_export_is_accepted()
    {
        // 우리 내보내기는 검증 샘플이 없으면 val_rec.txt 를 빈 파일로 낸다
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["train_crops/a-00.jpg"] = "",
            ["train_rec.txt"] = "train_crops/a-00.jpg\tX\n",
            ["val_rec.txt"] = "",
            ["dict.txt"] = "X",
            ["ppocr_keys_v1.txt"] = "X",
        };
        Check(files).Should().BeNull();
    }

    [Fact]
    public void Zip_of_the_folder_itself_is_explained()
    {
        // 탐색기에서 폴더째 압축하면 한 겹 더 들어간다 — 가장 흔한 실수라 고칠 방법까지 말한다
        var nested = SynthData().ToDictionary(kv => "ocr_synth/" + kv.Key, kv => kv.Value, StringComparer.Ordinal);
        Check(nested).Should().Contain("폴더째").And.Contain("ocr_synth/train_rec.txt");
    }

    [Fact]
    public void Missing_crop_is_named_with_its_line()
    {
        var files = SynthData();
        files.Remove("train_crops/000002.png");
        Check(files).Should().Contain("train_rec.txt 2번째 줄").And.Contain("train_crops/000002.png");
    }

    [Fact]
    public void Windows_separators_in_the_list_still_match()
    {
        var files = SynthData();
        files["train_rec.txt"] = "train_crops\\000001.png\tAB12\r\ntrain_crops\\000002.png\t가나\r\n";
        Check(files).Should().BeNull();
    }

    [Theory]
    [InlineData("val_rec.txt", "val_rec.txt")]
    [InlineData("train_rec.txt", "train_rec.txt")]
    public void Required_lists_must_exist(string remove, string expected)
    {
        var files = SynthData();
        files.Remove(remove);
        Check(files).Should().Contain(expected);
    }

    [Fact]
    public void Dictionary_must_exist()
    {
        var files = SynthData();
        files.Remove("dict.txt");
        files.Remove("ppocr_keys_v1.txt");
        Check(files).Should().Contain("문자 사전");
    }

    [Fact]
    public void A_line_without_a_label_is_rejected()
    {
        var files = SynthData();
        files["train_rec.txt"] = "train_crops/000001.png\n";
        Check(files).Should().Contain("형식");
    }

    [Fact]
    public void An_empty_training_list_is_rejected()
    {
        var files = SynthData();
        files["train_rec.txt"] = "\n";
        Check(files).Should().Contain("학습 샘플");
    }
}
