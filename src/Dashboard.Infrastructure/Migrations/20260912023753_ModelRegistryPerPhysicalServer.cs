using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dashboard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ModelRegistryPerPhysicalServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Models_Sources_SourceId",
                table: "Models");

            migrationBuilder.DropIndex(
                name: "IX_Models_SourceId_Name",
                table: "Models");

            migrationBuilder.AddColumn<Guid>(
                name: "ActiveModelId",
                table: "Sources",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PreloadModelId",
                table: "Sources",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PhysicalServerId",
                table: "Models",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sources_ActiveModelId",
                table: "Sources",
                column: "ActiveModelId");

            migrationBuilder.CreateIndex(
                name: "IX_Sources_PreloadModelId",
                table: "Sources",
                column: "PreloadModelId");

            migrationBuilder.CreateIndex(
                name: "IX_Models_PhysicalServerId_Name",
                table: "Models",
                columns: new[] { "PhysicalServerId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Models_PhysicalServers_PhysicalServerId",
                table: "Models",
                column: "PhysicalServerId",
                principalTable: "PhysicalServers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Sources_Models_ActiveModelId",
                table: "Sources",
                column: "ActiveModelId",
                principalTable: "Models",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Sources_Models_PreloadModelId",
                table: "Sources",
                column: "PreloadModelId",
                principalTable: "Models",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Models_PhysicalServers_PhysicalServerId",
                table: "Models");

            migrationBuilder.DropForeignKey(
                name: "FK_Sources_Models_ActiveModelId",
                table: "Sources");

            migrationBuilder.DropForeignKey(
                name: "FK_Sources_Models_PreloadModelId",
                table: "Sources");

            migrationBuilder.DropIndex(
                name: "IX_Sources_ActiveModelId",
                table: "Sources");

            migrationBuilder.DropIndex(
                name: "IX_Sources_PreloadModelId",
                table: "Sources");

            migrationBuilder.DropIndex(
                name: "IX_Models_PhysicalServerId_Name",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "ActiveModelId",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "PreloadModelId",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "PhysicalServerId",
                table: "Models");

            migrationBuilder.CreateIndex(
                name: "IX_Models_SourceId_Name",
                table: "Models",
                columns: new[] { "SourceId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Models_Sources_SourceId",
                table: "Models",
                column: "SourceId",
                principalTable: "Sources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
