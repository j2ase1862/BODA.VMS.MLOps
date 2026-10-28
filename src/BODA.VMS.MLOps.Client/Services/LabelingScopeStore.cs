using System.Text.Json;
using Blazored.LocalStorage;

namespace BODA.VMS.MLOps.Client.Services;

/// <summary>
/// [라벨링 시작] 이 라벨링 화면에 넘기는 대상 목록 — 격자에서 고르거나 거른 사진.
/// 목록은 길어서 주소에 싣지 않고 여기 두며, 주소에는 짧은 키만 붙인다(<c>?scope=</c>).
///
/// 데이터셋마다 하나만 둔다 — 새로 시작하면 덮어쓴다. 주소의 키와 맞을 때만 쓰므로
/// 격자 카드에서 바로 연 라벨링이나 예전 주소가 다른 목록을 이어받지 않는다.
/// 새로 고침해도 같은 범위가 남도록 메모리가 아니라 브라우저 저장소에 둔다.
/// </summary>
public sealed class LabelingScopeStore(ILocalStorageService storage)
{
    private sealed record Scope(string Key, Guid[] ImageIds);

    private static string StorageKey(Guid datasetId) => $"mlops.label-scope.{datasetId}";

    /// <summary>범위를 저장하고 주소에 붙일 키를 돌려준다</summary>
    public async Task<string> SaveAsync(Guid datasetId, IEnumerable<Guid> imageIds)
    {
        var key = Guid.NewGuid().ToString("N")[..8];
        await storage.SetItemAsync(StorageKey(datasetId), new Scope(key, imageIds.ToArray()));
        return key;
    }

    /// <summary>키가 맞는 범위. 없거나 다른 시작이 덮어썼으면 null — 그때는 데이터셋 전체를 오간다.</summary>
    public async Task<Guid[]?> LoadAsync(Guid datasetId, string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        try
        {
            var scope = await storage.GetItemAsync<Scope>(StorageKey(datasetId));
            return scope?.Key == key ? scope.ImageIds : null;
        }
        catch (JsonException) { return null; }
    }
}
