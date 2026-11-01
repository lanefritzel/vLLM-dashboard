using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dashboard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeploymentPushTrackingColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastSeenSeq",
                table: "deployments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PendingStopReason",
                table: "deployments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_deployments_pending_stop_reason",
                table: "deployments",
                sql: "\"PendingStopReason\" IN ('swapped','rollback')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_deployments_pending_stop_reason",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "LastSeenSeq",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "PendingStopReason",
                table: "deployments");
        }
    }
}
