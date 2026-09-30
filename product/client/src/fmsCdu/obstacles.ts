import type { LatLon } from "./fmsModel";
import { awarenessColour, type TerrainColouring } from "./terrainAwareness";

/**
 * Obstacles from the FAA Digital Obstacle File (DOF, daily CSV), as the out-the-window view draws them. The bench
 * serves an extract of unchanged records near its US areas (public/fms-cdu/obstacles, with its provenance); this
 * module reads it, answers a query by bounds, and colours an obstacle as the terrain colouring would colour its top. Public domain data, for demonstration only, not for navigation.
 */
export type Obstacle = {
  oas: string;
  /** VERIFIED STATUS: O verified, U unverified, as published. */
  verified: string;
  position: LatLon;
  type: string;
  quantity: number;
  /** Heights in feet: above ground level and above mean sea level. */
  aglFt: number;
  amslFt: number;
  /** LIGHTING, ACCURACY and MARKING codes as the FAA publishes them (not decoded). */
  lighting: string;
  accuracy: string;
  marking: string;
};

const HEADER = ["OAS", "VERIFIED STATUS", "COUNTRY", "STATE", "CITY", "LATDEC", "LONDEC", "DMSLAT", "DMSLON", "TYPE", "QUANTITY", "AGL", "AMSL", "LIGHTING", "ACCURACY", "MARKING", "FAA STUDY", "ACTION", "JDATE"];

/**
 * Reads DOF CSV text: the published header, then one record per line. A record whose position or heights cannot be
 * read is skipped with the reason; a file without the published header is refused whole.
 */
export function parseDof(text: string): { obstacles: Obstacle[]; errors: string[] } {
  const lines = text.split(/\r?\n/).filter(line => line.trim());
  const header = (lines[0] ?? "").split(",").map(cell => cell.trim());
  if (HEADER.some((name, i) => header[i] !== name)) return { obstacles: [], errors: ["not a DOF CSV file (header differs)"] };
  const obstacles: Obstacle[] = [];
  const errors: string[] = [];
  lines.slice(1).forEach((line, i) => {
    const f = line.split(",").map(cell => cell.trim());
    const lat = Number(f[5]), lon = Number(f[6]), agl = Number(f[11]), amsl = Number(f[12]), quantity = Number(f[10]);
    if (f.length < HEADER.length || !Number.isFinite(lat) || !Number.isFinite(lon) || Math.abs(lat) > 90 || Math.abs(lon) > 180) {
      errors.push(`record ${i + 1}: position not readable`);
      return;
    }
    if (!Number.isFinite(agl) || !Number.isFinite(amsl) || agl < 0) { errors.push(`record ${i + 1} (${f[0]}): heights not readable`); return; }
    obstacles.push({
      oas: f[0], verified: f[1], position: { lat, lon }, type: f[9], quantity: Number.isFinite(quantity) ? quantity : 1,
      aglFt: agl, amslFt: amsl, lighting: f[13], accuracy: f[14], marking: f[15],
    });
  });
  return { obstacles, errors };
}

export type Bounds = { south: number; west: number; north: number; east: number };

/** The obstacles inside a latitude/longitude box (degrees; west < east, the bench's areas do not cross 180°). */
export function obstaclesWithin(obstacles: readonly Obstacle[], bounds: Bounds): Obstacle[] {
  return obstacles.filter(o => o.position.lat >= bounds.south && o.position.lat <= bounds.north && o.position.lon >= bounds.west && o.position.lon <= bounds.east);
}

/** An uncoloured obstacle (the mode off, or relative mode below the caution threshold): a neutral light grey. */
export const NEUTRAL_RGB: readonly [number, number, number] = [225, 228, 232];

/**
 * An obstacle's colour: its top (AMSL) coloured exactly as the terrain would be at that height (terrainAwareness.ts,
 * shared with the terrain shader, so the two agree), drawn opaque; neutral where the mode leaves it uncoloured.
 */
export function obstacleColour(obstacle: Obstacle, aircraftAltitudeFt: number, mode: TerrainColouring): readonly [number, number, number] {
  return awarenessColour(mode, obstacle.amslFt, aircraftAltitudeFt) ?? NEUTRAL_RGB;
}
