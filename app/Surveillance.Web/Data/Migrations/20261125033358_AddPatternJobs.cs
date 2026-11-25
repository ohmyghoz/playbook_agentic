using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Surveillance.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPatternJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentName",
                table: "Events",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JobJson",
                table: "Cases",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StartedBy",
                table: "Cases",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StartedByUser",
                table: "Cases",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UseCase",
                table: "Cases",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Cases_UseCase_CreatedAt",
                table: "Cases",
                columns: new[] { "UseCase", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Cases_UseCase_CreatedAt",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "AgentName",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "JobJson",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "StartedBy",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "StartedByUser",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "UseCase",
                table: "Cases");
        }
    }
}
