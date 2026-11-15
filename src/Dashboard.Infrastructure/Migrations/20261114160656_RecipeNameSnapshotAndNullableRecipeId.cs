using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dashboard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RecipeNameSnapshotAndNullableRecipeId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_deployments_recipes_RecipeId",
                table: "deployments");

            migrationBuilder.AlterColumn<int>(
                name: "RecipeId",
                table: "deployments",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<string>(
                name: "RecipeName",
                table: "deployments",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Backfill existing rows from the still-live recipe before the column's
            // only other source (SwapService, going forward) exists -- without this,
            // every pre-existing deployment would show a blank recipe name forever.
            migrationBuilder.Sql(
                "UPDATE deployments d SET \"RecipeName\" = r.\"Name\" " +
                "FROM recipes r WHERE r.\"Id\" = d.\"RecipeId\";");

            migrationBuilder.AddForeignKey(
                name: "FK_deployments_recipes_RecipeId",
                table: "deployments",
                column: "RecipeId",
                principalTable: "recipes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_deployments_recipes_RecipeId",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "RecipeName",
                table: "deployments");

            migrationBuilder.AlterColumn<int>(
                name: "RecipeId",
                table: "deployments",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_deployments_recipes_RecipeId",
                table: "deployments",
                column: "RecipeId",
                principalTable: "recipes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
