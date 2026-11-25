using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Surveillance.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovalClaimsAndAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ApprovalTimeoutMinutes",
                table: "Cases",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AwaitingSince",
                table: "Cases",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimActivityAt",
                table: "Cases",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimedBy",
                table: "Cases",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimedByUser",
                table: "Cases",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CaseAuditRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseAuditRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaseAuditRecords_Cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "Cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Cases_Status_AwaitingSince",
                table: "Cases",
                columns: new[] { "Status", "AwaitingSince" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseAuditRecords_CaseId_At",
                table: "CaseAuditRecords",
                columns: new[] { "CaseId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaseAuditRecords");

            migrationBuilder.DropIndex(
                name: "IX_Cases_Status_AwaitingSince",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "ApprovalTimeoutMinutes",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "AwaitingSince",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "ClaimActivityAt",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "ClaimedBy",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "ClaimedByUser",
                table: "Cases");
        }
    }
}
