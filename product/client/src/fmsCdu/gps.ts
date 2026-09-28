import type { LatLon } from "./fmsModel";
import { seededRandom, type Attitude, type Constellation, type SkySatellite } from "./gnss";

/**
 * A simulated CMC CMA-5024 GPS/SBAS landing system sensor unit (GLSSU): one receiver, publishing an ARINC 743A-style
 * bus of labelled words, each with its sign/status matrix (SSM), and nothing else. The FMS will read only that bus.
 * Phase 1: GPS only. SBAS NAV, SBAS PA and the LPV deviations are phase 2 (TODO), dual GPS phase 3.
 *
 * Behaviour follows the CMA-5024 installation manual as publicly visible (operating modes, output labels, SSM rules;
 * see product/docs/FMS_TEST_BENCH.md for the sources). It is a SIMULATION, not the certified receiver:
 * - the position is the truth plus an error projected from per-satellite range errors through the satellite geometry
 *   (least squares on the errors, not a pseudorange solution), so DOP, RAIM, FDE and the protection levels come from
 *   the geometry, and a satellite fault moves the fix as it would;
 * - range errors are a deterministic, seeded, slowly varying noise of `sigmaUere` metres, plus any injected fault;
 * - RAIM is the classic residual test with slope-based protection levels, its false alert and missed detection
 *   probabilities laboratory parameters;
 * - velocity is the truth (Doppler noise is not modelled), and bit-level word layouts are not modelled: values are
 *   engineering units (assumptions noted per label).
 */

export type Ssm = "NORMAL" | "NCD" | "FT" | "FW";
/** A word: its value, null unless the status is NORMAL (or forced), and its SSM. Like efis.ts's Word, with SSM. */
export type Word<T> = { value: T | null; ssm: Ssm };
export type GpsMode = "SELF_TEST" | "INITIALIZATION" | "ACQUISITION" | "NAV" | "ALT_AIDING" | "FAULT";
// TODO phase 2: "SBAS_NAV" | "SBAS_PA", and "AIDED" (coasting on inertial inputs).
export type GpsInput = {
  /** ms since 1970 (UTC). */
  time: number;
  /** Where the aircraft truly is: altitude MSL in feet. */
  position: LatLon;
  altitude: number;
  /** Pressure altitude from air data, feet; null when the input is lost. */
  baroAltitude: number | null;
  /** True track (degrees), ground speed (kt) and vertical speed (fpm). */
  track: number;
  groundSpeed: number;
  verticalSpeed: number;
  attitude: Attitude;
};
export type Integrity = "OK" | "DETECTED" | "UNAVAILABLE";
/** Label 273: operating mode, satellites used and visible, aiding sources, and the RAIM state. */
export type GpsStatus = { mode: GpsMode; used: number; visible: number; baroAiding: boolean; integrity: Integrity };
/** Label 355: a fault isolated to the unit, to the RF input (antenna or cable), and a flag per input bus. */
export type FaultSummary = { unit: boolean; rfInput: boolean; buses: { irsFms: boolean; airData: boolean; crossTalk: boolean; ils: boolean; dme: boolean } };
/** Label 060, one per satellite in view: measurement status. */
export type SatelliteStatus = { prn: number; elevation: number; azimuth: number; cn0: number; tracked: boolean; used: boolean; excluded: boolean; ephemeris: boolean };
type NumberLabel = "110" | "120" | "111" | "121" | "076" | "370" | "103" | "112" | "165" | "166" | "174" | "101" | "102" | "130" | "133" | "247" | "136";
/**
 * The output bus. Units (engineering values; the bit layouts are not modelled):
 * 110/120 latitude and 111/121 longitude in degrees, coarse (to 180/2^20) and the fine remainder; 076 altitude MSL
 * and 370 height above the ellipsoid, ft; 103 true track, deg; 112 ground speed, kt; 165 vertical velocity, fpm;
 * 166 north and 174 east velocity, kt; 101 HDOP and 102 VDOP; 130 HIL and 247 HFOM, NM; 133 VIL and 136 VFOM, ft;
 * 150 UTC; 260 date; 273 status; 355 fault summary; 060 per satellite.
 */
export type GpsBus = { [L in NumberLabel]: Word<number> } & {
  "150": Word<{ hours: number; minutes: number; seconds: number }>;
  "260": Word<{ day: number; month: number; year: number }>;
  "273": Word<GpsStatus>;
  "355": Word<FaultSummary>;
  "060": Word<SatelliteStatus>[];
};
export type GpsLabel = Exclude<keyof GpsBus, "060">;
export type SatelliteFault = { kind: "RAMP"; metresPerSecond: number } | { kind: "STEP"; metres: number };
export type ReceiverFault = "RECEIVER" | "RF_INPUT" | "STOP_TRANSMITTING";
/** The override layer: force a word's value and/or SSM, freeze it, bias it, or ramp it from when it was set. */
export type Override = { kind: "FORCE"; value?: number; ssm?: Ssm } | { kind: "FREEZE" } | { kind: "BIAS"; amount: number } | { kind: "RAMP"; perSecond: number };

export type GpsOptions = {
  constellation: Constellation;
  /** Time to first fix from power-up, s: under 75 s (95%) per the datasheet. */
  ttffSeconds?: number;
  /** Initialization after the 10 s self-test, s: a laboratory parameter. */
  initSeconds?: number;
  /** One-sigma range error per satellite, m. */
  sigmaUere?: number;
  maskDeg?: number;
  /** Tracking threshold, dB-Hz. */
  trackCn0?: number;
  /** RAIM false alert probability per test, and the missed detection multiplier (3.09 for 1e-3): laboratory values. */
  falseAlert?: number;
  /** Geoid separation (ellipsoid above mean sea level), m, for label 370. */
  geoidSeparation?: number;
  seed?: number;
};

const SELF_TEST_S = 10;
/** 20 of the 24 channels are GPS (the other 4 SBAS, phase 2). */
const GPS_CHANNELS = 20;
const K_MISSED = 3.09;
const M_PER_DEG_LAT = 111_120;
const LAT_RESOLUTION = 180 / 2 ** 20;
const NUMBER_LABELS: NumberLabel[] = ["110", "120", "111", "121", "076", "370", "103", "112", "165", "166", "174", "101", "102", "130", "133", "247", "136"];

type Satellite = SkySatellite & { tracked: boolean; used: boolean; excluded: boolean };
type Solution = { enu: [number, number, number]; hdop: number; vdop: number; hpl: number | null; vpl: number | null; detected: boolean };

export class GpsReceiver {
  private readonly o: Required<GpsOptions>;
  private powerOn: number | null = null;
  private currentMode: GpsMode = "SELF_TEST";
  /** Ephemeris held: once there has been a fix, reacquisition needs no new time to first fix. */
  private ephemeris = false;
  private deselected = new Set<number>();
  private excluded = new Set<number>();
  private satelliteFaults = new Map<number, { fault: SatelliteFault; since: number | null }>();
  private faults = new Set<ReceiverFault>();
  private overrides = new Map<GpsLabel, { override: Override; since: number | null; frozen: Word<unknown> | null }>();
  private raw: GpsBus;
  private now = 0;
  private integrity: Integrity = "UNAVAILABLE";

  constructor(options: GpsOptions) {
    this.o = {
      ttffSeconds: 45, initSeconds: 2, sigmaUere: 1.5, maskDeg: 5, trackCn0: 30, falseAlert: 1e-5, geoidSeparation: -32, seed: options.constellation.seed,
      ...options,
    };
    this.raw = this.assemble(null, [], null);
  }

  get mode() { return this.currentMode; }
  /** The 28 V fault discrete: active in Fault mode. */
  get faultDiscrete() { return this.currentMode === "FAULT"; }

  /** Satellite deselection (labels 146/170, or terrain masking on the bench): these PRNs are not tracked. */
  deselect(prns: number[]) { this.deselected = new Set(prns); }

  /** A range error on one satellite: a ramp from the next step, or a step. null clears it (and any exclusion). */
  satelliteFault(prn: number, fault: SatelliteFault | null) {
    if (fault) this.satelliteFaults.set(prn, { fault, since: null });
    else { this.satelliteFaults.delete(prn); this.excluded.delete(prn); }
  }

  injectFault(kind: ReceiverFault, on: boolean) { if (on) this.faults.add(kind); else this.faults.delete(kind); }

  override(label: GpsLabel, override: Override | null) {
    if (override) this.overrides.set(label, { override, since: null, frozen: null });
    else this.overrides.delete(label);
  }

  /** What the receiver computes, before overrides. */
  rawBus(): GpsBus { return this.raw; }

  /** What the receiver transmits: the words after overrides, or null when it has stopped transmitting. */
  bus(): GpsBus | null {
    if (this.faults.has("STOP_TRANSMITTING")) return null;
    const out = { ...this.raw } as Record<string, unknown>;
    for (const [label, entry] of this.overrides) {
      const word = this.raw[label] as Word<unknown>;
      const { override } = entry;
      const elapsed = entry.since === null ? 0 : (this.now - entry.since) / 1000;
      const shift = (by: number): Word<unknown> => (typeof word.value === "number" ? { ...word, value: word.value + by } : word);
      out[label] = override.kind === "FORCE" ? { value: override.value ?? word.value, ssm: override.ssm ?? word.ssm }
        : override.kind === "FREEZE" ? entry.frozen ?? word
        : override.kind === "BIAS" ? shift(override.amount)
        : shift(override.perSecond * elapsed);
    }
    return out as GpsBus;
  }

  step(input: GpsInput) {
    this.now = input.time;
    if (this.powerOn === null) this.powerOn = input.time;
    // A receiver fault is Fault mode; clearing it restarts the unit from its self-test, ephemeris kept.
    if (this.faults.has("RECEIVER")) this.currentMode = "FAULT";
    else if (this.currentMode === "FAULT") { this.powerOn = input.time; this.currentMode = "SELF_TEST"; }
    for (const entry of this.satelliteFaults.values()) entry.since ??= input.time;

    let satellites: Satellite[] = [];
    let solution: Solution | null = null;
    if (this.currentMode !== "FAULT") {
      const elapsed = (input.time - this.powerOn) / 1000;
      if (elapsed < SELF_TEST_S) this.currentMode = "SELF_TEST";
      else if (elapsed < SELF_TEST_S + this.o.initSeconds) this.currentMode = "INITIALIZATION";
      else {
        satellites = this.track(input);
        const used = satellites.filter(s => s.used).length;
        const baro = input.baroAltitude !== null;
        const canFix = this.ephemeris || elapsed >= this.o.ttffSeconds;
        this.currentMode = canFix && used >= 4 ? "NAV" : canFix && this.ephemeris && used === 3 && baro ? "ALT_AIDING" : "ACQUISITION";
        if (this.currentMode === "NAV") this.ephemeris = true;
        if (this.currentMode === "NAV" || this.currentMode === "ALT_AIDING") {
          solution = this.solve(input, satellites);
          // The exclusion may have changed which satellites are used.
          satellites = satellites.map(s => ({ ...s, excluded: this.excluded.has(s.prn), used: s.used && !this.excluded.has(s.prn) }));
        }
      }
    }
    this.integrity = !solution ? "UNAVAILABLE" : solution.detected ? "DETECTED" : solution.hpl === null ? "UNAVAILABLE" : "OK";
    this.raw = this.assemble(input, satellites, solution);
    // Overrides freeze and ramp from the first step after they are set.
    for (const entry of this.overrides.values()) {
      if (entry.since === null) { entry.since = input.time; if (entry.override.kind === "FREEZE") entry.frozen = null; }
    }
    for (const [label, entry] of this.overrides) if (entry.override.kind === "FREEZE" && entry.frozen === null) entry.frozen = this.raw[label] as Word<unknown>;
  }

  /** The satellites in view, and which are tracked and used: signal above the threshold, not deselected, a channel free. */
  private track(input: GpsInput): Satellite[] {
    const sky = this.o.constellation.sky(input.time, input.position, input.altitude, input.attitude, this.o.maskDeg).filter(s => s.visible);
    const noSignal = this.faults.has("RF_INPUT");
    const trackable = sky.filter(s => !noSignal && s.cn0 >= this.o.trackCn0 && !this.deselected.has(s.prn))
      .sort((a, b) => b.elevation - a.elevation).slice(0, GPS_CHANNELS).map(s => s.prn);
    // An excluded satellite no longer tracked is forgotten.
    for (const prn of [...this.excluded]) if (!trackable.includes(prn)) this.excluded.delete(prn);
    return sky.sort((a, b) => a.prn - b.prn).map(s => {
      const tracked = trackable.includes(s.prn);
      return { ...s, tracked, excluded: this.excluded.has(s.prn), used: tracked && !this.excluded.has(s.prn) };
    });
  }

  /** The range error on a satellite now: seeded noise of sigmaUere RMS, plus any injected fault. */
  private rangeError(prn: number, time: number) {
    const next = seededRandom(this.o.seed * 1000 + prn);
    const a = next() * 2 * Math.PI, b = next() * 2 * Math.PI, t = time / 1000;
    const noise = this.o.sigmaUere * Math.SQRT2 * (0.6 * Math.sin((2 * Math.PI * t) / 300 + a) + 0.8 * Math.sin((2 * Math.PI * t) / 77 + b));
    const entry = this.satelliteFaults.get(prn);
    const fault = !entry ? 0 : entry.fault.kind === "STEP" ? entry.fault.metres : entry.fault.metresPerSecond * (time - (entry.since ?? time)) / 1000;
    return noise + fault;
  }

  /**
   * The fix error and integrity from the geometry: least squares of the range errors through the line-of-sight matrix,
   * the RAIM residual test, fault exclusion (FDE) when there is redundancy for it, and slope-based HPL and VPL. With three
   * satellites and baro, the baro altitude error is the fourth measurement (altitude aiding), with no redundancy.
   */
  private solve(input: GpsInput, satellites: Satellite[]): Solution {
    const used = satellites.filter(s => s.used);
    const rows = used.map(s => ({ h: [-s.los[0], -s.los[1], -s.los[2], 1], e: this.rangeError(s.prn, input.time), prn: s.prn }));
    if (used.length === 3 && input.baroAltitude !== null) rows.push({ h: [0, 0, 1, 0], e: (input.baroAltitude - input.altitude) * 0.3048, prn: 0 });
    let fit = leastSquares(rows);
    let detected = fit.dof > 0 && fit.statistic / this.o.sigmaUere > this.threshold(fit.dof);
    // FDE: with at least two degrees of freedom, drop the one satellite whose removal leaves a consistent set.
    if (detected && fit.dof >= 2) {
      let best: { prn: number; fit: Fit } | null = null;
      for (const row of rows) {
        if (row.prn === 0) continue;
        const without = leastSquares(rows.filter(r => r !== row));
        if (without.statistic / this.o.sigmaUere <= this.threshold(without.dof) && (!best || without.statistic < best.fit.statistic)) best = { prn: row.prn, fit: without };
      }
      if (best) { this.excluded.add(best.prn); fit = best.fit; detected = false; }
    }
    const pbias = this.o.sigmaUere * (this.threshold(fit.dof) + K_MISSED);
    return {
      enu: [fit.x[0], fit.x[1], fit.x[2]], hdop: Math.sqrt(fit.q[0][0] + fit.q[1][1]), vdop: Math.sqrt(fit.q[2][2]),
      hpl: fit.dof > 0 ? fit.hslope * pbias : null, vpl: fit.dof > 0 ? fit.vslope * pbias : null, detected,
    };
  }

  /** The RAIM test threshold on the normalized residual for the degrees of freedom, from the false alert probability. */
  private threshold(dof: number) {
    if (dof <= 0) return Infinity;
    // Wilson-Hilferty for the chi-square quantile, z the normal quantile of 1 - falseAlert.
    const z = normalQuantile(1 - this.o.falseAlert);
    const c = 2 / (9 * dof);
    return Math.sqrt(dof * (1 - c + z * Math.sqrt(c)) ** 3);
  }

  private assemble(input: GpsInput | null, satellites: Satellite[], s: Solution | null): GpsBus {
    const mode = this.currentMode;
    const navSsm: Ssm = mode === "SELF_TEST" ? "FT" : mode === "FAULT" ? "FW" : s ? "NORMAL" : "NCD";
    const word = <T>(value: T): Word<T> => (navSsm === "NORMAL" ? { value, ssm: "NORMAL" } : { value: null, ssm: navSsm });
    const bus: Partial<GpsBus> = {};
    if (input && s) {
      const lat = input.position.lat + s.enu[1] / M_PER_DEG_LAT;
      const lon = input.position.lon + s.enu[0] / (M_PER_DEG_LAT * Math.cos((input.position.lat * Math.PI) / 180));
      const coarse = (v: number) => Math.round(v / LAT_RESOLUTION) * LAT_RESOLUTION;
      const msl = input.altitude + s.enu[2] / 0.3048;
      const track = (input.track * Math.PI) / 180;
      const values: Record<NumberLabel, number | null> = {
        "110": coarse(lat), "120": lat - coarse(lat), "111": coarse(lon), "121": lon - coarse(lon),
        "076": msl, "370": msl + this.o.geoidSeparation / 0.3048, "103": input.track, "112": input.groundSpeed, "165": input.verticalSpeed,
        "166": input.groundSpeed * Math.cos(track), "174": input.groundSpeed * Math.sin(track), "101": s.hdop, "102": s.vdop,
        "130": s.hpl === null ? null : s.hpl / 1852, "133": s.vpl === null ? null : s.vpl / 0.3048,
        "247": (2 * s.hdop * this.o.sigmaUere) / 1852, "136": (2 * s.vdop * this.o.sigmaUere) / 0.3048,
      };
      for (const label of NUMBER_LABELS) {
        const value = values[label];
        // The integrity limits: failure warning when a fault is detected and cannot be excluded, no computed data
        // without the redundancy for RAIM.
        bus[label] = (label === "130" || label === "133") && s.detected ? { value: null, ssm: "FW" } : value === null ? { value: null, ssm: "NCD" } : word(value);
      }
    } else for (const label of NUMBER_LABELS) bus[label] = word(0);
    const tracked = satellites.filter(sat => sat.tracked).length;
    // Time comes from the satellites: valid once any is tracked.
    const timeSsm: Ssm = mode === "SELF_TEST" ? "FT" : mode === "FAULT" ? "FW" : input && (s || tracked > 0) ? "NORMAL" : "NCD";
    const date = input ? new Date(input.time) : null;
    bus["150"] = timeSsm === "NORMAL" && date ? { value: { hours: date.getUTCHours(), minutes: date.getUTCMinutes(), seconds: date.getUTCSeconds() }, ssm: "NORMAL" } : { value: null, ssm: timeSsm };
    bus["260"] = timeSsm === "NORMAL" && date ? { value: { day: date.getUTCDate(), month: date.getUTCMonth() + 1, year: date.getUTCFullYear() }, ssm: "NORMAL" } : { value: null, ssm: timeSsm };
    // Status and maintenance words stay Normal in every mode, Fault included, so the fault can be read.
    const baro = input?.baroAltitude != null;
    bus["273"] = {
      value: { mode, used: satellites.filter(sat => sat.used).length, visible: satellites.length, baroAiding: baro && mode !== "FAULT", integrity: this.integrity },
      ssm: "NORMAL",
    };
    bus["355"] = {
      value: { unit: mode === "FAULT", rfInput: this.faults.has("RF_INPUT"), buses: { irsFms: false, airData: input !== null && !baro, crossTalk: false, ils: false, dme: false } },
      ssm: "NORMAL",
    };
    bus["060"] = satellites.map(sat => ({
      value: { prn: sat.prn, elevation: sat.elevation, azimuth: sat.azimuth, cn0: sat.cn0, tracked: sat.tracked, used: sat.used, excluded: sat.excluded, ephemeris: this.ephemeris },
      ssm: "NORMAL",
    }));
    return bus as GpsBus;
  }
}

type Fit = { x: number[]; q: number[][]; statistic: number; dof: number; hslope: number; vslope: number };

/**
 * Least squares of the errors e through the rows h (east, north, up, clock): the solution error x, the covariance
 * factor Q = (HᵀH)⁻¹, the residual root-sum-square, and the largest horizontal and vertical slopes (fix error per unit
 * of residual that a bias on one measurement produces), which scale into the protection levels.
 */
function leastSquares(rows: { h: number[]; e: number }[]): Fit {
  const n = rows.length;
  const hth = [0, 1, 2, 3].map(i => [0, 1, 2, 3].map(j => rows.reduce((sum, r) => sum + r.h[i] * r.h[j], 0)));
  const q = invert4(hth);
  // S = Q Hᵀ: column k maps measurement k into the solution.
  const s = rows.map(r => [0, 1, 2, 3].map(i => q[i].reduce((sum, qij, j) => sum + qij * r.h[j], 0)));
  const x = [0, 1, 2, 3].map(i => rows.reduce((sum, r, k) => sum + s[k][i] * r.e, 0));
  let sse = 0, hslope = 0, vslope = 0;
  rows.forEach((r, k) => {
    const residual = r.e - r.h.reduce((sum, hk, i) => sum + hk * x[i], 0);
    sse += residual * residual;
    // The diagonal of I - H S for measurement k.
    const pkk = 1 - r.h.reduce((sum, hk, i) => sum + hk * s[k][i], 0);
    if (pkk > 1e-9) {
      hslope = Math.max(hslope, Math.hypot(s[k][0], s[k][1]) / Math.sqrt(pkk));
      vslope = Math.max(vslope, Math.abs(s[k][2]) / Math.sqrt(pkk));
    }
  });
  return { x, q, statistic: Math.sqrt(sse), dof: n - 4, hslope, vslope };
}

function invert4(m: number[][]): number[][] {
  const a = m.map((row, i) => [...row, ...[0, 1, 2, 3].map(j => (i === j ? 1 : 0))]);
  for (let c = 0; c < 4; c += 1) {
    let pivot = c;
    for (let r = c + 1; r < 4; r += 1) if (Math.abs(a[r][c]) > Math.abs(a[pivot][c])) pivot = r;
    [a[c], a[pivot]] = [a[pivot], a[c]];
    const p = a[c][c] || 1e-12;
    for (let j = 0; j < 8; j += 1) a[c][j] /= p;
    for (let r = 0; r < 4; r += 1) if (r !== c) { const f = a[r][c]; for (let j = 0; j < 8; j += 1) a[r][j] -= f * a[c][j]; }
  }
  return a.map(row => row.slice(4));
}

/** The standard normal quantile (Acklam's rational approximation). */
function normalQuantile(p: number) {
  const a = [-39.69683028665376, 220.9460984245205, -275.9285104469687, 138.357751867269, -30.66479806614716, 2.506628277459239];
  const b = [-54.47609879822406, 161.5858368580409, -155.6989798598866, 66.80131188771972, -13.28068155288572];
  const c = [-0.007784894002430293, -0.3223964580411365, -2.400758277161838, -2.549732539343734, 4.374664141464968, 2.938163982698783];
  const d = [0.007784695709041462, 0.3224671290700398, 2.445134137142996, 3.754408661907416];
  const q = Math.sqrt(-2 * Math.log(1 - p));
  return p > 0.97575
    ? -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1)
    : (() => {
      const r = p - 0.5, t = r * r;
      return (((((a[0] * t + a[1]) * t + a[2]) * t + a[3]) * t + a[4]) * t + a[5]) * r / (((((b[0] * t + b[1]) * t + b[2]) * t + b[3]) * t + b[4]) * t + 1);
    })();
}
