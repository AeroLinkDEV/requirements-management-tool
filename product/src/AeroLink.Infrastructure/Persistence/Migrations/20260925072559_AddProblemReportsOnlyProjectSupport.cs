using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProblemReportsOnlyProjectSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ReleasedWithoutReadiness",
                table: "software_releases",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ResolutionAttestation",
                table: "problem_reports",
                type: "text",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "VerificationExecutionId",
                table: "problem_report_closure_candidates",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReleasedWithoutReadiness",
                table: "software_releases");

            migrationBuilder.DropColumn(
                name: "ResolutionAttestation",
                table: "problem_reports");

            migrationBuilder.AlterColumn<Guid>(
                name: "VerificationExecutionId",
                table: "problem_report_closure_candidates",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
