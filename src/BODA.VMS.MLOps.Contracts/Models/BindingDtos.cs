using BODA.VMS.MLOps.Core.Domain;

namespace BODA.VMS.MLOps.Contracts.Models;

/// <summary>레시피 도구 ↔ 모델 버전 바인딩 (Phase 1 §3, §5). Pinned 는 modelVersionId, FollowProduction 은 modelId 필수.</summary>
public sealed record SetBindingRequest(BindingMode Mode, Guid? ModelVersionId = null, Guid? ModelId = null, string? Reason = null);

public sealed record RollbackRequest(string? Reason = null);

public sealed record ModelBindingDto(
    Guid Id, string RecipeId, string ToolId, BindingMode Mode, Guid ModelId, string ModelName,
    Guid? ModelVersionId, int? VersionNumber, string? Sha256,
    /// <summary>FollowProduction 이면 현재 Production 버전으로 해석된 값 (없으면 null)</summary>
    Guid? ResolvedVersionId, int? ResolvedNumber, string? ResolvedSha256,
    string BoundBy, DateTime BoundAt, Guid? PreviousBindingId, bool IsActive, string? Reason,
    /// <summary>레시피 동기화 페이로드에 그대로 넣는 참조 문자열 model://…</summary>
    string Reference);

/// <summary>레시피 동기화 JSON 확장 (Phase 1 §6.4) — VMS 가 바인딩 캐시 선반영·프리페치에 사용</summary>
public sealed record RecipeModelBindingsPayload(string RecipeId, IReadOnlyList<RecipeModelBindingEntry> ModelBindings);

public sealed record RecipeModelBindingEntry(string ToolId, Guid ModelId, Guid? ModelVersionId, string? Sha256, int? Number, BindingMode Mode, string Reference, string? ArtifactUrl);
