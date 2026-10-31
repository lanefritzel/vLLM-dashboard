using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Dashboard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddV2Schema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "servers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    Label = table.Column<string>(type: "text", nullable: false),
                    DockerHost = table.Column<string>(type: "text", nullable: true),
                    GpuName = table.Column<string>(type: "text", nullable: true),
                    GpuCount = table.Column<int>(type: "integer", nullable: false),
                    GpuMemGb = table.Column<int>(type: "integer", nullable: true),
                    ModelsDir = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_servers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "instances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    ServerId = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: false),
                    ProxySlug = table.Column<string>(type: "text", nullable: false),
                    AdminUrl = table.Column<string>(type: "text", nullable: true),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    PollIntervalMs = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_instances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_instances_servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "models",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    ServerId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    SourceType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RepoId = table.Column<string>(type: "text", nullable: true),
                    Path = table.Column<string>(type: "text", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Quantization = table.Column<string>(type: "text", nullable: true),
                    LoadFormat = table.Column<string>(type: "text", nullable: false),
                    Config = table.Column<string>(type: "jsonb", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_models", x => x.Id);
                    table.CheckConstraint("ck_models_source_type", "\"SourceType\" IN ('local','huggingface')");
                    table.CheckConstraint("ck_models_status", "\"Status\" IN ('available','downloading','download_failed')");
                    table.ForeignKey(
                        name: "FK_models_servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recipes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    ServerId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    ServedModelName = table.Column<string>(type: "text", nullable: false),
                    MaxModelLen = table.Column<int>(type: "integer", nullable: false),
                    GpuMemoryUtilization = table.Column<float>(type: "real", nullable: false),
                    KvCacheDtype = table.Column<string>(type: "text", nullable: false),
                    Dtype = table.Column<string>(type: "text", nullable: false),
                    EnforceEager = table.Column<bool>(type: "boolean", nullable: false),
                    MaxNumBatchedTokens = table.Column<int>(type: "integer", nullable: false),
                    BlockSize = table.Column<int>(type: "integer", nullable: false),
                    SpeculativeDecoding = table.Column<bool>(type: "boolean", nullable: false),
                    EnableAutoToolChoice = table.Column<bool>(type: "boolean", nullable: false),
                    ToolCallParser = table.Column<string>(type: "text", nullable: true),
                    ReasoningParser = table.Column<string>(type: "text", nullable: true),
                    ExtraArgs = table.Column<string>(type: "jsonb", nullable: false),
                    ExtraEnv = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recipes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_recipes_servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deployments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    InstanceId = table.Column<int>(type: "integer", nullable: false),
                    ModelId = table.Column<int>(type: "integer", nullable: false),
                    RecipeId = table.Column<int>(type: "integer", nullable: false),
                    ArgsSnapshot = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StopReason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ExitCode = table.Column<int>(type: "integer", nullable: true),
                    LogTail = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StoppedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deployments", x => x.Id);
                    table.CheckConstraint("ck_deployments_failure_reason", "\"FailureReason\" IN ('process_exited','health_timeout','bad_args','out_of_memory')");
                    table.CheckConstraint("ck_deployments_status", "\"Status\" IN ('pending','active','stopped','failed')");
                    table.CheckConstraint("ck_deployments_stop_reason", "\"StopReason\" IN ('swapped','rollback','manual','crash','cancelled')");
                    table.ForeignKey(
                        name: "FK_deployments_instances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_deployments_models_ModelId",
                        column: x => x.ModelId,
                        principalTable: "models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_deployments_recipes_RecipeId",
                        column: x => x.RecipeId,
                        principalTable: "recipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deployment_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    DeploymentId = table.Column<int>(type: "integer", nullable: false),
                    Seq = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    Detail = table.Column<string>(type: "text", nullable: true),
                    EventAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deployment_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_deployment_events_deployments_DeploymentId",
                        column: x => x.DeploymentId,
                        principalTable: "deployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_deployment_events_DeploymentId_Seq",
                table: "deployment_events",
                columns: new[] { "DeploymentId", "Seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_deployments_ModelId",
                table: "deployments",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_deployments_RecipeId",
                table: "deployments",
                column: "RecipeId");

            migrationBuilder.CreateIndex(
                name: "one_active_per_instance",
                table: "deployments",
                column: "InstanceId",
                unique: true,
                filter: "\"Status\" IN ('pending','active')");

            migrationBuilder.CreateIndex(
                name: "IX_instances_ProxySlug",
                table: "instances",
                column: "ProxySlug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_instances_ServerId",
                table: "instances",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_models_ServerId",
                table: "models",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_recipes_ServerId_Name",
                table: "recipes",
                columns: new[] { "ServerId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_servers_Label",
                table: "servers",
                column: "Label",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deployment_events");

            migrationBuilder.DropTable(
                name: "deployments");

            migrationBuilder.DropTable(
                name: "instances");

            migrationBuilder.DropTable(
                name: "models");

            migrationBuilder.DropTable(
                name: "recipes");

            migrationBuilder.DropTable(
                name: "servers");
        }
    }
}
