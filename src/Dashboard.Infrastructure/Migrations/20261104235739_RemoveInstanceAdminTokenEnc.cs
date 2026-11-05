using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dashboard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveInstanceAdminTokenEnc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdminTokenEnc",
                table: "instances");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdminTokenEnc",
                table: "instances",
                type: "text",
                nullable: true);
        }
    }
}
