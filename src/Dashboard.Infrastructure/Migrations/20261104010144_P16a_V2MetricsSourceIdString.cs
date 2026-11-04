using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dashboard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class P16a_V2MetricsSourceIdString : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MetricsRollups_Sources_SourceId",
                table: "MetricsRollups");

            migrationBuilder.DropForeignKey(
                name: "FK_MetricsSamples_Sources_SourceId",
                table: "MetricsSamples");

            migrationBuilder.AlterColumn<string>(
                name: "SourceId",
                table: "MetricsSamples",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "SourceId",
                table: "MetricsRollups",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "SourceId",
                table: "MetricsSamples",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SourceId",
                table: "MetricsRollups",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AddForeignKey(
                name: "FK_MetricsRollups_Sources_SourceId",
                table: "MetricsRollups",
                column: "SourceId",
                principalTable: "Sources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MetricsSamples_Sources_SourceId",
                table: "MetricsSamples",
                column: "SourceId",
                principalTable: "Sources",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
