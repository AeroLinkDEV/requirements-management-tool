/**
 * The surface under the aircraft, for the radio altimeter (helicopter-first plan, Stage B2).
 *
 * The bench has no terrain model for the flight simulation. Instead a scenario declares flat regions, each with a
 * stated elevation; the radio height is the aircraft's physical height above that surface. Outside every declared
 * region there is no radio height: the word is NCD, never a plausible number. A terrain-derived radio height is later
 * work. Barometric altitude and radio height are separate outputs of the one physical height, so a barometric setting
 * never moves the aircraft and never changes the radio height.
 */

import type { LatLon } from "./fmsModel";
import { ACTIVE_PROFILE } from "./profile";

export type SurfaceRegion = {
  name: string;
  /** Elevation of the flat surface, feet above mean sea level. */
  elevationFt: number;
  /** The region's outline, in order; the last point joins the first. */
  outline: readonly LatLon[];
};

export type Surface = {
  id: string;
  title: string;
  /** Where the declaration comes from and what it simplifies. */
  basis: string;
  regions: readonly SurfaceRegion[];
};

/** No declared surface: the radio altimeter has nothing to measure against, so its height is never computed. */
export const NO_SURFACE: Surface = { id: "none", title: "No surface declared", basis: "radio height NCD everywhere", regions: [] };

/**
 * The open sea south of Southampton, New York (87N), for the helicopter acceptance mission: a flat sea-level region
 * whose north edge (40.78 N) stays south of the barrier-beach shoreline between 72.60 W and 72.30 W. Tide and waves
 * are ignored. A fixture approximation, not a coastline.
 */
export const OFFSHORE_87N: Surface = {
  id: "offshore-87n",
  title: "Open sea south of Southampton (87N)",
  basis: "declared flat sea at 0 ft MSL, tide and waves ignored; outline is a fixture approximation south of the shore",
  regions: [{
    name: "Atlantic south of 87N",
    elevationFt: 0,
    outline: [{ lat: 40.78, lon: -72.6 }, { lat: 40.78, lon: -72.3 }, { lat: 40.5, lon: -72.3 }, { lat: 40.5, lon: -72.6 }],
  }],
};

export const SURFACES: readonly Surface[] = [NO_SURFACE, OFFSHORE_87N];

export const surfaceById = (id: string) => SURFACES.find(surface => surface.id === id);

/** Whether a point is inside an outline (ray casting in latitude and longitude, adequate over a few tens of miles). */
function inside(point: LatLon, outline: readonly LatLon[]) {
  let within = false;
  for (let i = 0, j = outline.length - 1; i < outline.length; j = i, i += 1) {
    const a = outline[i], b = outline[j];
    if ((a.lat > point.lat) !== (b.lat > point.lat) && point.lon < ((b.lon - a.lon) * (point.lat - a.lat)) / (b.lat - a.lat) + a.lon) within = !within;
  }
  return within;
}

/** The declared surface elevation under a position, or null where nothing is declared. */
export function surfaceElevation(surface: Surface, position: LatLon): number | null {
  return surface.regions.find(region => inside(position, region.outline))?.elevationFt ?? null;
}

export type RadioHeight = { value: number | null; status: "NORMAL" | "NCD" | "FAIL" };

/**
 * The radio altimeter's output: the physical height above the declared surface. FAIL when the altimeter has failed;
 * NCD off every declared region, or above the altimeter's range; at or below the surface it reads 0.
 */
export function radioHeight(surface: Surface, position: LatLon, heightFt: number, failed: boolean): RadioHeight {
  if (failed) return { value: null, status: "FAIL" };
  const elevation = surfaceElevation(surface, position);
  if (elevation === null) return { value: null, status: "NCD" };
  const height = Math.max(0, heightFt - elevation);
  return height > ACTIVE_PROFILE.parameters.radioAltimeterRange.value ? { value: null, status: "NCD" } : { value: height, status: "NORMAL" };
}
