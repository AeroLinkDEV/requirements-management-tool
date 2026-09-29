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
 * Cockpit: the pilot's eye at the aircraft, along the aircraft's heading (not its track, so a crab or a sideways
 * drift shows the ground sliding past), pitched and rolled with the aircraft data's pitch and bank, and down a little
 * so the ground ahead fills most of the window. The field of view spans the window's width, so the panel layout's
 * short, wide window sees less vertically and looks down less to keep the horizon in it; in the hover, where the
 * pilot watches the ground close ahead, it looks down further. Chase: close behind the aircraft along its heading
 * and a little above, level, so the model fills a good part of the view and its attitude shows. Map: straight down
 * from altitude, heading up.
 */
export function cameraPose(air: AircraftSample, view: View, layout: Layout): CameraPose {
  const { lat, lon } = air.position, height = air.altitude * FT;
  if (view === "cockpit") {
    const down = (layout === "panel" ? 3 : 5) + (air.hoverData ? HOVER_LOOK_DOWN : 0);
    return { longitude: lon, latitude: lat, height, heading: rad(air.heading), pitch: rad(air.pitch - down), roll: rad(air.bank) };
  }
  if (view === "chase") {
    const back = CHASE_BEHIND / 110_540; // metres behind, in degrees of latitude
    return {
      longitude: lon - (back * Math.sin(rad(air.heading))) / Math.cos(rad(lat)), latitude: lat - back * Math.cos(rad(air.heading)),
      height: height + CHASE_ABOVE, heading: rad(air.heading), pitch: rad(-7), roll: 0,
    };
  }
  return { longitude: lon, latitude: lat, height: height + 30_000 * FT, heading: rad(air.heading), pitch: rad(-90), roll: 0 };
}

/** The chase camera's place: metres behind the aircraft along its heading, and above it. */
export const CHASE_BEHIND = 55, CHASE_ABOVE = 10;
/** How much further down the cockpit camera looks while the hover data is shown, degrees. */
export const HOVER_LOOK_DOWN = 8;

export type AircraftPart = {
  name: string;
  shape: "box" | "ellipsoid";
  /** A box's full dimensions or an ellipsoid's radii, metres: forward, left, up. */
  size: [number, number, number];
  /** The part's centre from the aircraft's reference point (the rotor mast), metres: forward, left, up. */
  offset: [number, number, number];
  colour: [number, number, number];
  /** Opacity, 0 … 1: the rotor discs are translucent, everything else opaque. */
  alpha?: number;
};

/**
 * The aircraft seen in the chase view: a generic medium twin-engine helicopter built from boxes and ellipsoids, with
 * a 14.6 m main rotor disc, a tail boom with a tail rotor on its left, and skids. The rotors are still, translucent
 * discs. It stands for "the aircraft" and is not the AW189 or any other type the CMA-9000 is installed in.
 */
export const AIRCRAFT_PARTS: AircraftPart[] = [
  { name: "cabin", shape: "ellipsoid", size: [3.3, 1.25, 1.15], offset: [0.9, 0, -0.2], colour: [236, 238, 240] },
  { name: "engine deck", shape: "box", size: [3.0, 1.5, 0.6], offset: [0, 0, 1.0], colour: [196, 202, 210] },
  { name: "mast", shape: "box", size: [0.3, 0.3, 0.6], offset: [0, 0, 1.55], colour: [90, 96, 104] },
  { name: "main rotor", shape: "ellipsoid", size: [7.3, 7.3, 0.05], offset: [0, 0, 1.9], colour: [210, 214, 220], alpha: 0.3 },
  { name: "tail boom", shape: "ellipsoid", size: [3.6, 0.35, 0.35], offset: [-5.0, 0, 0.45], colour: [236, 238, 240] },
  { name: "stabiliser", shape: "box", size: [0.7, 2.6, 0.1], offset: [-7.3, 0, 0.55], colour: [196, 202, 210] },
  { name: "fin", shape: "box", size: [1.1, 0.15, 1.7], offset: [-8.4, 0, 1.2], colour: [23, 108, 99] },
  { name: "tail rotor", shape: "ellipsoid", size: [1.1, 0.04, 1.1], offset: [-8.5, 0.35, 1.45], colour: [60, 66, 72], alpha: 0.4 },
  { name: "left skid", shape: "box", size: [4.4, 0.12, 0.12], offset: [0.4, 1.15, -1.6], colour: [70, 74, 80] },
  { name: "right skid", shape: "box", size: [4.4, 0.12, 0.12], offset: [0.4, -1.15, -1.6], colour: [70, 74, 80] },
  { name: "left front strut", shape: "box", size: [0.1, 0.1, 0.55], offset: [1.6, 1.05, -1.3], colour: [70, 74, 80] },
  { name: "right front strut", shape: "box", size: [0.1, 0.1, 0.55], offset: [1.6, -1.05, -1.3], colour: [70, 74, 80] },
  { name: "left rear strut", shape: "box", size: [0.1, 0.1, 0.55], offset: [-0.8, 1.05, -1.3], colour: [70, 74, 80] },
  { name: "right rear strut", shape: "box", size: [0.1, 0.1, 0.55], offset: [-0.8, -1.05, -1.3], colour: [70, 74, 80] },
];

/**
 * What the view needs of the aircraft. `hoverData` is the helicopter profile's "hover data on the display" flag
 * (AircraftData.helicopter.hoverData), when the aircraft data carries it.
 */
export type AircraftSample = Pick<AircraftData, "position" | "altitude" | "heading" | "pitch" | "bank"> & { hoverData?: boolean };

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
    // A flag has no in-between: the newer tick's stands.
    hoverData: to.hoverData,
  };
}

/** The route line's height at each fix: its altitude constraint, else the altitude the line had before it. */
export function routeHeights(constraints: (number | null)[], startFeet: number): number[] {
  let current = startFeet;
  return constraints.map(value => (current = value ?? current) * FT);
}
