using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Adds prospective capture of the actor's human-readable name to the Problem Report audit trail.
    ///
    /// Nullable, and existing rows are deliberately left NULL. Backfilling them from today's
    /// <c>user_accounts.DisplayName</c> would assert that the name a handle resolves to now is the name that
    /// was true when the event happened years earlier, which is exactly the rewrite this column exists to
    /// prevent. An event recorded before this migration genuinely captured no name, and the honest rendering
    /// of it stays the login handle already in <c>Actor</c>.
    ///
    /// The column sits beside the evidence snapshot rather than inside it, so every historical
    /// <c>SnapshotHash</c> continues to recompute exactly as it was written.
    ///
    /// Regenerated after #824 landed <c>AddProjectLeadership</c>. The first scaffold carried an earlier
    /// timestamp than that migration, so it would have sorted before one already applied elsewhere while
    /// running after it, and its snapshot had never seen the project-leadership model. Re-scaffolding gives
    /// it a later timestamp and a snapshot of the merged model. The two touch no common table.
    /// </summary>
    public partial class AddProblemReportRevisionActorDisplayName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActorDisplayName",
                table: "problem_report_revisions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActorDisplayName",
                table: "problem_report_revisions");
        }
    }
}
