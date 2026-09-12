using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dashboard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPhysicalServers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PhysicalServerId",
                table: "Sources",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PhysicalServers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DockerHost = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    GpuVendor = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GpuVramTotalGb = table.Column<int>(type: "integer", nullable: true),
                    HfCacheHostPath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    HfToken = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhysicalServers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sources_PhysicalServerId",
                table: "Sources",
                column: "PhysicalServerId");

            migrationBuilder.AddForeignKey(
                name: "FK_Sources_PhysicalServers_PhysicalServerId",
                table: "Sources",
                column: "PhysicalServerId",
                principalTable: "PhysicalServers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Sources_PhysicalServers_PhysicalServerId",
                table: "Sources");

            migrationBuilder.DropTable(
                name: "PhysicalServers");

            migrationBuilder.DropIndex(
                name: "IX_Sources_PhysicalServerId",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "PhysicalServerId",
                table: "Sources");
        }
    }
}
