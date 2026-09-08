using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Data;
using BODA.VMS.MLOps.Server.Data.Entities;
using BODA.VMS.MLOps.Server.Hubs;
using Microsoft.EntityFrameworkCore;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>레시피 도구 ↔ 모델 버전 바인딩·롤백·동기화 페이로드 (Phase 1 §3, §5, §6.4)</summary>
public sealed class BindingService(MlopsDbContext db, AuditService audit, IMlopsNotifier notifier, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<ModelBindingDto> SetAsync(string recipeId, string toolId, SetBindingRequest req, CurrentUser user, CancellationToken ct)
    {
        recipeId = Required(recipeId, "recipeId");
        toolId = Required(toolId, "toolId");

        Guid modelId;
        Guid? versionId = null;
        if (req.Mode == BindingMode.Pinned)
        {
            if (req.ModelVersionId is null)
                throw ApiException.BadRequest(ErrorCodes.Validation, "Pinned 모드는 modelVersionId 가 필요합니다.");
            var v = await db.ModelVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == req.ModelVersionId, ct)
                    ?? throw ApiException.NotFound("모델 버전");
            if (v.Stage == ModelStage.Retired)
                throw ApiException.Conflict(ErrorCodes.InvalidStageTransition, "Retired 버전은 바인딩할 수 없습니다.");
            modelId = v.ModelId;
            versionId = v.Id;
        }
        else
        {
            modelId = req.ModelId ?? (req.ModelVersionId is not null
                ? (await db.ModelVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == req.ModelVersionId, ct))?.ModelId
                  ?? throw ApiException.NotFound("모델 버전")
                : throw ApiException.BadRequest(ErrorCodes.Validation, "FollowProduction 모드는 modelId 가 필요합니다."));
            if (!await db.Models.AnyAsync(m => m.Id == modelId, ct)) throw ApiException.NotFound("모델");
        }

        var previous = await db.ModelBindings.FirstOrDefaultAsync(b => b.RecipeId == recipeId && b.ToolId == toolId && b.IsActive, ct);
        if (previous is not null) previous.IsActive = false;

        var binding = new ModelBinding
        {
            Id = Guid.NewGuid(), RecipeId = recipeId, ToolId = toolId, ModelId = modelId, ModelVersionId = versionId,
            Mode = req.Mode, BoundBy = user.Name, BoundAt = Now, PreviousBindingId = previous?.Id, IsActive = true, Reason = req.Reason,
        };
        db.ModelBindings.Add(binding);
        audit.Record(AuditService.Model, "Bound", user.Name, binding.Id.ToString(),
            new { recipeId, toolId, req.Mode, modelId, versionId, previousBindingId = previous?.Id, previousVersionId = previous?.ModelVersionId, req.Reason });
        await db.SaveChangesAsync(ct);

        var dto = await ToDtoAsync(binding, ct);
        await notifier.BindingChanged(recipeId, toolId, dto.ResolvedVersionId);
        return dto;
    }

    /// <summary>이전 바인딩의 버전으로 새 바인딩을 만든다 (이력 보존, Phase 1 §5 rollback)</summary>
    public async Task<ModelBindingDto> RollbackAsync(Guid bindingId, RollbackRequest req, CurrentUser user, CancellationToken ct)
    {
        var current = await db.ModelBindings.FirstOrDefaultAsync(b => b.Id == bindingId, ct) ?? throw ApiException.NotFound("바인딩");
        if (!current.IsActive)
            throw ApiException.Conflict(ErrorCodes.Validation, "활성 바인딩만 롤백할 수 있습니다.");
        if (current.PreviousBindingId is null)
            throw ApiException.Conflict(ErrorCodes.Validation, "이전 바인딩이 없어 롤백할 수 없습니다.");
        var previous = await db.ModelBindings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == current.PreviousBindingId, ct)
                       ?? throw ApiException.NotFound("이전 바인딩");

        current.IsActive = false;
        var rolled = new ModelBinding
        {
            Id = Guid.NewGuid(), RecipeId = current.RecipeId, ToolId = current.ToolId,
            ModelId = previous.ModelId, ModelVersionId = previous.ModelVersionId, Mode = previous.Mode,
            BoundBy = user.Name, BoundAt = Now, PreviousBindingId = current.Id, IsActive = true,
            Reason = req.Reason ?? $"롤백 (← {previous.Id})",
        };
        db.ModelBindings.Add(rolled);
        audit.Record(AuditService.Model, "RolledBack", user.Name, rolled.Id.ToString(),
            new { current.RecipeId, current.ToolId, fromBinding = current.Id, toBinding = previous.Id, previous.ModelVersionId, req.Reason });
        await db.SaveChangesAsync(ct);

        var dto = await ToDtoAsync(rolled, ct);
        await notifier.BindingChanged(rolled.RecipeId, rolled.ToolId, dto.ResolvedVersionId);
        return dto;
    }

    public async Task<ModelBindingDto> GetAsync(Guid id, CancellationToken ct)
    {
        var b = await db.ModelBindings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("바인딩");
        return await ToDtoAsync(b, ct);
    }

    /// <summary>활성 바인딩 먼저, 그 다음 이력(최근순)</summary>
    public async Task<List<ModelBindingDto>> ListAsync(string recipeId, CancellationToken ct)
    {
        var list = await db.ModelBindings.AsNoTracking().Where(b => b.RecipeId == recipeId)
            .OrderByDescending(b => b.IsActive).ThenByDescending(b => b.BoundAt).ToListAsync(ct);
        return await ToDtosAsync(list, ct);
    }

    /// <summary>레시피 동기화 JSON 확장 (Phase 1 §6.4) — VMS 가 프리페치에 사용</summary>
    public async Task<RecipeModelBindingsPayload> PayloadAsync(string recipeId, CancellationToken ct)
    {
        var active = await db.ModelBindings.AsNoTracking().Where(b => b.RecipeId == recipeId && b.IsActive).ToListAsync(ct);
        var dtos = await ToDtosAsync(active, ct);
        var entries = dtos.Select(dto => new RecipeModelBindingEntry(dto.ToolId, dto.ModelId, dto.ResolvedVersionId, dto.ResolvedSha256,
            dto.ResolvedNumber, dto.Mode, dto.Reference,
            dto.ResolvedVersionId is null ? null : Mapping.ArtifactUrl(dto.ResolvedVersionId.Value))).ToList();
        return new RecipeModelBindingsPayload(recipeId, entries);
    }

    /// <summary>
    /// 여러 바인딩을 DTO 로 — 모델·고정 버전·Production 버전을 각각 한 번씩만 조회한다.
    /// 행마다 조회하면 이력 50건에 150여 번의 왕복이 된다.
    /// </summary>
    private async Task<List<ModelBindingDto>> ToDtosAsync(IReadOnlyList<ModelBinding> bindings, CancellationToken ct)
    {
        if (bindings.Count == 0) return [];

        var modelIds = bindings.Select(b => b.ModelId).Distinct().ToList();
        var versionIds = bindings.Where(b => b.ModelVersionId is not null).Select(b => b.ModelVersionId!.Value).Distinct().ToList();

        var models = await db.Models.AsNoTracking().Where(m => modelIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.Name, ct);
        var pinned = await db.ModelVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, ct);
        var production = await db.ModelVersions.AsNoTracking()
            .Where(v => modelIds.Contains(v.ModelId) && v.Stage == ModelStage.Production)
            .ToDictionaryAsync(v => v.ModelId, ct);

        return bindings.Select(b =>
        {
            var pin = b.ModelVersionId is not null ? pinned.GetValueOrDefault(b.ModelVersionId.Value) : null;
            var resolved = b.Mode == BindingMode.Pinned ? pin : production.GetValueOrDefault(b.ModelId);
            var reference = new ModelReference(b.ModelId, b.Mode == BindingMode.Pinned ? pin?.Number : null).ToString();
            return new ModelBindingDto(b.Id, b.RecipeId, b.ToolId, b.Mode, b.ModelId, models.GetValueOrDefault(b.ModelId) ?? "(삭제됨)",
                pin?.Id, pin?.Number, pin?.Sha256,
                resolved?.Id, resolved?.Number, resolved?.Sha256,
                b.BoundBy, b.BoundAt, b.PreviousBindingId, b.IsActive, b.Reason, reference);
        }).ToList();
    }

    private async Task<ModelBindingDto> ToDtoAsync(ModelBinding b, CancellationToken ct)
    {
        var model = await db.Models.AsNoTracking().FirstOrDefaultAsync(m => m.Id == b.ModelId, ct);
        ModelVersion? pinned = b.ModelVersionId is null ? null
            : await db.ModelVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == b.ModelVersionId, ct);
        ModelVersion? resolved = b.Mode == BindingMode.Pinned ? pinned
            : await db.ModelVersions.AsNoTracking().Where(v => v.ModelId == b.ModelId && v.Stage == ModelStage.Production)
                .OrderByDescending(v => v.Number).FirstOrDefaultAsync(ct);

        var reference = new ModelReference(b.ModelId, b.Mode == BindingMode.Pinned ? pinned?.Number : null).ToString();
        return new ModelBindingDto(b.Id, b.RecipeId, b.ToolId, b.Mode, b.ModelId, model?.Name ?? "(삭제됨)",
            pinned?.Id, pinned?.Number, pinned?.Sha256,
            resolved?.Id, resolved?.Number, resolved?.Sha256,
            b.BoundBy, b.BoundAt, b.PreviousBindingId, b.IsActive, b.Reason, reference);
    }

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw ApiException.BadRequest(ErrorCodes.Validation, $"{name} 는 필수입니다.") : value.Trim();
}
