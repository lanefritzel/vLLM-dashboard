using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dashboard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddModelDrivenSwapFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImageOverride",
                table: "Models",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelPathOrRepoId",
                table: "Models",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ModelSource",
                table: "Models",
                type: "text",
                nullable: false,
                defaultValue: "huggingface");

            migrationBuilder.AddColumn<string>(
                name: "OptionsJson",
                table: "Models",
                type: "text",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "Entrypoint",
                table: "LaunchSpecs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HealthWaitTimeoutSeconds",
                table: "LaunchSpecs",
                type: "integer",
                nullable: false,
                defaultValue: 600);

            migrationBuilder.AddColumn<string>(
                name: "HfCacheHostPath",
                table: "LaunchSpecs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HfToken",
                table: "LaunchSpecs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ServingPort",
                table: "LaunchSpecs",
                type: "integer",
                nullable: true);

            // Backfill: ModelPathOrRepoId supersedes the old free-text SwapArgs field.
            // Existing rows used SwapArgs ?? Name as the --model value at swap time
            // (see the pre-redesign DockerOrchestrator.SwapModelAsync), so that's the
            // closest equivalent to carry forward rather than leaving it blank.
            migrationBuilder.Sql(
                "UPDATE \"Models\" SET \"ModelPathOrRepoId\" = COALESCE(NULLIF(\"SwapArgs\", ''), \"Name\") " +
                "WHERE \"ModelPathOrRepoId\" = ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImageOverride",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "ModelPathOrRepoId",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "ModelSource",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "OptionsJson",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "Entrypoint",
                table: "LaunchSpecs");

            migrationBuilder.DropColumn(
                name: "HealthWaitTimeoutSeconds",
                table: "LaunchSpecs");

            migrationBuilder.DropColumn(
                name: "HfCacheHostPath",
                table: "LaunchSpecs");

            migrationBuilder.DropColumn(
                name: "HfToken",
                table: "LaunchSpecs");

            migrationBuilder.DropColumn(
                name: "ServingPort",
                table: "LaunchSpecs");
        }
    }
}
