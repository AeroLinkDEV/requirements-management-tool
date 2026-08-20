using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReopenStrandedChangeRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Written by hand rather than left as the scaffolded AlterColumn, which emits a bare
            // `ALTER COLUMN ... TYPE character varying(40)`. PostgreSQL has no assignment cast from integer to
            // varchar and refuses that outright, and even where it succeeded it would write `2` where the
            // column beside it writes `Approved`. The CASE is the actual conversion: ordinals in, the names
            // `ChangeRequestState` uses out.
            migrationBuilder.Sql(@"
                ALTER TABLE system_change_requests
                ALTER COLUMN ""WithdrawnFromState"" TYPE character varying(40)
                USING CASE ""WithdrawnFromState""
                    WHEN 0 THEN 'Draft'
                    WHEN 1 THEN 'InReview'
                    WHEN 2 THEN 'Approved'
                    WHEN 3 THEN 'Deferred'
                    WHEN 4 THEN 'SelectedForBaseline'
                    WHEN 5 THEN 'Withdrawn'
                END;");

            migrationBuilder.AddColumn<string>(
                name: "RebaseRequiredReason",
                table: "system_change_requests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RebaseRequiredReason",
                table: "system_change_requests");

            migrationBuilder.Sql(@"
                ALTER TABLE system_change_requests
                ALTER COLUMN ""WithdrawnFromState"" TYPE integer
                USING CASE ""WithdrawnFromState""
                    WHEN 'Draft' THEN 0
                    WHEN 'InReview' THEN 1
                    WHEN 'Approved' THEN 2
                    WHEN 'Deferred' THEN 3
                    WHEN 'SelectedForBaseline' THEN 4
                    WHEN 'Withdrawn' THEN 5
                END;");
        }
    }
}
