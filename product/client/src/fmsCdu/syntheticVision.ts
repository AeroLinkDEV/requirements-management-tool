/**
 * Synthetic vision for the PFD: the terrain ahead drawn behind the attitude indicator, conformal with its pitch scale.
 *
 * It is drawn in two dimensions, a column at a time, marching each column's ray out over the height data front to
 * back and filling the rows each new ridge rises above what is already drawn (the "voxel space" method). That needs
 * no WebGL context of its own beside the out-the-window view, costs a few milliseconds at PFD size, and is a pure
 * function of the aircraft state and a height lookup, so it is tested without a browser.
 *
 * Roll is not applied here: the PFD rotates the image with its attitude group, as it rotates the sky and ground.
 */

export type SvsEye = {
  lat: number;
  lon: number;
  /** Metres above sea level. */
  altitude: number;
  /** True heading, degrees. */
  heading: number;
  /** Pitch, degrees, nose up positive. */
  pitch: number;
};

export type SvsView = {
  width: number;
  height: number;
  /** The pixel the aircraft's boresight passes through at zero pitch. */
  centreX: number;
  centreY: number;
  /** Pixels per unit tangent: a feature 1° above the boresight is `focal × tan 1°` pixels above it. */
  focal: number;
  /** How far the terrain is drawn, metres. */
  range?: number;
};

/** Height of the ground in metres, or null where there is no data yet (drawn as sea level). */
export type HeightAt = (lat: number, lon: number) => number | null;

/** Height tiles at this zoom: about 55 m a sample at the demonstration's latitude, enough for a 40 km picture. */
export const SVS_ZOOM = 11;

const EARTH_RADIUS = 6_371_000;
/** Refraction makes the horizon a little further than geometry alone; the usual surveying factor. */
const REFRACTION = 0.13;
const SKY_TOP: [number, number, number] = [26, 84, 168];
const SKY_HORIZON: [number, number, number] = [120, 172, 222];
// Distance fades the ground toward a neutral grey rather than sky blue, so far terrain never reads as sky.
const HAZE: [number, number, number] = [168, 170, 164];

// Height bands (metres) with their colours, darker and browner than the out-the-window view so the white and
// magenta symbology stays readable over them.
const RAMP: [number, [number, number, number]][] = [
  [0, [58, 92, 50]], [300, [84, 102, 56]], [800, [112, 98, 64]], [1500, [104, 86, 70]], [2500, [128, 124, 122]], [3500, [196, 200, 204]],
];

export function svsColour(height: number): [number, number, number] {
  if (height <= RAMP[0][0]) return RAMP[0][1];
  for (let i = 1; i < RAMP.length; i++) {
    const [h1, c1] = RAMP[i];
    if (height <= h1) {
      const [h0, c0] = RAMP[i - 1], t = (height - h0) / (h1 - h0);
      return [c0[0] + (c1[0] - c0[0]) * t, c0[1] + (c1[1] - c0[1]) * t, c0[2] + (c1[2] - c0[2]) * t];
    }
  }
  return RAMP[RAMP.length - 1][1];
}

/** The screen row of the horizon for a pitch: nose up moves it down. */
export const horizonRow = (view: SvsView, pitch: number) => view.centreY + Math.tan((pitch * Math.PI) / 180) * view.focal;

/**
 * Draws the terrain and sky into `out`, RGBA, `view.width` × `view.height`. Returns the number of height samples
 * taken, which a caller can use to judge the cost.
 */
export function renderSyntheticVision(out: Uint8ClampedArray, view: SvsView, eye: SvsEye, heightAt: HeightAt): number {
  const { width, height, centreX, focal } = view;
  const range = view.range ?? 40_000;
  const horizon = horizonRow(view, eye.pitch);
  const cosLat = Math.cos((eye.lat * Math.PI) / 180);
  let samples = 0;

  for (let x = 0; x < width; x++) {
    const offset = Math.atan((x + 0.5 - centreX) / focal);
    const bearing = (eye.heading * Math.PI) / 180 + offset;
    // Degrees of latitude and longitude per metre along this column's ray.
    const north = Math.cos(bearing) / 110_540, east = Math.sin(bearing) / (111_320 * cosLat);
    const cosOffset = Math.cos(offset);
    let lowest = height; // rows at and below this are drawn
    let distance = 20, step = 10, previous: number | null = null;

    while (distance < range && lowest > 0) {
      const ground = heightAt(eye.lat + north * distance, eye.lon + east * distance) ?? 0;
      samples++;
      // Distance along the view axis, and the drop of the curved (and refracted) Earth.
      const depth = distance * cosOffset;
      const drop = ((1 - REFRACTION) * distance * distance) / (2 * EARTH_RADIUS);
      const row = horizon - ((ground - drop - eye.altitude) / depth) * focal;
      if (row < lowest) {
        // Faces turned toward the aircraft are lit, faces turned away are in shade; distance fades into haze.
        const rise = previous === null ? 0 : (ground - previous) / step;
        const shade = Math.min(1.25, Math.max(0.55, 0.9 + rise * 1.6));
        const haze = Math.min(1, (distance / range) ** 0.8);
        const base = svsColour(ground);
        const r = base[0] * shade * (1 - haze) + HAZE[0] * haze;
        const g = base[1] * shade * (1 - haze) + HAZE[1] * haze;
        const b = base[2] * shade * (1 - haze) + HAZE[2] * haze;
        for (let y = Math.max(0, Math.ceil(row)); y < lowest; y++) {
          const i = (y * width + x) * 4;
          out[i] = r; out[i + 1] = g; out[i + 2] = b; out[i + 3] = 255;
        }
        lowest = Math.max(0, Math.ceil(row));
      }
      previous = ground;
      distance += step;
      step *= 1.025;
    }

    // Above the terrain, sky: deep blue overhead fading toward the horizon.
    for (let y = 0; y < lowest; y++) {
      const t = Math.min(1, Math.max(0, 1 - (horizon - y) / (focal * 0.6)));
      const i = (y * width + x) * 4;
      out[i] = SKY_TOP[0] + (SKY_HORIZON[0] - SKY_TOP[0]) * t;
      out[i + 1] = SKY_TOP[1] + (SKY_HORIZON[1] - SKY_TOP[1]) * t;
      out[i + 2] = SKY_TOP[2] + (SKY_HORIZON[2] - SKY_TOP[2]) * t;
      out[i + 3] = 255;
    }
  }
  return samples;
}
