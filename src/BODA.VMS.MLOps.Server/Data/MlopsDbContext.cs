using BODA.VMS.MLOps.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BODA.VMS.MLOps.Server.Data;

public class MlopsDbContext(DbContextOptions<MlopsDbContext> options) : DbContext(options)
{
    public DbSet<Model> Models => Set<Model>();
    public DbSet<ModelVersion> ModelVersions => Set<ModelVersion>();
    public DbSet<ModelStageHistory> ModelStageHistories => Set<ModelStageHistory>();
    public DbSet<ModelBinding> ModelBindings => Set<ModelBinding>();
    public DbSet<Worker> Workers => Set<Worker>();
    public DbSet<TrainingJob> TrainingJobs => Set<TrainingJob>();
    public DbSet<JobLogChunk> JobLogChunks => Set<JobLogChunk>();
    public DbSet<JobArtifact> JobArtifacts => Set<JobArtifact>();
    public DbSet<PretrainedAsset> PretrainedAssets => Set<PretrainedAsset>();
    public DbSet<DatasetVersion> DatasetVersions => Set<DatasetVersion>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Dataset> Datasets => Set<Dataset>();
    public DbSet<Image> Images => Set<Image>();
    public DbSet<DatasetImage> DatasetImages => Set<DatasetImage>();
    public DbSet<Annotation> Annotations => Set<Annotation>();
    public DbSet<ImageLabelState> ImageLabelStates => Set<ImageLabelState>();

    /// <summary>
    /// 동시성 토큰 갱신. EF 는 UPDATE 의 WHERE 에 원래 Stamp 를 넣으므로, 그 사이 다른 요청이 같은 행을 고쳤다면
    /// 0 행이 갱신되어 <see cref="DbUpdateConcurrencyException"/> 이 난다. 호출 측이 이를 409 나 재시도로 처리한다.
    /// </summary>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<IConcurrencyStamped>())
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Entity.Stamp = Guid.NewGuid();
        return base.SaveChangesAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Model>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.TaskType).HasConversion<string>();
            e.HasIndex(x => x.Name);
            e.HasMany(x => x.Versions).WithOne(v => v.Model).HasForeignKey(v => v.ModelId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ModelVersion>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.Format).HasConversion<string>();
            e.Property(x => x.Source).HasConversion<string>();
            e.Property(x => x.Stage).HasConversion<string>();
            e.HasIndex(x => new { x.ModelId, x.Number }).IsUnique();
            // 해시 유일 범위는 Model 계열 안 (Phase 1 §3 의 "Sha256 유일" 을 계열 범위로 좁힘).
            // 전역 유일로 두면 다른 계열에 같은 파일을 올릴 때 남의 계열 버전이 반환되어 정보가 새고,
            // 그 계열의 작업 유형·클래스 검증도 건너뛰게 된다. 아티팩트 파일 자체는 여전히 sha 경로로 한 벌만 저장된다.
            e.HasIndex(x => new { x.ModelId, x.Sha256 }).IsUnique();
            e.HasIndex(x => x.Sha256);
            e.HasIndex(x => x.TrainingJobId);
            e.HasMany(x => x.StageHistory).WithOne(h => h.ModelVersion).HasForeignKey(h => h.ModelVersionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ModelStageHistory>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FromStage).HasConversion<string>();
            e.Property(x => x.ToStage).HasConversion<string>();
        });

        b.Entity<ModelBinding>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.RecipeId).HasMaxLength(200).IsRequired();
            e.Property(x => x.ToolId).HasMaxLength(200).IsRequired();
            e.Property(x => x.Mode).HasConversion<string>();
            e.HasIndex(x => new { x.RecipeId, x.ToolId, x.IsActive });
            e.HasIndex(x => x.ModelVersionId);
        });

        b.Entity<Worker>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Stamp).IsConcurrencyToken();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasConversion<string>();
            e.HasIndex(x => x.TokenHash).IsUnique();
        });

        b.Entity<TrainingJob>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Stamp).IsConcurrencyToken();
            e.Property(x => x.TaskType).HasConversion<string>();
            e.Property(x => x.Script).HasConversion<string>();
            e.Property(x => x.State).HasConversion<string>();
            e.HasIndex(x => new { x.State, x.Priority, x.CreatedAt });
            e.HasIndex(x => x.WorkerId);
            e.HasIndex(x => x.DedupKey);
        });

        b.Entity<JobLogChunk>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Level).HasConversion<string>();
            e.HasIndex(x => new { x.JobId, x.Seq }).IsUnique();
        });

        b.Entity<JobArtifact>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>();
            e.HasIndex(x => x.JobId);
        });

        b.Entity<PretrainedAsset>(e =>
        {
            e.HasKey(x => x.Ref);
            e.Property(x => x.Ref).HasMaxLength(200);
        });

        b.Entity<DatasetVersion>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TaskType).HasConversion<string>();
            e.Property(x => x.Source).HasConversion<string>();
            e.HasIndex(x => x.ManifestHash);
            e.HasIndex(x => x.DatasetId);
        });

        b.Entity<Dataset>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.TaskType).HasConversion<string>();
            e.HasIndex(x => x.Name);
        });

        b.Entity<Image>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.Source).HasConversion<string>();
            // 같은 파일은 한 벌만 — 재업로드는 기존 레코드를 돌려준다
            e.HasIndex(x => x.Sha256).IsUnique();
            e.HasIndex(x => x.PerceptualHash);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.InspectionId);
        });

        b.Entity<DatasetImage>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Split).HasConversion<string>();
            e.HasIndex(x => new { x.DatasetId, x.ImageId }).IsUnique();
            e.HasIndex(x => x.ImageId);
        });

        b.Entity<Annotation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Shape).HasConversion<string>();
            e.Property(x => x.ClassName).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.DatasetId, x.ImageId });
        });

        b.Entity<ImageLabelState>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Stamp).IsConcurrencyToken();
            e.Property(x => x.Status).HasConversion<string>();
            e.HasIndex(x => new { x.DatasetId, x.ImageId }).IsUnique();
            // 라벨링 큐는 (데이터셋, 상태) 로 훑고 불확실도 순으로 정렬한다
            e.HasIndex(x => new { x.DatasetId, x.Status });
        });

        b.Entity<AuditLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Category, x.At });
        });
    }
}
