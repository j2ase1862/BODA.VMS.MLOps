using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BODA.VMS.MLOps.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", nullable: true),
                    DetailsJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DatasetVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    TaskType = table.Column<string>(type: "TEXT", nullable: false),
                    ExportFormat = table.Column<string>(type: "TEXT", nullable: false),
                    ManifestHash = table.Column<string>(type: "TEXT", nullable: false),
                    StorageKey = table.Column<string>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ImageCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ClassesJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatasetVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    StorageKey = table.Column<string>(type: "TEXT", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobArtifacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobLogChunks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Seq = table.Column<long>(type: "INTEGER", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobLogChunks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModelBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipeId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ToolId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ModelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModelVersionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    BoundBy = table.Column<string>(type: "TEXT", nullable: false),
                    BoundAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PreviousBindingId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelBindings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Models",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TaskType = table.Column<string>(type: "TEXT", nullable: false),
                    ClassesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Models", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PretrainedAssets",
                columns: table => new
                {
                    Ref = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FilesJson = table.Column<string>(type: "TEXT", nullable: false),
                    License = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: true),
                    AddedBy = table.Column<string>(type: "TEXT", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PretrainedAssets", x => x.Ref);
                });

            migrationBuilder.CreateTable(
                name: "TrainingJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Stamp = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<string>(type: "TEXT", nullable: true),
                    DatasetVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskType = table.Column<string>(type: "TEXT", nullable: false),
                    Script = table.Column<string>(type: "TEXT", nullable: false),
                    Backbone = table.Column<string>(type: "TEXT", nullable: true),
                    PretrainedRef = table.Column<string>(type: "TEXT", nullable: true),
                    HyperparamsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Seed = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AckedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Progress = table.Column<double>(type: "REAL", nullable: false),
                    CurrentEpoch = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalEpochs = table.Column<int>(type: "INTEGER", nullable: false),
                    LastLoss = table.Column<double>(type: "REAL", nullable: true),
                    LastMetric = table.Column<double>(type: "REAL", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    FailureKind = table.Column<string>(type: "TEXT", nullable: true),
                    ResultModelVersionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReproducibilityJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CancelRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    CancelReason = table.Column<string>(type: "TEXT", nullable: true),
                    ScriptSha256 = table.Column<string>(type: "TEXT", nullable: true),
                    DedupKey = table.Column<string>(type: "TEXT", nullable: false),
                    LastProgressAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LogSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    License = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrainingJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Workers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Stamp = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    MachineName = table.Column<string>(type: "TEXT", nullable: true),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    AdminDisabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisabledReason = table.Column<string>(type: "TEXT", nullable: true),
                    LastHeartbeatAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CapabilitiesJson = table.Column<string>(type: "TEXT", nullable: true),
                    TaskTypesJson = table.Column<string>(type: "TEXT", nullable: false),
                    MaxConcurrent = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    WorkerVersion = table.Column<string>(type: "TEXT", nullable: true),
                    DiskFreeGB = table.Column<double>(type: "REAL", nullable: false),
                    GpuMemFreeMB = table.Column<int>(type: "INTEGER", nullable: false),
                    ProtocolVersion = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModelVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ArtifactKey = table.Column<string>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Format = table.Column<string>(type: "TEXT", nullable: false),
                    InputSize = table.Column<int>(type: "INTEGER", nullable: true),
                    ClassesJson = table.Column<string>(type: "TEXT", nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: false),
                    MetricsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    TrainingJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    License = table.Column<string>(type: "TEXT", nullable: true),
                    Stage = table.Column<string>(type: "TEXT", nullable: false),
                    StageChangedBy = table.Column<string>(type: "TEXT", nullable: true),
                    StageChangedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ValidationStatus = table.Column<string>(type: "TEXT", nullable: true),
                    WarningsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModelVersions_Models_ModelId",
                        column: x => x.ModelId,
                        principalTable: "Models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ModelStageHistories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModelVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromStage = table.Column<string>(type: "TEXT", nullable: false),
                    ToStage = table.Column<string>(type: "TEXT", nullable: false),
                    ChangedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelStageHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModelStageHistories_ModelVersions_ModelVersionId",
                        column: x => x.ModelVersionId,
                        principalTable: "ModelVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Category_At",
                table: "AuditLogs",
                columns: new[] { "Category", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_DatasetVersions_ManifestHash",
                table: "DatasetVersions",
                column: "ManifestHash");

            migrationBuilder.CreateIndex(
                name: "IX_JobArtifacts_JobId",
                table: "JobArtifacts",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_JobLogChunks_JobId_Seq",
                table: "JobLogChunks",
                columns: new[] { "JobId", "Seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModelBindings_ModelVersionId",
                table: "ModelBindings",
                column: "ModelVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_ModelBindings_RecipeId_ToolId_IsActive",
                table: "ModelBindings",
                columns: new[] { "RecipeId", "ToolId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Models_Name",
                table: "Models",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_ModelStageHistories_ModelVersionId",
                table: "ModelStageHistories",
                column: "ModelVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_ModelVersions_ModelId_Number",
                table: "ModelVersions",
                columns: new[] { "ModelId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModelVersions_ModelId_Sha256",
                table: "ModelVersions",
                columns: new[] { "ModelId", "Sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModelVersions_Sha256",
                table: "ModelVersions",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_ModelVersions_TrainingJobId",
                table: "ModelVersions",
                column: "TrainingJobId");

            migrationBuilder.CreateIndex(
                name: "IX_TrainingJobs_DedupKey",
                table: "TrainingJobs",
                column: "DedupKey");

            migrationBuilder.CreateIndex(
                name: "IX_TrainingJobs_State_Priority_CreatedAt",
                table: "TrainingJobs",
                columns: new[] { "State", "Priority", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TrainingJobs_WorkerId",
                table: "TrainingJobs",
                column: "WorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_Workers_TokenHash",
                table: "Workers",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "DatasetVersions");

            migrationBuilder.DropTable(
                name: "JobArtifacts");

            migrationBuilder.DropTable(
                name: "JobLogChunks");

            migrationBuilder.DropTable(
                name: "ModelBindings");

            migrationBuilder.DropTable(
                name: "ModelStageHistories");

            migrationBuilder.DropTable(
                name: "PretrainedAssets");

            migrationBuilder.DropTable(
                name: "TrainingJobs");

            migrationBuilder.DropTable(
                name: "Workers");

            migrationBuilder.DropTable(
                name: "ModelVersions");

            migrationBuilder.DropTable(
                name: "Models");
        }
    }
}
