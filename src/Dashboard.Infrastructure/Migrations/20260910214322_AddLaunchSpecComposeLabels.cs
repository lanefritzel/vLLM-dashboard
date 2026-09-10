using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dashboard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLaunchSpecComposeLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ComposeProject",
                table: "LaunchSpecs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ComposeService",
                table: "LaunchSpecs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ComposeProject",
                table: "LaunchSpecs");

            migrationBuilder.DropColumn(
                name: "ComposeService",
                table: "LaunchSpecs");
        }
    }
}
