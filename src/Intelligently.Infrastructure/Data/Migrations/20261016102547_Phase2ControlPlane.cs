using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intelligently.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase2ControlPlane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Principal_CourseScopeJson",
                table: "AgentRuns",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Principal_RolesJson",
                table: "AgentRuns",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Principal_UserId",
                table: "AgentRuns",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: false,
                defaultValue: "");

            // Phase 1 runs carried only UserId/CourseId; give them the equivalent learner principal.
            migrationBuilder.Sql("""
                UPDATE [AgentRuns]
                SET [Principal_UserId] = [UserId],
                    [Principal_RolesJson] = N'["Learner"]',
                    [Principal_CourseScopeJson] = N'[' + CAST([CourseId] AS nvarchar(20)) + N']'
                WHERE [Principal_UserId] = N'';
                """);

            migrationBuilder.CreateTable(
                name: "AgentApprovals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StepId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToolName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ToolCallJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ArgsHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DecidedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EscalationId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentApprovals_AIEscalations_EscalationId",
                        column: x => x.EscalationId,
                        principalTable: "AIEscalations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AgentApprovals_AgentRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "AgentRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentIdempotencyRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToolName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ArgsHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ToolCallId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsError = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentIdempotencyRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentIdempotencyRecords_AgentRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "AgentRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentApprovals_EscalationId",
                table: "AgentApprovals",
                column: "EscalationId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentApprovals_RunId_CreatedAt",
                table: "AgentApprovals",
                columns: new[] { "RunId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentApprovals_RunId_ToolName_ArgsHash",
                table: "AgentApprovals",
                columns: new[] { "RunId", "ToolName", "ArgsHash" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentApprovals_Status_CreatedAt",
                table: "AgentApprovals",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentIdempotencyRecords_RunId_ToolName_ArgsHash",
                table: "AgentIdempotencyRecords",
                columns: new[] { "RunId", "ToolName", "ArgsHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentApprovals");

            migrationBuilder.DropTable(
                name: "AgentIdempotencyRecords");

            migrationBuilder.DropColumn(
                name: "Principal_CourseScopeJson",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "Principal_RolesJson",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "Principal_UserId",
                table: "AgentRuns");
        }
    }
}
