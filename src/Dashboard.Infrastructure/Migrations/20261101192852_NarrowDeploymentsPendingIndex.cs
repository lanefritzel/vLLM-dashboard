using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dashboard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NarrowDeploymentsPendingIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "one_active_per_instance",
                table: "deployments");

            migrationBuilder.CreateIndex(
                name: "one_pending_per_instance",
                table: "deployments",
                column: "InstanceId",
                unique: true,
                filter: "\"Status\" = 'pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "one_pending_per_instance",
                table: "deployments");

            migrationBuilder.CreateIndex(
                name: "one_active_per_instance",
                table: "deployments",
                column: "InstanceId",
                unique: true,
                filter: "\"Status\" IN ('pending','active')");
        }
    }
}
