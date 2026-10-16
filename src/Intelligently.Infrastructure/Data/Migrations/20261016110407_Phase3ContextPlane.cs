using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intelligently.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase3ContextPlane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PromptTokens",
                table: "AgentSteps",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseContentJson",
                table: "AgentSteps",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseContentJson",
                table: "AgentPlans",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingJson",
                table: "AgentEpisodes",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaskText",
                table: "AgentEpisodes",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_AgentEpisodes_TaskType_CreatedAt",
                table: "AgentEpisodes",
                columns: new[] { "TaskType", "CreatedAt" });

            // Full-text retrieval for KnowledgeRetriever. Only where the SQL Server full-text component is
            // installed (LocalDB and SQLite are not); elsewhere the retriever falls back to LIKE. Full-text DDL
            // cannot run inside the migration transaction.
            migrationBuilder.Sql("""
                IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'IntelligentlyKnowledgeCatalog')
                        EXEC(N'CREATE FULLTEXT CATALOG [IntelligentlyKnowledgeCatalog]');
                    IF NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'dbo.AIKnowledgeEntries'))
                        EXEC(N'CREATE FULLTEXT INDEX ON [dbo].[AIKnowledgeEntries] ([Question], [Answer], [Tags])
                               KEY INDEX [PK_AIKnowledgeEntries] ON [IntelligentlyKnowledgeCatalog] WITH CHANGE_TRACKING AUTO');
                END
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'dbo.AIKnowledgeEntries'))
                    EXEC(N'DROP FULLTEXT INDEX ON [dbo].[AIKnowledgeEntries]');
                IF EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'IntelligentlyKnowledgeCatalog')
                    EXEC(N'DROP FULLTEXT CATALOG [IntelligentlyKnowledgeCatalog]');
                """, suppressTransaction: true);
            migrationBuilder.DropIndex(
                name: "IX_AgentEpisodes_TaskType_CreatedAt",
                table: "AgentEpisodes");

            migrationBuilder.DropColumn(
                name: "PromptTokens",
                table: "AgentSteps");

            migrationBuilder.DropColumn(
                name: "ResponseContentJson",
                table: "AgentSteps");

            migrationBuilder.DropColumn(
                name: "ResponseContentJson",
                table: "AgentPlans");

            migrationBuilder.DropColumn(
                name: "EmbeddingJson",
                table: "AgentEpisodes");

            migrationBuilder.DropColumn(
                name: "TaskText",
                table: "AgentEpisodes");
        }
    }
}
