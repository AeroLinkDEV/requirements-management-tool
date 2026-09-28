import type { LatLon } from "./fmsModel";

/**
 * A simulated GPS constellation for the CMA-5024 sensor model (gps.ts): 31 satellites on circular medium Earth
 * orbits, and what a receiver sees of them from a place at a time: elevation, azimuth, elevation above its antenna's
 * horizon, and signal strength (C/N0).
 *
 * It is a SIMULATION for the test bench, with labelled simplifications: circular orbits in six planes inclined 55°
 * (the nominal GPS arrangement), a spherical Earth, no ephemeris or almanac data, and C/N0 as a parameter of elevation,
 * not the output of a tracking loop. It is deterministic for its seed: the same seed, time and place give the same sky.
 */

export type Attitude = { bank: number; pitch: number; heading: number };
export type SkySatellite = {
  prn: number;
  /** Degrees above the local horizon, and true azimuth. */
  elevation: number;
  azimuth: number;
  /** Degrees above the antenna's horizon, which tilts with the aircraft's bank and pitch. */
  antennaElevation: number;
  /** Carrier-to-noise density, dB-Hz. */
  cn0: number;
  /** Above the Earth's horizon and the antenna mask angle. */
  visible: boolean;
  /** Unit line of sight from the receiver: east, north, up. */
  los: [number, number, number];
};

const EARTH_RADIUS_M = 6_371_000;
const ORBIT_RADIUS_M = 26_560_000;
/** Half a sidereal day: the GPS orbital period. */
const ORBIT_PERIOD_S = 43_082;
const EARTH_RATE = 7.2921151467e-5;
const INCLINATION = (55 * Math.PI) / 180;
/** Satellites per plane, six planes: 31 in all, the extra one in the first plane. */
const PER_PLANE = [6, 5, 5, 5, 5, 5];
/** An arbitrary epoch for the orbits: the simulated sky repeats with its orbits, not with any real almanac. */
const EPOCH = Date.UTC(2026, 0, 1);

const rad = (d: number) => (d * Math.PI) / 180;
const deg = (r: number) => (r * 180) / Math.PI;
const dot = (a: number[], b: number[]) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

/** A small seeded generator (mulberry32), so the sky is repeatable. */
export function seededRandom(seed: number) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

type Orbit = { prn: number; raan: number; phase: number; cn0Offset: number };

export class Constellation {
  readonly seed: number;
  private readonly orbits: Orbit[];

  constructor(seed = 1) {
    this.seed = seed;
    const next = seededRandom(seed);
    const orbits: Orbit[] = [];
    let prn = 1;
    PER_PLANE.forEach((count, plane) => {
      const raan = (plane * 2 * Math.PI) / PER_PLANE.length + rad((next() - 0.5) * 4);
      for (let slot = 0; slot < count; slot += 1) {
        // Evenly spaced in the plane, the planes staggered, each with a small seeded offset.
        const phase = (slot * 2 * Math.PI) / count + (plane * Math.PI) / (PER_PLANE.length * 2) + rad((next() - 0.5) * 10);
        orbits.push({ prn, raan, phase, cn0Offset: (next() - 0.5) * 2 });
        prn += 1;
      }
    });
    this.orbits = orbits;
  }

  get count() { return this.orbits.length; }

  /** Every satellite seen from a place (altitude in feet) at a time (ms since 1970), with the antenna mask in degrees. */
  sky(time: number, at: LatLon, altitudeFt: number, attitude: Attitude, maskDeg = 5): SkySatellite[] {
    const t = (time - EPOCH) / 1000;
    const lat = rad(at.lat), lon = rad(at.lon);
    const r = EARTH_RADIUS_M + altitudeFt * 0.3048;
    const rx = [r * Math.cos(lat) * Math.cos(lon), r * Math.cos(lat) * Math.sin(lon), r * Math.sin(lat)];
    const east = [-Math.sin(lon), Math.cos(lon), 0];
    const north = [-Math.sin(lat) * Math.cos(lon), -Math.sin(lat) * Math.sin(lon), Math.cos(lat)];
    const up = [Math.cos(lat) * Math.cos(lon), Math.cos(lat) * Math.sin(lon), Math.sin(lat)];
    const normal = antennaNormal(attitude);
    return this.orbits.map(orbit => {
      const u = orbit.phase + (2 * Math.PI * t) / ORBIT_PERIOD_S;
      // In the orbital plane, inclined, turned to the plane's node, and into the rotating Earth frame.
      const xp = ORBIT_RADIUS_M * Math.cos(u), yp = ORBIT_RADIUS_M * Math.sin(u);
      const yi = yp * Math.cos(INCLINATION), zi = yp * Math.sin(INCLINATION);
      const node = orbit.raan - EARTH_RATE * t;
      const sat = [xp * Math.cos(node) - yi * Math.sin(node), xp * Math.sin(node) + yi * Math.cos(node), zi];
      const d = [sat[0] - rx[0], sat[1] - rx[1], sat[2] - rx[2]];
      const range = Math.hypot(d[0], d[1], d[2]);
      const e = dot(d, east) / range, n = dot(d, north) / range, v = dot(d, up) / range;
      const elevation = deg(Math.asin(v));
      const antennaElevation = deg(Math.asin(e * normal[0] + n * normal[1] + v * normal[2]));
      // Stronger overhead, weaker toward the antenna's horizon: a parameter, not a correlator output.
      const cn0 = 45 + orbit.cn0Offset - 10 * (1 - Math.sin(rad(Math.max(0, antennaElevation))));
      return {
        prn: orbit.prn, elevation, azimuth: (deg(Math.atan2(e, n)) + 360) % 360, antennaElevation, cn0,
        visible: elevation >= 0 && antennaElevation >= maskDeg, los: [e, n, v] as [number, number, number],
      };
    });
  }
}

/**
 * The antenna's boresight (the top of the fuselage) in east, north, up: straight up when level, tilted toward the low
 * wing in a bank, and toward the tail with the nose up.
 */
function antennaNormal({ bank, pitch, heading }: Attitude): [number, number, number] {
  const phi = rad(bank), theta = rad(pitch), psi = rad(heading);
  // The body z axis (down) in north, east, down; the antenna points the other way.
  const zn = Math.cos(phi) * Math.sin(theta) * Math.cos(psi) + Math.sin(phi) * Math.sin(psi);
  const ze = Math.cos(phi) * Math.sin(theta) * Math.sin(psi) - Math.sin(phi) * Math.cos(psi);
  const zd = Math.cos(phi) * Math.cos(theta);
  return [-ze, -zn, zd];
}
