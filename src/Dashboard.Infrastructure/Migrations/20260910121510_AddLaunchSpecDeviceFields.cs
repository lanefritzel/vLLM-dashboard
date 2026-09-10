using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dashboard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLaunchSpecDeviceFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DevicesJson",
                table: "LaunchSpecs",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "GpuVendor",
                table: "LaunchSpecs",
                type: "text",
                nullable: false,
                defaultValue: "none");

            migrationBuilder.AddColumn<string>(
                name: "IpcMode",
                table: "LaunchSpecs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NetworkMode",
                table: "LaunchSpecs",
                type: "text",
                nullable: false,
                defaultValue: "bridge");

            migrationBuilder.AddColumn<bool>(
                name: "Privileged",
                table: "LaunchSpecs",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DevicesJson",
                table: "LaunchSpecs");

            migrationBuilder.DropColumn(
                name: "GpuVendor",
                table: "LaunchSpecs");

            migrationBuilder.DropColumn(
                name: "IpcMode",
                table: "LaunchSpecs");

            migrationBuilder.DropColumn(
                name: "NetworkMode",
                table: "LaunchSpecs");

            migrationBuilder.DropColumn(
                name: "Privileged",
                table: "LaunchSpecs");
        }
    }
}
