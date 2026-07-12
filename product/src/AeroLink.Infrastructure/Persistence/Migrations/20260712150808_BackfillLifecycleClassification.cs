using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackfillLifecycleClassification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"test_procedures\" SET \"Level\" = 'HighLevel' WHERE \"Level\" = '';");
            migrationBuilder.Sql("UPDATE \"system_change_requests\" SET \"Type\" = 'Software' WHERE \"Type\" = '' AND EXISTS (SELECT 1 FROM \"requirement_changes\" r WHERE r.\"ScrId\" = \"system_change_requests\".\"Id\" AND r.\"Level\" IN ('HighLevel','LowLevel'));");
            migrationBuilder.Sql("UPDATE \"system_change_requests\" SET \"Type\" = 'System' WHERE \"Type\" = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
