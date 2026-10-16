using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intelligently.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRunLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClaimedBy",
                table: "AgentRuns",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedUntil",
                table: "AgentRuns",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimedBy",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "ClaimedUntil",
                table: "AgentRuns");
        }
    }
}
