using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScopeSoftwareDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SoftwareLevel",
                table: "system_change_requests",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE system_change_requests AS request
                   SET "SoftwareLevel" = 'HighLevel'
                 WHERE request."Type" = 'Software'
                   AND EXISTS (SELECT 1 FROM requirement_changes AS change WHERE change."ScrId" = request."Id" AND change."Level" = 'HighLevel')
                   AND NOT EXISTS (SELECT 1 FROM requirement_changes AS change WHERE change."ScrId" = request."Id" AND change."Level" <> 'HighLevel');
                UPDATE system_change_requests AS request
                   SET "SoftwareLevel" = 'LowLevel'
                 WHERE request."Type" = 'Software'
                   AND EXISTS (SELECT 1 FROM requirement_changes AS change WHERE change."ScrId" = request."Id" AND change."Level" = 'LowLevel')
                   AND NOT EXISTS (SELECT 1 FROM requirement_changes AS change WHERE change."ScrId" = request."Id" AND change."Level" <> 'LowLevel');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SoftwareLevel",
                table: "system_change_requests");
        }
    }
}
