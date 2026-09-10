using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Dashboard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMetricsTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MetricsUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    InferenceUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DockerHost = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ContainerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ScrapeIntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LaunchSpecs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Image = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ModelArgTemplate = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    PortsJson = table.Column<string>(type: "text", nullable: false),
                    EnvJson = table.Column<string>(type: "text", nullable: false),
                    VolumesJson = table.Column<string>(type: "text", nullable: false),
                    RestartPolicy = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ExtraRunArgs = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    GpuOptions = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LaunchSpecs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LaunchSpecs_Sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MetricsRollups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BucketStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Samples = table.Column<int>(type: "integer", nullable: false),
                    AvgRunning = table.Column<double>(type: "double precision", nullable: true),
                    AvgWaiting = table.Column<double>(type: "double precision", nullable: true),
                    AvgKvPct = table.Column<double>(type: "double precision", nullable: true),
                    MaxKvPct = table.Column<double>(type: "double precision", nullable: true),
                    TokensGenerated = table.Column<long>(type: "bigint", nullable: false),
                    TokensPrompt = table.Column<long>(type: "bigint", nullable: false),
                    RequestsCompleted = table.Column<long>(type: "bigint", nullable: false),
                    P50E2eSec = table.Column<double>(type: "double precision", nullable: true),
                    P95E2eSec = table.Column<double>(type: "double precision", nullable: true),
                    P99E2eSec = table.Column<double>(type: "double precision", nullable: true),
                    P50TtftSec = table.Column<double>(type: "double precision", nullable: true),
                    P95TtftSec = table.Column<double>(type: "double precision", nullable: true),
                    AvgGenTps = table.Column<double>(type: "double precision", nullable: true),
                    PeakGenTps = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricsRollups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetricsRollups_Sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MetricsSamples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Ts = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ModelName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Running = table.Column<int>(type: "integer", nullable: false),
                    Waiting = table.Column<int>(type: "integer", nullable: false),
                    KvPct = table.Column<double>(type: "double precision", precision: 5, scale: 2, nullable: true),
                    GenTps = table.Column<double>(type: "double precision", precision: 12, scale: 4, nullable: true),
                    PromptTps = table.Column<double>(type: "double precision", precision: 12, scale: 4, nullable: true),
                    ReqTps = table.Column<double>(type: "double precision", precision: 12, scale: 4, nullable: true),
                    TtftAvgSec = table.Column<double>(type: "double precision", precision: 12, scale: 4, nullable: true),
                    ItlAvgSec = table.Column<double>(type: "double precision", precision: 12, scale: 4, nullable: true),
                    E2eAvgSec = table.Column<double>(type: "double precision", precision: 12, scale: 4, nullable: true),
                    QueueAvgSec = table.Column<double>(type: "double precision", precision: 12, scale: 4, nullable: true),
                    PrefixHitRatio = table.Column<double>(type: "double precision", precision: 5, scale: 4, nullable: true),
                    PreemptionsTotal = table.Column<long>(type: "bigint", nullable: true),
                    Up = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricsSamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetricsSamples_Sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Models",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SwapArgs = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Models", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Models_Sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LaunchSpecs_SourceId",
                table: "LaunchSpecs",
                column: "SourceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetricsRollups_SourceId_ModelName_BucketStart",
                table: "MetricsRollups",
                columns: new[] { "SourceId", "ModelName", "BucketStart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetricsSamples_SourceId_Ts",
                table: "MetricsSamples",
                columns: new[] { "SourceId", "Ts" });

            migrationBuilder.CreateIndex(
                name: "IX_MetricsSamples_Ts",
                table: "MetricsSamples",
                column: "Ts");

            migrationBuilder.CreateIndex(
                name: "IX_Models_SourceId_Name",
                table: "Models",
                columns: new[] { "SourceId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LaunchSpecs");

            migrationBuilder.DropTable(
                name: "MetricsRollups");

            migrationBuilder.DropTable(
                name: "MetricsSamples");

            migrationBuilder.DropTable(
                name: "Models");

            migrationBuilder.DropTable(
                name: "Sources");
        }
    }
}
