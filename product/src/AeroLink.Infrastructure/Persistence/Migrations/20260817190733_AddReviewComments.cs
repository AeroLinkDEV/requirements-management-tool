using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewComments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "review_comments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewCycleId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Anchor = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RequirementChangeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DecisionRecorded = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_comments", x => x.Id);
                    table.CheckConstraint("CK_review_comments_anchor", "(\"Anchor\" = 'RequirementRevision') = (\"RequirementChangeId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_review_comments_review_cycles_ReviewCycleId",
                        column: x => x.ReviewCycleId,
                        principalTable: "review_cycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_review_comments_ReviewCycleId_AuthorId",
                table: "review_comments",
                columns: new[] { "ReviewCycleId", "AuthorId" });

            migrationBuilder.CreateIndex(
                name: "IX_review_comments_ReviewCycleId_State",
                table: "review_comments",
                columns: new[] { "ReviewCycleId", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "review_comments");
        }
    }
}
