using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEnterpriseRequirementsWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "artifact_comments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactType = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ParentCommentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Body = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    MentionsJson = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Disposition = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifact_comments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_artifact_comments_artifact_comments_ParentCommentId",
                        column: x => x.ParentCommentId,
                        principalTable: "artifact_comments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "artifact_schema_definitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AppliesTo = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifact_schema_definitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "enterprise_operation_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    RequestJson = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ItemCount = table.Column<int>(type: "integer", nullable: false),
                    SucceededCount = table.Column<int>(type: "integer", nullable: false),
                    FailedCount = table.Column<int>(type: "integer", nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enterprise_operation_jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "requirement_interchange_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MappingJson = table.Column<string>(type: "text", nullable: false),
                    RowsJson = table.Column<string>(type: "text", nullable: false),
                    ValidRows = table.Column<int>(type: "integer", nullable: false),
                    InvalidRows = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedScrId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requirement_interchange_jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "requirement_specifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Level = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requirement_specifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_requirement_specifications_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "saved_requirement_views",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    QueryJson = table.Column<string>(type: "text", nullable: false),
                    ColumnsJson = table.Column<string>(type: "text", nullable: false),
                    IsShared = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saved_requirement_views", x => x.Id);
                    table.ForeignKey(
                        name: "FK_saved_requirement_views_user_accounts_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "user_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "artifact_field_definitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SchemaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    OptionsJson = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifact_field_definitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_artifact_field_definitions_artifact_schema_definitions_Sche~",
                        column: x => x.SchemaId,
                        principalTable: "artifact_schema_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "requirement_revision_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SchemaId = table.Column<Guid>(type: "uuid", nullable: false),
                    RichText = table.Column<string>(type: "text", nullable: false),
                    AttributesJson = table.Column<string>(type: "text", nullable: false),
                    TagsJson = table.Column<string>(type: "text", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requirement_revision_profiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_requirement_revision_profiles_artifact_schema_definitions_S~",
                        column: x => x.SchemaId,
                        principalTable: "artifact_schema_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_requirement_revision_profiles_requirement_revisions_Revisio~",
                        column: x => x.RevisionId,
                        principalTable: "requirement_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "specification_nodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SpecificationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Heading = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RequirementArtifactId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_specification_nodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_specification_nodes_requirement_specifications_Specificatio~",
                        column: x => x.SpecificationId,
                        principalTable: "requirement_specifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_specification_nodes_requirements_RequirementArtifactId",
                        column: x => x.RequirementArtifactId,
                        principalTable: "requirements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_specification_nodes_specification_nodes_ParentId",
                        column: x => x.ParentId,
                        principalTable: "specification_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_artifact_comments_ParentCommentId",
                table: "artifact_comments",
                column: "ParentCommentId");

            migrationBuilder.CreateIndex(
                name: "IX_artifact_comments_ProjectId_ArtifactType_ArtifactId_Created~",
                table: "artifact_comments",
                columns: new[] { "ProjectId", "ArtifactType", "ArtifactId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_artifact_field_definitions_SchemaId_Key",
                table: "artifact_field_definitions",
                columns: new[] { "SchemaId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_artifact_schema_definitions_ProjectId_Key",
                table: "artifact_schema_definitions",
                columns: new[] { "ProjectId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_enterprise_operation_jobs_ProjectId_CreatedAt",
                table: "enterprise_operation_jobs",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_requirement_interchange_jobs_ProjectId_CreatedAt",
                table: "requirement_interchange_jobs",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_requirement_interchange_jobs_ProjectId_Sha256",
                table: "requirement_interchange_jobs",
                columns: new[] { "ProjectId", "Sha256" });

            migrationBuilder.CreateIndex(
                name: "IX_requirement_revision_profiles_RevisionId",
                table: "requirement_revision_profiles",
                column: "RevisionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_requirement_revision_profiles_SchemaId",
                table: "requirement_revision_profiles",
                column: "SchemaId");

            migrationBuilder.CreateIndex(
                name: "IX_requirement_specifications_ProjectId_DocumentNumber",
                table: "requirement_specifications",
                columns: new[] { "ProjectId", "DocumentNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_saved_requirement_views_OwnerId",
                table: "saved_requirement_views",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_saved_requirement_views_ProjectId_OwnerId_Name",
                table: "saved_requirement_views",
                columns: new[] { "ProjectId", "OwnerId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_specification_nodes_ParentId",
                table: "specification_nodes",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_specification_nodes_RequirementArtifactId",
                table: "specification_nodes",
                column: "RequirementArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_specification_nodes_SpecificationId_ParentId_Position",
                table: "specification_nodes",
                columns: new[] { "SpecificationId", "ParentId", "Position" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "artifact_comments");

            migrationBuilder.DropTable(
                name: "artifact_field_definitions");

            migrationBuilder.DropTable(
                name: "enterprise_operation_jobs");

            migrationBuilder.DropTable(
                name: "requirement_interchange_jobs");

            migrationBuilder.DropTable(
                name: "requirement_revision_profiles");

            migrationBuilder.DropTable(
                name: "saved_requirement_views");

            migrationBuilder.DropTable(
                name: "specification_nodes");

            migrationBuilder.DropTable(
                name: "artifact_schema_definitions");

            migrationBuilder.DropTable(
                name: "requirement_specifications");
        }
    }
}
