namespace BODA.VMS.MLOps.Contracts.Pretrained;

/// <summary>사전학습 가중치 미러 자산 (Phase 3 §3 PretrainedAsset, §9). 워커는 서버 미러에서만 받는다.</summary>
public sealed record PretrainedAssetDto(string Ref, IReadOnlyList<PretrainedFileDto> Files, string? License, string? Source, string AddedBy, DateTime AddedAt, long TotalBytes);

public sealed record PretrainedFileDto(string FileName, string Sha256, long SizeBytes, string Url);

public sealed record CreatePretrainedAssetRequest(string Ref, string? License = null, string? Source = null);
