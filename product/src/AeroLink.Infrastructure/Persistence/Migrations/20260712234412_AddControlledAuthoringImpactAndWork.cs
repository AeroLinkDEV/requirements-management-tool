using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddControlledAuthoringImpactAndWork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttributesJson",
                table: "requirement_changes",
                type: "text",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "ImpactDispositionJson",
                table: "requirement_changes",
                type: "text",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "RichText",
                table: "requirement_changes",
                type: "character varying(32000)",
                maxLength: 32000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "artifact_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactType = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommentId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedTo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CompletedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifact_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_artifact_assignments_artifact_comments_CommentId",
                        column: x => x.CommentId,
                        principalTable: "artifact_comments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "artifact_watches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactType = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifact_watches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "requirement_import_mappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MappingJson = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requirement_import_mappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "user_notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Recipient = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Route = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: true),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_notifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_artifact_assignments_ArtifactType_ArtifactId",
                table: "artifact_assignments",
                columns: new[] { "ArtifactType", "ArtifactId" });

            migrationBuilder.CreateIndex(
                name: "IX_artifact_assignments_CommentId",
                table: "artifact_assignments",
                column: "CommentId");

            migrationBuilder.CreateIndex(
                name: "IX_artifact_assignments_ProjectId_AssignedTo_State_DueAt",
                table: "artifact_assignments",
                columns: new[] { "ProjectId", "AssignedTo", "State", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_artifact_watches_ProjectId_ArtifactType_ArtifactId_UserName",
                table: "artifact_watches",
                columns: new[] { "ProjectId", "ArtifactType", "ArtifactId", "UserName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_requirement_import_mappings_ProjectId_Name",
                table: "requirement_import_mappings",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_notifications_ProjectId",
                table: "user_notifications",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_user_notifications_Recipient_State_CreatedAt",
                table: "user_notifications",
                columns: new[] { "Recipient", "State", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "artifact_assignments");

            migrationBuilder.DropTable(
                name: "artifact_watches");

            migrationBuilder.DropTable(
                name: "requirement_import_mappings");

            migrationBuilder.DropTable(
                name: "user_notifications");

            migrationBuilder.DropColumn(
                name: "AttributesJson",
                table: "requirement_changes");

            migrationBuilder.DropColumn(
                name: "ImpactDispositionJson",
                table: "requirement_changes");

            migrationBuilder.DropColumn(
                name: "RichText",
                table: "requirement_changes");
        }
    }
}
