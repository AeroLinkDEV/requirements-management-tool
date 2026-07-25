using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConfigurableReviewWorkflows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "WorkflowId",
                table: "review_cycles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkflowLogicalId",
                table: "review_cycles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkflowName",
                table: "review_cycles",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "WorkflowVersion",
                table: "review_cycles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StageName",
                table: "approval_steps",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "review_workflows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LogicalId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AppliesTo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_workflows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "review_workflow_stages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    RequiredRole = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_workflow_stages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_review_workflow_stages_review_workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "review_workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_review_workflow_stages_WorkflowId_Position",
                table: "review_workflow_stages",
                columns: new[] { "WorkflowId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_review_workflows_LogicalId_Version",
                table: "review_workflows",
                columns: new[] { "LogicalId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_review_workflows_ProjectId_AppliesTo_State",
                table: "review_workflows",
                columns: new[] { "ProjectId", "AppliesTo", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "review_workflow_stages");

            migrationBuilder.DropTable(
                name: "review_workflows");

            migrationBuilder.DropColumn(
                name: "WorkflowId",
                table: "review_cycles");

            migrationBuilder.DropColumn(
                name: "WorkflowLogicalId",
                table: "review_cycles");

            migrationBuilder.DropColumn(
                name: "WorkflowName",
                table: "review_cycles");

            migrationBuilder.DropColumn(
                name: "WorkflowVersion",
                table: "review_cycles");

            migrationBuilder.DropColumn(
                name: "StageName",
                table: "approval_steps");
        }
    }
}
