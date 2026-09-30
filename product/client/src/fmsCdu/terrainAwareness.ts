/**
 * How the out-the-window view colours the ground, and the obstacles on it, so terrain heights read at a glance (Sean,
 * 29 September: "seeing terrain heights / obstacles is also really desirable"; both modes as options). One set of
 * values, shared by the terrain shader (FmsOutTheWindow.tsx) and the obstacle layer, so the two always agree.
 *
 * - Relative (TAWS-style): red where the ground is at or above 100 ft below the aircraft, amber within 500 ft below
 *   it, nothing lower. The thresholds are laboratory values, not a certified TAWS or HTAWS envelope.
 * - Absolute: height bands above mean sea level, a hypsometric tint.
 * - Off: the imagery or relief alone.
 */
export type TerrainColouring = "off" | "relative" | "absolute";
export const TERRAIN_COLOURINGS: readonly TerrainColouring[] = ["off", "relative", "absolute"];

/** Red at or above the aircraft altitude less this, feet. */
export const RELATIVE_DANGER_FT = 100;
/** Amber at or above the aircraft altitude less this (and below the red), feet. */
export const RELATIVE_CAUTION_FT = 500;
export const DANGER_RGB: readonly [number, number, number] = [255, 45, 31];
export const CAUTION_RGB: readonly [number, number, number] = [255, 184, 0];

/** The absolute bands' edges, feet above mean sea level: below 500, 500–1,000, 1,000–2,000, 2,000–3,000, 3,000 and up. */
export const ABSOLUTE_BANDS_FT: readonly number[] = [500, 1000, 2000, 3000];
export const ABSOLUTE_RGB: readonly (readonly [number, number, number])[] = [[46, 139, 87], [154, 205, 50], [255, 215, 0], [255, 140, 0], [178, 34, 34]];

/**
 * The colour for ground (or an obstacle top) at `heightFt` above mean sea level, with the aircraft at `aircraftFt`, or
 * null where the mode leaves it uncoloured (off, or relative mode below the caution threshold).
 */
export function awarenessColour(mode: TerrainColouring, heightFt: number, aircraftFt: number): readonly [number, number, number] | null {
  if (mode === "off") return null;
  if (mode === "relative") {
    if (heightFt >= aircraftFt - RELATIVE_DANGER_FT) return DANGER_RGB;
    if (heightFt >= aircraftFt - RELATIVE_CAUTION_FT) return CAUTION_RGB;
    return null;
  }
  const band = ABSOLUTE_BANDS_FT.filter(edge => heightFt >= edge).length;
  return ABSOLUTE_RGB[band];
}
