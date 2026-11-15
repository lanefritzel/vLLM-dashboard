using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dashboard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ModelNameSnapshotAndNullableModelId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_deployments_models_ModelId",
                table: "deployments");

            migrationBuilder.AlterColumn<int>(
                name: "ModelId",
                table: "deployments",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<string>(
                name: "ModelName",
                table: "deployments",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Backfill existing rows from the still-live model before the column's
            // only other source (SwapService, going forward) exists -- without this,
            // every pre-existing deployment would show a blank model name forever.
            migrationBuilder.Sql(
                "UPDATE deployments d SET \"ModelName\" = m.\"Name\" " +
                "FROM models m WHERE m.\"Id\" = d.\"ModelId\";");

            migrationBuilder.AddForeignKey(
                name: "FK_deployments_models_ModelId",
                table: "deployments",
                column: "ModelId",
                principalTable: "models",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_deployments_models_ModelId",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "ModelName",
                table: "deployments");

            migrationBuilder.AlterColumn<int>(
                name: "ModelId",
                table: "deployments",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_deployments_models_ModelId",
                table: "deployments",
                column: "ModelId",
                principalTable: "models",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
