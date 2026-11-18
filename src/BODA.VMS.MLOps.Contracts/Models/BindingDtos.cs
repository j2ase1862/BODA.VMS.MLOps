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

/// <summary>운영 웹의 레시피 한 줄 — 바인딩 화면이 레시피 ID 를 손으로 치지 않고 고르게 한다 (ML-02).</summary>
/// <param name="RecipeId">바인딩의 레시피 키 — 운영 웹 레시피 ID(정수)를 문자열로</param>
public sealed record WebRecipeDto(string RecipeId, string Name, int ClientId, string ClientName);

/// <summary>
/// 운영 웹에서 당겨 온 레시피 목록. 못 당겨 오면 <c>Available=false</c> 와 이유 — 화면은 직접 입력으로 돌아간다.
/// 도구 목록은 운영 웹에 없다(도구는 VisionServer 소유) — 도구 ID 는 여전히 직접 넣는다.
/// </summary>
public sealed record WebRecipeListDto(bool Available, string? Message, IReadOnlyList<WebRecipeDto> Recipes);

/// <summary>레시피 동기화 JSON 확장 (Phase 1 §6.4) — VMS 가 바인딩 캐시 선반영·프리페치에 사용</summary>
public sealed record RecipeModelBindingsPayload(string RecipeId, IReadOnlyList<RecipeModelBindingEntry> ModelBindings);

public sealed record RecipeModelBindingEntry(string ToolId, Guid ModelId, Guid? ModelVersionId, string? Sha256, int? Number, BindingMode Mode, string Reference, string? ArtifactUrl);
