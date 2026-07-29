using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExactRequirementFilterIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Owner",
                table: "requirement_revision_profiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "requirement_revision_tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tag = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    DisplayTag = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requirement_revision_tags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_requirement_revision_tags_requirement_revisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "requirement_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_requirement_revision_tags_RevisionId_Tag",
                table: "requirement_revision_tags",
                columns: new[] { "RevisionId", "Tag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_requirement_revision_tags_Tag_RevisionId",
                table: "requirement_revision_tags",
                columns: new[] { "Tag", "RevisionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "requirement_revision_tags");

            migrationBuilder.DropColumn(
                name: "Owner",
                table: "requirement_revision_profiles");
        }
    }
}
