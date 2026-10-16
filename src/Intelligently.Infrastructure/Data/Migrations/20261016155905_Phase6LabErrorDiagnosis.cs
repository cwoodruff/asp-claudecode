using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intelligently.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase6LabErrorDiagnosis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FinalExplanation",
                table: "AgentRuns",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FixDiff",
                table: "AgentRuns",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Rule",
                table: "AgentApprovals",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuleKind",
                table: "AgentApprovals",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FinalExplanation",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "FixDiff",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "Rule",
                table: "AgentApprovals");

            migrationBuilder.DropColumn(
                name: "RuleKind",
                table: "AgentApprovals");
        }
    }
}
