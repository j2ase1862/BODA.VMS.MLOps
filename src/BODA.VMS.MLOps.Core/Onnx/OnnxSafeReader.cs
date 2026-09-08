using System.Text;

namespace BODA.VMS.MLOps.Core.Onnx;

/// <summary>그래프 입력/출력 텐서 하나 — 이름과 차원(dim_value 없으면 null: 동적)</summary>
public sealed record OnnxTensorInfo(string Name, long?[] Dims)
{
    public int Rank => Dims.Length;
    public override string ToString() => $"{Name}[{string.Join(",", Dims.Select(d => d?.ToString() ?? "?"))}]";
}

/// <summary>한 번의 순회로 읽은 ONNX 헤더 정보</summary>
public sealed record OnnxFileInfo(
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<OnnxTensorInfo> Inputs,
    IReadOnlyList<OnnxTensorInfo> Outputs);

/// <summary>
/// InferenceSession 없이 ONNX 파일에서 metadata_props 와 그래프 입출력의 이름·형상만 읽는다.
/// 가중치(initializer)와 노드는 길이만 읽고 Seek 으로 건너뛰므로 대용량 모델도 수~수십 ms 에 끝난다.
///
/// <para>
/// <b>신뢰할 수 없는 입력을 다룬다.</b> 업로드된 파일이 그대로 들어오므로 모든 길이 필드는 남은 바이트 수와 대조한다.
/// varint 는 최대 2^64-1 이라 <c>(long)</c> 로 캐스팅하면 음수가 될 수 있고, 그 값으로 계산한 끝 오프셋에
/// <c>Stream.Position</c> 을 대입하면 스트림이 뒤로 감겨 파싱이 무한히 반복된다.
/// <see cref="TryReadLength"/> 가 캐스팅 전에 <c>ulong</c> 상태로 비교해 이 경우를 막는다 —
/// 길이는 항상 0 이상이고 끝 오프셋은 절대 현재 위치보다 앞설 수 없다. 매 회전은 태그 1바이트 이상을 소비하므로 종료가 보장된다.
/// </para>
///
/// <code>
/// ModelProto.metadata_props(14) → StringStringEntryProto.key(1)/value(2)
/// ModelProto.graph(7) → GraphProto.input(11)/output(12) → ValueInfoProto.name(1), type(2)
///   → TypeProto.tensor_type(1) → Tensor.shape(2) → TensorShapeProto.dim(1) → Dimension.dim_value(1)
/// </code>
/// </summary>
public static class OnnxSafeReader
{
    private const int WireVarint = 0, WireFixed64 = 1, WireLen = 2, WireFixed32 = 5;

    private const int ModelMetadataProps = 14, ModelGraph = 7;
    private const int EntryKey = 1, EntryValue = 2;
    private const int GraphInput = 11, GraphOutput = 12;
    private const int ValueInfoName = 1, ValueInfoType = 2;

    /// <summary>손상·악의적 파일이면 그때까지 읽은 것만 돌려준다 (예외를 던지지 않는다).</summary>
    public static OnnxFileInfo Read(string path)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        var inputs = new List<OnnxTensorInfo>();
        var outputs = new List<OnnxTensorInfo>();
        if (!File.Exists(path)) return new OnnxFileInfo(metadata, inputs, outputs);

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            long end = fs.Length;
            while (fs.Position < end)
            {
                if (!TryReadTag(fs, out int field, out int wire)) break;

                if (field == ModelMetadataProps && wire == WireLen)
                {
                    if (!TryReadLength(fs, end, out long len)) break;
                    long entryEnd = fs.Position + len;
                    ParseMetadataEntry(fs, entryEnd, metadata);
                    fs.Position = entryEnd;
                }
                else if (field == ModelGraph && wire == WireLen)
                {
                    if (!TryReadLength(fs, end, out long len)) break;
                    long graphEnd = fs.Position + len;
                    ParseGraph(fs, graphEnd, inputs, outputs);
                    fs.Position = graphEnd;
                }
                else if (!Skip(fs, wire, end)) break;
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException)
        {
            // 읽기 실패 — 지금까지 모은 것만 반환한다
        }
        return new OnnxFileInfo(metadata, inputs, outputs);
    }

    private static void ParseMetadataEntry(Stream s, long entryEnd, Dictionary<string, string> result)
    {
        string? key = null, value = null;
        while (s.Position < entryEnd)
        {
            if (!TryReadTag(s, out int field, out int wire)) return;
            if (wire == WireLen && (field == EntryKey || field == EntryValue))
            {
                if (!TryReadLength(s, entryEnd, out long len)) return;
                var text = ReadString(s, len);
                if (field == EntryKey) key = text; else value = text;
            }
            else if (!Skip(s, wire, entryEnd)) return;
        }
        if (key is not null) result[key] = value ?? string.Empty;
    }

    private static void ParseGraph(Stream s, long graphEnd, List<OnnxTensorInfo> ins, List<OnnxTensorInfo> outs)
    {
        while (s.Position < graphEnd)
        {
            if (!TryReadTag(s, out int field, out int wire)) return;
            if (wire == WireLen && (field == GraphInput || field == GraphOutput))
            {
                if (!TryReadLength(s, graphEnd, out long len)) return;
                long valueEnd = s.Position + len;
                var info = ParseValueInfo(s, valueEnd);
                if (info is not null) (field == GraphInput ? ins : outs).Add(info);
                s.Position = valueEnd;
            }
            else if (!Skip(s, wire, graphEnd)) return;
        }
    }

    private static OnnxTensorInfo? ParseValueInfo(Stream s, long valueEnd)
    {
        string? name = null;
        long?[] dims = [];
        while (s.Position < valueEnd)
        {
            if (!TryReadTag(s, out int field, out int wire)) break;
            if (wire != WireLen) { if (!Skip(s, wire, valueEnd)) break; continue; }
            if (!TryReadLength(s, valueEnd, out long len)) break;
            long fieldEnd = s.Position + len;
            if (field == ValueInfoName) name = ReadString(s, len);
            else if (field == ValueInfoType) dims = ParseTypeProto(s, fieldEnd);
            s.Position = fieldEnd;
        }
        return name is null ? null : new OnnxTensorInfo(name, dims);
    }

    private static long?[] ParseTypeProto(Stream s, long typeEnd)
    {
        while (s.Position < typeEnd)
        {
            if (!TryReadTag(s, out int field, out int wire)) break;
            if (field == 1 && wire == WireLen) // tensor_type
            {
                if (!TryReadLength(s, typeEnd, out long len)) break;
                long tensorEnd = s.Position + len;
                while (s.Position < tensorEnd)
                {
                    if (!TryReadTag(s, out int f2, out int w2)) return [];
                    if (f2 == 2 && w2 == WireLen) // shape
                    {
                        if (!TryReadLength(s, tensorEnd, out long shapeLen)) return [];
                        return ParseShape(s, s.Position + shapeLen);
                    }
                    if (!Skip(s, w2, tensorEnd)) return [];
                }
                return [];
            }
            if (!Skip(s, wire, typeEnd)) break;
        }
        return [];
    }

    private static long?[] ParseShape(Stream s, long shapeEnd)
    {
        var dims = new List<long?>();
        while (s.Position < shapeEnd)
        {
            if (!TryReadTag(s, out int field, out int wire)) break;
            if (field == 1 && wire == WireLen) // dim
            {
                if (!TryReadLength(s, shapeEnd, out long len)) break;
                long dimEnd = s.Position + len;
                long? value = null;
                while (s.Position < dimEnd)
                {
                    if (!TryReadTag(s, out int df, out int dw)) break;
                    if (df == 1 && dw == WireVarint) { if (TryReadVarint(s, out var v)) value = (long)v; }
                    else if (!Skip(s, dw, dimEnd)) break;
                }
                dims.Add(value);
                s.Position = dimEnd;
            }
            else if (!Skip(s, wire, shapeEnd)) break;
        }
        return dims.ToArray();
    }

    private static bool TryReadTag(Stream s, out int field, out int wire)
    {
        field = 0; wire = 0;
        if (!TryReadVarint(s, out var tag)) return false;
        field = (int)(tag >> 3);
        wire = (int)(tag & 0x7);
        return true;
    }

    /// <summary>
    /// 길이 필드를 읽되 남은 바이트 수를 넘으면 실패로 처리한다.
    /// ulong 상태로 비교하므로 2^63 이상 값이 음수 long 으로 바뀌어 스트림을 되감는 일이 없다.
    /// </summary>
    private static bool TryReadLength(Stream s, long boundary, out long length)
    {
        length = 0;
        if (!TryReadVarint(s, out var raw)) return false;
        long remaining = boundary - s.Position;
        if (remaining < 0 || raw > (ulong)remaining) return false;
        length = (long)raw;
        return true;
    }

    private static bool Skip(Stream s, int wire, long boundary)
    {
        switch (wire)
        {
            case WireVarint:
                return TryReadVarint(s, out _);
            case WireFixed64:
                if (boundary - s.Position < 8) return false;
                s.Position += 8;
                return true;
            case WireLen:
                if (!TryReadLength(s, boundary, out long len)) return false;
                s.Position += len;
                return true;
            case WireFixed32:
                if (boundary - s.Position < 4) return false;
                s.Position += 4;
                return true;
            default:
                return false; // SGROUP/EGROUP 은 ONNX 에서 쓰지 않는다
        }
    }

    /// <summary>길이는 호출 전에 TryReadLength 로 검증되어 스트림 안에 있음이 보장된다.</summary>
    private static string ReadString(Stream s, long length)
    {
        if (length == 0) return string.Empty;
        var buf = new byte[length];
        int offset = 0;
        while (offset < buf.Length)
        {
            int read = s.Read(buf, offset, buf.Length - offset);
            if (read <= 0) throw new EndOfStreamException();
            offset += read;
        }
        return Encoding.UTF8.GetString(buf);
    }

    private static bool TryReadVarint(Stream s, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (shift < 64)
        {
            int b = s.ReadByte();
            if (b < 0) return false;
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false; // 10바이트를 넘는 varint 는 잘못된 것
    }
}

/// <summary>그래프 입출력만 필요할 때 쓰는 얇은 래퍼</summary>
public static class OnnxGraphShapeReader
{
    public static (IReadOnlyList<OnnxTensorInfo> Inputs, IReadOnlyList<OnnxTensorInfo> Outputs) Read(string path)
    {
        var info = OnnxSafeReader.Read(path);
        return (info.Inputs, info.Outputs);
    }
}
