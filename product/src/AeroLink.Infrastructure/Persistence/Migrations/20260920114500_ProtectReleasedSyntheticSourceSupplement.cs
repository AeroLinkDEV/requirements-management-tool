using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AeroLinkDbContext))]
[Migration("20260920114500_ProtectReleasedSyntheticSourceSupplement")]
public partial class ProtectReleasedSyntheticSourceSupplement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
            return;

        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION aerolink_refuse_released_synthetic_supplement_mutation() RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION 'Released synthetic source supplements are immutable historical provenance';
            END;
            $$ LANGUAGE plpgsql;

            CREATE TRIGGER refuse_released_synthetic_supplement_mutation
                BEFORE UPDATE OR DELETE ON released_synthetic_source_supplements
                FOR EACH ROW EXECUTE FUNCTION aerolink_refuse_released_synthetic_supplement_mutation();

            CREATE OR REPLACE FUNCTION aerolink_refuse_released_synthetic_snapshot_mutation() RETURNS trigger AS $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM released_synthetic_source_supplements s
                    WHERE s."ProjectId" = OLD."ProjectId"
                      AND s."SourceSnapshotId" = OLD."Id") THEN
                    RAISE EXCEPTION 'A source snapshot owned by a released synthetic supplement is immutable historical provenance';
                END IF;
                IF TG_OP = 'DELETE' THEN
                    RETURN OLD;
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            CREATE TRIGGER refuse_released_synthetic_snapshot_mutation
                BEFORE UPDATE OR DELETE ON gitlab_source_snapshots
                FOR EACH ROW EXECUTE FUNCTION aerolink_refuse_released_synthetic_snapshot_mutation();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
            return;

        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS refuse_released_synthetic_snapshot_mutation ON gitlab_source_snapshots;
            DROP TRIGGER IF EXISTS refuse_released_synthetic_supplement_mutation ON released_synthetic_source_supplements;
            DROP FUNCTION IF EXISTS aerolink_refuse_released_synthetic_snapshot_mutation();
            DROP FUNCTION IF EXISTS aerolink_refuse_released_synthetic_supplement_mutation();
            """);
    }
}
