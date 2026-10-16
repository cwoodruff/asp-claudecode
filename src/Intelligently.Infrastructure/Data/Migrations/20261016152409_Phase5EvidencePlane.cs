using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intelligently.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase5EvidencePlane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CostUsd",
                table: "AgentTraces",
                type: "decimal(12,6)",
                precision: 12,
                scale: 6,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Effort",
                table: "AgentTraces",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LatencyMs",
                table: "AgentTraces",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReasoningSummary",
                table: "AgentTraces",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StopReason",
                table: "AgentTraces",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RootSpanId",
                table: "AgentRuns",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraceId",
                table: "AgentRuns",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentTraces_StepId_Kind",
                table: "AgentTraces",
                columns: new[] { "StepId", "Kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentTraces_StepId_Kind",
                table: "AgentTraces");

            migrationBuilder.DropColumn(
                name: "CostUsd",
                table: "AgentTraces");

            migrationBuilder.DropColumn(
                name: "Effort",
                table: "AgentTraces");

            migrationBuilder.DropColumn(
                name: "LatencyMs",
                table: "AgentTraces");

            migrationBuilder.DropColumn(
                name: "ReasoningSummary",
                table: "AgentTraces");

            migrationBuilder.DropColumn(
                name: "StopReason",
                table: "AgentTraces");

            migrationBuilder.DropColumn(
                name: "RootSpanId",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "TraceId",
                table: "AgentRuns");
        }
    }
}
