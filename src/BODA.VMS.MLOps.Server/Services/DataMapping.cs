using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Core.Labeling;
using BODA.VMS.MLOps.Server.Data.Entities;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>데이터 관리·라벨링 쪽 엔티티 ↔ DTO</summary>
public static class DataMapping
{
    public static DatasetDto ToDto(this Dataset d, DatasetService.DatasetStats? stats = null) =>
        new(d.Id, d.Name, d.TaskType, Mapping.Json(d.ClassesJson, Array.Empty<string>()), d.Description,
            d.CreatedBy, d.CreatedAt, d.IsArchived,
            stats is null ? null : new DatasetStatsDto(stats.ImageCount, stats.Labeled, stats.Reviewed, stats.Unlabeled,
                stats.AnnotationCount, stats.TrainCount, stats.ValCount, stats.TestCount, stats.PerClass));

    public static ImageDto ToDto(this Image i,
        LabelStatus? labelStatus = null, DatasetSplit? split = null, int? annotationCount = null, string? lockedBy = null,
        IReadOnlyList<AnnotationDto>? annotations = null) =>
        new(i.Id, i.Sha256, i.FileName, i.ContentType, i.Width, i.Height, i.SizeBytes,
            i.Source, i.LineId, i.InspectionId, Mapping.Json(i.TagsJson, Array.Empty<string>()), i.PerceptualHash,
            i.CapturedAt, i.CreatedBy, i.CreatedAt,
            ImageUrl(i.Id, "thumb"), ImageUrl(i.Id, "view"), ImageUrl(i.Id, "original"),
            labelStatus, split, annotationCount, lockedBy, annotations);

    public static string ImageUrl(Guid imageId, string variant) => $"/api/images/{imageId}/{variant}";

    public static ImageLabelsDto ToDto(this LabelingService.ImageLabels labels) =>
        new(labels.DatasetId, labels.ImageId, labels.Annotations.Select(ToDto).ToList(),
            labels.Status, labels.LockedBy, labels.LockExpiresAt, labels.LockedByMe,
            labels.LabeledBy, labels.LabeledAt, labels.ReviewedBy, labels.ReviewedAt);

    public static AnnotationDto ToDto(this LabelAnnotation a) =>
        new(a.Shape, a.ClassName,
            a.Box?.X, a.Box?.Y, a.Box?.Width, a.Box?.Height,
            a.Polygon?.Select(p => new[] { p.X, p.Y }).ToArray(),
            a.Text);

    public static LabelAnnotation ToDomain(this AnnotationDto d) => new()
    {
        Shape = d.Shape,
        ClassName = d.ClassName?.Trim() ?? "",
        Box = d is { X: not null, Y: not null, W: not null, H: not null }
            ? new NormBox(d.X.Value, d.Y.Value, d.W.Value, d.H.Value)
            : null,
        Polygon = d.Points?.Where(p => p.Length >= 2).Select(p => new NormPoint(p[0], p[1])).ToList(),
        Text = d.Text,
    };
}
