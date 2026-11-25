using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Surveillance.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRetentionMarkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EventsPurgedAt",
                table: "Cases",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TokensCompactedAt",
                table: "Cases",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EventsPurgedAt",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "TokensCompactedAt",
                table: "Cases");
        }
    }
}
