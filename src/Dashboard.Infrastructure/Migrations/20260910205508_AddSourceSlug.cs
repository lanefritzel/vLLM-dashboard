using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dashboard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceSlug : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Added nullable first: existing rows have no slug yet, and backfilling
            // them from Label (below) happens before the NOT NULL + unique constraints
            // are applied, so a straight "add NOT NULL with a blank default" can't be
            // used here — every existing row would collide on "" under the unique index.
            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "Sources",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            // Same normalization as Dashboard.Core.Models.Slug.From: lowercase, run of
            // non-alphanumerics collapsed to one hyphen, leading/trailing hyphens trimmed.
            migrationBuilder.Sql("""
                UPDATE "Sources"
                SET "Slug" = NULLIF(trim(both '-' from regexp_replace(lower("Label"), '[^a-z0-9]+', '-', 'g')), '')
                WHERE "Slug" IS NULL;
                """);
            migrationBuilder.Sql("""UPDATE "Sources" SET "Slug" = 'source' WHERE "Slug" IS NULL;""");

            // Existing labels could collide once normalized (e.g. two sources both
            // reducing to "vllm") — de-duplicate before the unique index is added, the
            // same way SourceCatalog.EnsureUniqueSlugAsync does for new writes going
            // forward.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "Slug" ORDER BY "CreatedAt", "Id") AS rn
                    FROM "Sources"
                )
                UPDATE "Sources" s
                SET "Slug" = s."Slug" || '-' || ranked.rn
                FROM ranked
                WHERE ranked."Id" = s."Id" AND ranked.rn > 1;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Slug",
                table: "Sources",
                type: "character varying(60)",
                maxLength: 60,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(60)",
                oldMaxLength: 60,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sources_Slug",
                table: "Sources",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sources_Slug",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "Sources");
        }
    }
}
