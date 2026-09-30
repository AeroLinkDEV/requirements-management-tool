import type { LatLon } from "./fmsModel";

/**
 * Obstacles from the FAA Digital Obstacle File (DOF, daily CSV), as the out-the-window view draws them. The bench
 * serves an extract of unchanged records near its US areas (public/fms-cdu/obstacles, with its provenance); this
 * module reads it, answers a query by bounds, and colours an obstacle by its clearance, to match the terrain
 * colouring modes. Public domain data, for demonstration only, not for navigation.
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

export type ObstacleColouring = "relative" | "absolute";
export type ObstacleBand = "danger" | "caution" | "clear" | "band0" | "band1" | "band2" | "band3";

/**
 * The colour band of an obstacle, matching the terrain colouring modes:
 * - relative to the aircraft: danger when its top is at or above 100 ft below the aircraft, caution within 500 ft
 *   below, clear otherwise;
 * - absolute: by the height of its top above mean sea level, in bands of below 500 ft, 500 to 1,000, 1,000 to 2,000,
 *   and 2,000 ft and above.
 * The thresholds are laboratory choices, matching the terrain colouring.
 */
export function obstacleBand(obstacle: Obstacle, aircraftAltitudeFt: number, mode: ObstacleColouring): ObstacleBand {
  if (mode === "relative") {
    const below = aircraftAltitudeFt - obstacle.amslFt;
    return below <= 100 ? "danger" : below <= 500 ? "caution" : "clear";
  }
  const top = obstacle.amslFt;
  return top < 500 ? "band0" : top < 1000 ? "band1" : top < 2000 ? "band2" : "band3";
}

/** The colour of each band, RGB 0-255. */
export const BAND_COLOURS: Record<ObstacleBand, [number, number, number]> = {
  danger: [230, 40, 40], caution: [245, 170, 20], clear: [150, 160, 170],
  band0: [120, 190, 120], band1: [210, 200, 90], band2: [220, 140, 60], band3: [200, 70, 70],
};
