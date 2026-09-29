import type { AircraftData } from "./efis";

/**
 * The pure parts of the out-the-window view: decoding the open elevation tiles, colouring and shading the ground from
 * them, and turning the aircraft's state into a camera. Kept apart from the Cesium component so they can be tested
 * without a GPU.
 *
 * The ground is drawn from elevation alone, the way a synthetic vision system draws it: colour by height, shade by
 * slope. Photographic imagery is a separate licensing choice and is not used.
 */

export const FT = 0.3048;
/** The deepest level of the open Terrain Tiles set (and of the relay). */
export const TERRAIN_MAX_ZOOM = 15;
/** The terrain mesh is built from level 13 at most; finer levels sample their level-13 ancestor. */
export const MESH_MAX_ZOOM = 13;
export const TILE_PIXELS = 256;

export type Layout = "hud" | "panel";
export type View = "cockpit" | "chase" | "map";

/** Heights in metres from a Terrarium-encoded tile: height = R × 256 + G + B / 256 − 32768. */
export function decodeTerrarium(rgba: ArrayLike<number>): Float32Array {
  const heights = new Float32Array(rgba.length / 4);
  for (let i = 0; i < heights.length; i++) heights[i] = rgba[i * 4] * 256 + rgba[i * 4 + 1] + rgba[i * 4 + 2] / 256 - 32768;
  return heights;
}

/** The ancestor at `maxZoom` that holds a tile, and where the tile sits inside it (offset and span, 0 … 1). */
export function ancestorOf(level: number, x: number, y: number, maxZoom: number) {
  const z = Math.min(level, maxZoom), scale = 2 ** (level - z);
  const ax = Math.floor(x / scale), ay = Math.floor(y / scale);
  return { z, x: ax, y: ay, offsetX: (x - ax * scale) / scale, offsetY: (y - ay * scale) / scale, span: 1 / scale };
}

/**
 * A `samples` × `samples` height grid (row 0 north) for the part of a 256 × 256 source tile at (offset, span), bilinear.
 * Below sea level reads as sea level: the bench flies over land, and a bathymetric trench would only confuse the view.
 */
export function sampleHeights(source: Float32Array, offsetX: number, offsetY: number, span: number, samples: number): Float32Array {
  const out = new Float32Array(samples * samples), last = TILE_PIXELS - 1;
  for (let j = 0; j < samples; j++) for (let i = 0; i < samples; i++) {
    const u = (offsetX + (span * i) / (samples - 1)) * last, v = (offsetY + (span * j) / (samples - 1)) * last;
    const x0 = Math.floor(u), y0 = Math.floor(v), x1 = Math.min(x0 + 1, last), y1 = Math.min(y0 + 1, last);
    const fx = u - x0, fy = v - y0;
    const top = source[y0 * TILE_PIXELS + x0] * (1 - fx) + source[y0 * TILE_PIXELS + x1] * fx;
    const bottom = source[y1 * TILE_PIXELS + x0] * (1 - fx) + source[y1 * TILE_PIXELS + x1] * fx;
    out[j * samples + i] = Math.max(0, top * (1 - fy) + bottom * fy);
  }
  return out;
}

/** Ground width of one pixel of a Web Mercator tile at this zoom and latitude, metres. */
export const pixelMetres = (zoom: number, latitude: number) =>
  (40_075_016.686 * Math.cos((latitude * Math.PI) / 180)) / 2 ** zoom / TILE_PIXELS;

/** Latitude of a Web Mercator tile row's centre. */
export const tileLatitude = (zoom: number, y: number) =>
  (Math.atan(Math.sinh(Math.PI * (1 - (2 * (y + 0.5)) / 2 ** zoom))) * 180) / Math.PI;

// Height bands (metres) with their colours: lowland green through upland olive and tan to rock and snow.
const RAMP: [number, [number, number, number]][] = [
  [0, [74, 112, 62]], [250, [96, 124, 70]], [500, [128, 128, 82]], [900, [148, 128, 94]],
  [1500, [128, 108, 90]], [2500, [150, 150, 150]], [3500, [236, 240, 242]],
];
export const WATER: [number, number, number] = [52, 92, 132];

export function rampColour(height: number): [number, number, number] {
  if (height <= RAMP[0][0]) return RAMP[0][1];
  for (let i = 1; i < RAMP.length; i++) {
    const [h1, c1] = RAMP[i];
    if (height <= h1) {
      const [h0, c0] = RAMP[i - 1], t = (height - h0) / (h1 - h0);
      return [0, 1, 2].map(k => Math.round(c0[k] + (c1[k] - c0[k]) * t)) as [number, number, number];
    }
  }
  return RAMP[RAMP.length - 1][1];
}

/**
 * Colours a 256 × 256 height tile as RGBA: height colour, lit from the north-west at 45° (the cartographic convention),
 * and water where the ground is perfectly level over its neighbourhood (lakes and rivers are flat in the source data).
 */
export function shadeTile(heights: Float32Array, cellMetres: number): Uint8ClampedArray<ArrayBuffer> {
  const out = new Uint8ClampedArray(TILE_PIXELS * TILE_PIXELS * 4);
  const at = (x: number, y: number) =>
    heights[Math.min(TILE_PIXELS - 1, Math.max(0, y)) * TILE_PIXELS + Math.min(TILE_PIXELS - 1, Math.max(0, x))];
  const azimuth = (315 * Math.PI) / 180, zenith = (45 * Math.PI) / 180;
  for (let y = 0; y < TILE_PIXELS; y++) for (let x = 0; x < TILE_PIXELS; x++) {
    const h = at(x, y);
    const dzdx = (at(x + 1, y) - at(x - 1, y)) / (2 * cellMetres), dzdy = (at(x, y + 1) - at(x, y - 1)) / (2 * cellMetres);
    let level = true;
    for (let dy = -2; dy <= 2 && level; dy++) for (let dx = -2; dx <= 2; dx++) if (Math.abs(at(x + dx, y + dy) - h) > 0.05) { level = false; break; }
    const slope = Math.atan(Math.hypot(dzdx, dzdy)), aspect = Math.atan2(dzdy, -dzdx);
    const light = Math.cos(zenith) * Math.cos(slope) + Math.sin(zenith) * Math.sin(slope) * Math.cos(azimuth - Math.PI / 2 - aspect);
    const shade = 0.55 + 0.55 * Math.max(0, light);
    const base = level && h > 0 ? WATER : rampColour(h);
    const i = (y * TILE_PIXELS + x) * 4;
    out[i] = Math.min(255, base[0] * shade); out[i + 1] = Math.min(255, base[1] * shade); out[i + 2] = Math.min(255, base[2] * shade); out[i + 3] = 255;
  }
  return out;
}

export type CameraPose = { longitude: number; latitude: number; height: number; heading: number; pitch: number; roll: number };
const rad = (deg: number) => (deg * Math.PI) / 180;

/**
 * Where the camera is for a view, in degrees and metres in, radians out.
 *
 * Cockpit: the pilot's eye at the aircraft, looking along the heading, pitched by the flight-path angle (the
 * point-mass model has no attitude of its own) and down a little so the ground ahead fills most of the window, rolled
 * with the bank. The field of view spans the window's width, so the panel layout's short, wide window sees less
 * vertically and looks down less to keep the horizon in it. Chase: behind and above, level. Map: straight down from
 * altitude, heading up.
 */
export function cameraPose(air: AircraftSample, view: View, layout: Layout): CameraPose {
  const { lat, lon } = air.position, height = air.altitude * FT;
  if (view === "cockpit") {
    return { longitude: lon, latitude: lat, height, heading: rad(air.heading), pitch: rad(air.pitch - (layout === "panel" ? 3 : 5)), roll: rad(air.bank) };
  }
  if (view === "chase") {
    const back = 0.12 / 60; // 0.12 NM, in degrees of latitude
    return {
      longitude: lon - (back * Math.sin(rad(air.heading))) / Math.cos(rad(lat)), latitude: lat - back * Math.cos(rad(air.heading)),
      height: height + 90, heading: rad(air.heading), pitch: rad(-18), roll: 0,
    };
  }
  return { longitude: lon, latitude: lat, height: height + 30_000 * FT, heading: rad(air.heading), pitch: rad(-90), roll: 0 };
}

export type AircraftSample = Pick<AircraftData, "position" | "altitude" | "heading" | "pitch" | "bank">;

/**
 * The aircraft `fraction` (0 … 1) of the way from one simulation tick to the next. The simulation moves in quarter
 * seconds and much further per tick at a high rate, while the view draws every frame; drawing each frame between the
 * last two ticks keeps the motion smooth at the cost of one tick's delay. Angles take the short way round.
 */
export function blendAircraft(from: AircraftSample, to: AircraftSample, fraction: number): AircraftSample {
  const f = Math.min(1, Math.max(0, fraction));
  const mix = (a: number, b: number) => a + (b - a) * f;
  const turn = (a: number, b: number) => ((((b - a) % 360) + 540) % 360) - 180;
  const angle = (a: number, b: number) => (a + turn(a, b) * f + 360) % 360;
  const lon = from.position.lon + turn(from.position.lon, to.position.lon) * f;
  return {
    position: { lat: mix(from.position.lat, to.position.lat), lon: ((((lon + 180) % 360) + 360) % 360) - 180 },
    altitude: mix(from.altitude, to.altitude), heading: angle(from.heading, to.heading), pitch: mix(from.pitch, to.pitch), bank: mix(from.bank, to.bank),
  };
}

/** The route line's height at each fix: its altitude constraint, else the altitude the line had before it. */
export function routeHeights(constraints: (number | null)[], startFeet: number): number[] {
  let current = startFeet;
  return constraints.map(value => (current = value ?? current) * FT);
}
