using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Surveillance.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddToolResultPreviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ToolResultPreviews",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ToolCallId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ToolName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Preview = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolResultPreviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ToolResultPreviews_Cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "Cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ToolResultPreviews_CaseId_ToolCallId",
                table: "ToolResultPreviews",
                columns: new[] { "CaseId", "ToolCallId" });

            migrationBuilder.CreateIndex(
                name: "IX_ToolResultPreviews_RunId_ToolCallId",
                table: "ToolResultPreviews",
                columns: new[] { "RunId", "ToolCallId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ToolResultPreviews");
        }
    }
}
