import { bearingDeg, distanceNm, type LatLon } from "./fmsModel";
import { seededRandom, type Attitude, type Constellation, type SkySatellite } from "./gnss";

/**
 * A simulated CMC CMA-5024 GPS/SBAS landing system sensor unit (GLSSU): one receiver, publishing an ARINC 743A-style
 * bus of labelled words, each with its sign/status matrix (SSM), and nothing else. The FMS will read only that bus.
 * Phase 2 adds SBAS: SBAS NAV and SBAS PA, the FAS data block from the FMS, and the approach guidance the GPS
 * computes from it. Dual GPS is phase 3.
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
 *   engineering units (assumptions noted per label);
 * - SBAS corrections scale the range errors down (SBAS_RESIDUAL), and the SBAS protection levels take the DO-229 form
 *   (a weighted position covariance and its K factors) with per-satellite error bounds whose values are laboratory
 *   parameters, not broadcast UDRE and GIVE data. The geostationary satellites are not used for ranging.
 */

export type Ssm = "NORMAL" | "NCD" | "FT" | "FW";
/** A word: its value, null unless the status is NORMAL (or forced), and its SSM. Like efis.ts's Word, with SSM. */
export type Word<T> = { value: T | null; ssm: Ssm };
export type GpsMode = "SELF_TEST" | "INITIALIZATION" | "ACQUISITION" | "NAV" | "SBAS_NAV" | "SBAS_PA" | "ALT_AIDING" | "FAULT";
// Not modelled: "AIDED" (coasting on inertial inputs) and "DATA_LOAD".
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
export type SatelliteStatus = {
  prn: number; elevation: number; azimuth: number; cn0: number; tracked: boolean; used: boolean; excluded: boolean; ephemeris: boolean;
  /** An SBAS geostationary satellite (PRN 120-158): corrections and integrity, not used here as a ranging source. */
  sbas: boolean;
};
/**
 * The SBAS final approach segment (FAS) data block, as DO-229 Appendix D lists its fields, which the FMS sends to the
 * GPS. Units here are engineering units (degrees, metres, feet), not the block's bit encoding. The CRC is CRC-32Q over
 * this module's serialization of the fields (fasCrc), not over the DO-229 bit packing.
 */
export type FasDataBlock = {
  operationType: number; sbasProvider: number; airport: string; runway: number; designator: "L" | "R" | "C" | "";
  performance: number; routeIndicator: string; referencePathSelector: number; referencePathId: string;
  /** Landing threshold point: position and height above the ellipsoid, m. */
  ltp: { lat: number; lon: number; heightM: number };
  /** Flight path alignment point, as its offset from the LTP in degrees. */
  fpapDelta: { lat: number; lon: number };
  tchFt: number; gpaDeg: number; courseWidthM: number; lengthOffsetM: number;
  /** The approach's horizontal and vertical alert limits, m. */
  halM: number; valM: number;
  crc: number;
};
/** What the FMS selects: the approach identifier it chose, and the FAS block it sent (null until it has). */
export type ApproachSelection = { id: string; fas: FasDataBlock | null; parked?: boolean };
/** Label 156, approach selection status. */
export type ApproachStatus = { selected: boolean; available: boolean; crcInvalid: boolean; mismatch: boolean; incomplete: boolean; parked: boolean };
export type ApproachLevel = "LPV" | "LNAV/VNAV" | "LNAV" | "NONE";
/** Label 305, SBAS PA mode and service provider; the approach level it can support is an assumption of this model. */
export type SbasStatus = { paActive: boolean; provider: string | null; level: ApproachLevel };
/** The bench's SBAS conditions: a "do not use" broadcast, geostationary satellites out, and an ionospheric storm factor. */
export type SbasState = { doNotUse: boolean; outage: number[]; ionoStorm: number };
type NumberLabel = "110" | "120" | "111" | "121" | "076" | "370" | "103" | "112" | "165" | "166" | "174" | "101" | "102" | "130" | "133" | "247" | "136" | "116" | "117" | "201";
/**
 * The output bus. Units (engineering values; the bit layouts are not modelled):
 * 110/120 latitude and 111/121 longitude in degrees, coarse (to 180/2^20) and the fine remainder; 076 altitude MSL
 * and 370 height above the ellipsoid, ft; 103 true track, deg; 112 ground speed, kt; 165 vertical velocity, fpm;
 * 166 north and 174 east velocity, kt; 101 HDOP and 102 VDOP; 130 HIL and 247 HFOM, NM; 133 VIL and 136 VFOM, ft;
 * 150 UTC; 260 date; 273 status; 355 fault summary; 060 per satellite; 116 lateral and 117 vertical rectilinear
 * deviation from the FAS path, ft (positive right of the landing course, and above the path); 201 distance to the
 * threshold, NM; 156 approach selection status; 305 SBAS PA status.
 */
export type GpsBus = { [L in NumberLabel]: Word<number> } & {
  "156": Word<ApproachStatus>;
  "305": Word<SbasStatus>;
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
  /** SBAS on (the unit's normal state), and how long a geostationary satellite must be tracked before its corrections are in, s: a laboratory parameter. */
  sbas?: boolean;
  sbasAcquireSeconds?: number;
};

const SELF_TEST_S = 10;
/** 20 of the 24 channels are GPS (the other 4 SBAS, phase 2). */
const GPS_CHANNELS = 20;
const K_MISSED = 3.09;
const M_PER_DEG_LAT = 111_120;
const LAT_RESOLUTION = 180 / 2 ** 20;
const NUMBER_LABELS: NumberLabel[] = ["110", "120", "111", "121", "076", "370", "103", "112", "165", "166", "174", "101", "102", "130", "133", "247", "136", "116", "117", "201"];

type Satellite = SkySatellite & { tracked: boolean; used: boolean; excluded: boolean; sbas: boolean };
type Solution = { enu: [number, number, number]; hdop: number; vdop: number; hpl: number | null; vpl: number | null; detected: boolean; sigma: number };

/** The fraction of the range error left after SBAS corrections (clock, ephemeris, ionosphere): a laboratory value. */
const SBAS_RESIDUAL = 0.35;
/** DO-229 K factors: horizontal for non-precision (SBAS NAV) and precision approach, and vertical. */
const K_H_NPA = 6.18, K_H_PA = 6.0, K_V_PA = 5.33;
/** The horizontal alert limit that LNAV and LNAV/VNAV need (0.3 NM, 556 m), and LNAV/VNAV's vertical limit, m. */
const HAL_LNAV_M = 556, VAL_LNAV_VNAV_M = 50;
/** The Earth radius distanceNm uses, NM. */
const EARTH_RADIUS_NM = 3440.065;
const PROVIDERS = ["WAAS", "EGNOS", "MSAS", "GAGAN", "SDCM"];

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
  private sbasState: SbasState = { doNotUse: false, outage: [], ionoStorm: 1 };
  private approach: ApproachSelection | null = null;
  /** Since when a geostationary satellite has been tracked without a break: its corrections take sbasAcquireSeconds. */
  private sbasSince: number | null = null;

  constructor(options: GpsOptions) {
    this.o = {
      ttffSeconds: 45, initSeconds: 2, sigmaUere: 1.5, maskDeg: 5, trackCn0: 30, falseAlert: 1e-5, geoidSeparation: -32, seed: options.constellation.seed, sbas: true, sbasAcquireSeconds: 30,
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

  /** The bench's SBAS conditions; fields not given keep their value. */
  setSbas(state: Partial<SbasState>) { this.sbasState = { ...this.sbasState, ...state }; }

  /** The approach the FMS selects, with its FAS block; null deselects it. */
  selectApproach(selection: ApproachSelection | null) { this.approach = selection; }

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
        const used = satellites.filter(s => s.used && !s.sbas).length;
        const baro = input.baroAltitude !== null;
        const canFix = this.ephemeris || elapsed >= this.o.ttffSeconds;
        this.currentMode = canFix && used >= 4 ? "NAV" : canFix && this.ephemeris && used === 3 && baro ? "ALT_AIDING" : "ACQUISITION";
        if (this.currentMode === "NAV") this.ephemeris = true;
        const geoTracked = satellites.some(s => s.sbas && s.tracked);
        this.sbasSince = geoTracked ? this.sbasSince ?? input.time : null;
        const sbas = this.o.sbas && !this.sbasState.doNotUse && this.sbasSince !== null && input.time - this.sbasSince >= this.o.sbasAcquireSeconds * 1000;
        if (this.currentMode === "NAV" && sbas) this.currentMode = this.approachStatus().available && !this.approach?.parked ? "SBAS_PA" : "SBAS_NAV";
        if (this.currentMode !== "ACQUISITION") {
          solution = this.solve(input, satellites);
          // The exclusion may have changed which satellites are used.
          satellites = satellites.map(s => (s.sbas ? s : { ...s, excluded: this.excluded.has(s.prn), used: s.used && !this.excluded.has(s.prn) }));
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
    const gps = sky.sort((a, b) => a.prn - b.prn).map(s => {
      const tracked = trackable.includes(s.prn);
      return { ...s, tracked, excluded: this.excluded.has(s.prn), used: tracked && !this.excluded.has(s.prn), sbas: false };
    });
    // The SBAS channels: a geostationary satellite in view, strong enough, not deselected and not in an outage.
    const geos = this.o.sbas ? this.o.constellation.geos(input.position, input.altitude, input.attitude, this.o.maskDeg).filter(s => s.visible) : [];
    return [...gps, ...geos.map(s => {
      const tracked = !noSignal && s.cn0 >= this.o.trackCn0 && !this.deselected.has(s.prn) && !this.sbasState.outage.includes(s.prn);
      return { ...s, tracked, excluded: false, used: false, sbas: true };
    })];
  }

  /** The range error on a satellite now: seeded noise of sigmaUere RMS, plus any injected fault. */
  private rangeError(prn: number, time: number, corrected: boolean) {
    const next = seededRandom(this.o.seed * 1000 + prn);
    const a = next() * 2 * Math.PI, b = next() * 2 * Math.PI, t = time / 1000;
    const noise = this.o.sigmaUere * Math.SQRT2 * (0.6 * Math.sin((2 * Math.PI * t) / 300 + a) + 0.8 * Math.sin((2 * Math.PI * t) / 77 + b));
    const entry = this.satelliteFaults.get(prn);
    const fault = !entry ? 0 : entry.fault.kind === "STEP" ? entry.fault.metres : entry.fault.metresPerSecond * (time - (entry.since ?? time)) / 1000;
    return noise * (corrected ? SBAS_RESIDUAL : 1) + fault;
  }

  /**
   * The fix error and integrity from the geometry: least squares of the range errors through the line-of-sight matrix,
   * the RAIM residual test, fault exclusion (FDE) when there is redundancy for it, and slope-based HPL and VPL. With three
   * satellites and baro, the baro altitude error is the fourth measurement (altitude aiding), with no redundancy.
   */
  private solve(input: GpsInput, satellites: Satellite[]): Solution {
    const sbas = this.currentMode === "SBAS_NAV" || this.currentMode === "SBAS_PA";
    const sigma = this.o.sigmaUere * (sbas ? SBAS_RESIDUAL : 1);
    const used = satellites.filter(s => s.used && !s.sbas);
    const rows = used.map(s => ({ h: [-s.los[0], -s.los[1], -s.los[2], 1], e: this.rangeError(s.prn, input.time, sbas), prn: s.prn, elevation: s.elevation }));
    if (used.length === 3 && input.baroAltitude !== null) rows.push({ h: [0, 0, 1, 0], e: (input.baroAltitude - input.altitude) * 0.3048, prn: 0, elevation: 90 });
    let fit = leastSquares(rows);
    let detected = fit.dof > 0 && fit.statistic / sigma > this.threshold(fit.dof);
    // FDE: with at least two degrees of freedom, drop the one satellite whose removal leaves a consistent set.
    if (detected && fit.dof >= 2) {
      let best: { prn: number; fit: Fit } | null = null;
      for (const row of rows) {
        if (row.prn === 0) continue;
        const without = leastSquares(rows.filter(r => r !== row));
        if (without.statistic / sigma <= this.threshold(without.dof) && (!best || without.statistic < best.fit.statistic)) best = { prn: row.prn, fit: without };
      }
      if (best) { this.excluded.add(best.prn); fit = best.fit; detected = false; }
    }
    const hdop = Math.sqrt(fit.q[0][0] + fit.q[1][1]), vdop = Math.sqrt(fit.q[2][2]);
    const enu: [number, number, number] = [fit.x[0], fit.x[1], fit.x[2]];
    if (sbas) {
      // SBAS protection levels from the weighted covariance of the satellites in the (possibly FDE-reduced) solution.
      const inSolution = rows.filter(r => r.prn === 0 || !this.excluded.has(r.prn));
      const d = invert4([0, 1, 2, 3].map(i => [0, 1, 2, 3].map(j => inSolution.reduce((sum, r) => sum + (r.h[i] * r.h[j]) / this.sbasVariance(r.elevation), 0))));
      const half = (d[0][0] + d[1][1]) / 2, diff = (d[0][0] - d[1][1]) / 2;
      const major = Math.sqrt(half + Math.sqrt(diff * diff + d[0][1] * d[0][1]));
      const k = this.currentMode === "SBAS_PA" ? K_H_PA : K_H_NPA;
      return { enu, hdop, vdop, hpl: k * major, vpl: K_V_PA * Math.sqrt(d[2][2]), detected, sigma };
    }
    const pbias = sigma * (this.threshold(fit.dof) + K_MISSED);
    return { enu, hdop, vdop, hpl: fit.dof > 0 ? fit.hslope * pbias : null, vpl: fit.dof > 0 ? fit.vslope * pbias : null, detected, sigma };
  }

  /**
   * The SBAS error bound on one satellite's range, m²: fast and long-term corrections, the ionosphere (its vertical bound
   * mapped to the elevation, times the storm factor), the airborne receiver and multipath, and the troposphere (the
   * DO-229 form). The values are laboratory parameters, not broadcast UDRE and GIVE data.
   */
  private sbasVariance(elevation: number) {
    const el = (Math.max(elevation, 5) * Math.PI) / 180;
    const obliquity = 1 / Math.sqrt(1 - ((6371 * Math.cos(el)) / (6371 + 350)) ** 2);
    const flt = 0.75, iono = 0.5 * obliquity * this.sbasState.ionoStorm, air = 0.5;
    const tropo = (0.12 * 1.001) / Math.sqrt(0.002001 + Math.sin(el) ** 2);
    return flt ** 2 + iono ** 2 + air ** 2 + tropo ** 2;
  }

  /** Label 156: the FMS's approach selection against the FAS block it sent. */
  private approachStatus(): ApproachStatus {
    const selection = this.approach, fas = selection?.fas ?? null;
    const crcInvalid = fas !== null && fasCrc(fas) !== fas.crc;
    const mismatch = fas !== null && !crcInvalid && fas.referencePathId !== selection!.id;
    return {
      selected: selection !== null, available: fas !== null && !crcInvalid && !mismatch, crcInvalid, mismatch,
      incomplete: selection !== null && fas === null, parked: selection?.parked ?? false,
    };
  }

  /**
   * The approach level the GPS can support: LPV in SBAS PA within the FAS block's alert limits; LNAV/VNAV with SBAS within
   * 556 m and 50 m; LNAV within 556 m (0.3 NM); none when integrity is lost.
   */
  private approachLevel(s: Solution | null): ApproachLevel {
    if (!s || s.detected || s.hpl === null) return "NONE";
    const fas = this.approachStatus().available ? this.approach!.fas! : null;
    const vpl = s.vpl ?? Infinity;
    if (this.currentMode === "SBAS_PA" && fas && s.hpl <= fas.halM && vpl <= fas.valM) return "LPV";
    if ((this.currentMode === "SBAS_NAV" || this.currentMode === "SBAS_PA") && s.hpl <= HAL_LNAV_M && vpl <= VAL_LNAV_VNAV_M) return "LNAV/VNAV";
    return s.hpl <= HAL_LNAV_M ? "LNAV" : "NONE";
  }

  /** The RAIM test threshold on the normalized residual for the degrees of freedom, from the false alert probability. */
  private threshold(dof: number) {
    if (dof <= 0) return Infinity;
    // Wilson-Hilferty for the chi-square quantile, z the normal quantile of 1 - falseAlert.
    const z = normalQuantile(1 - this.o.falseAlert);
    const c = 2 / (9 * dof);
    return Math.sqrt(dof * (1 - c + z * Math.sqrt(c)) ** 3);
  }

  /**
   * The rectilinear deviations from the FAS final approach path, from the GPS's own fix (latitude, longitude, height
   * above the ellipsoid in metres): 116 lateral (ft, positive right of the landing course), 117 vertical (ft, positive
   * above the path, which rises from the threshold crossing height at the glide path angle) and 201 distance to the
   * threshold (NM). None without an active approach: nothing selected, the block invalid, or the approach parked.
   */
  private deviations(lat: number, lon: number, heightM: number): Record<"116" | "117" | "201", number | null> {
    const status = this.approachStatus();
    const fas = this.approach?.fas;
    if (!status.available || status.parked || !fas) return { "116": null, "117": null, "201": null };
    const ltp = { lat: fas.ltp.lat, lon: fas.ltp.lon };
    // On the sphere, from the LTP: the cross-track distance from the course line, and the distance along it.
    const course = bearingDeg(ltp, { lat: ltp.lat + fas.fpapDelta.lat, lon: ltp.lon + fas.fpapDelta.lon });
    const d = distanceNm(ltp, { lat, lon }) / EARTH_RADIUS_NM, angle = ((bearingDeg(ltp, { lat, lon }) - course) * Math.PI) / 180;
    const right = Math.asin(Math.sin(d) * Math.sin(angle)) * EARTH_RADIUS_NM;
    const before = -Math.atan2(Math.tan(d) * Math.cos(angle), 1) * EARTH_RADIUS_NM;
    const path = fas.ltp.heightM + fas.tchFt * 0.3048 + before * 1852 * Math.tan((fas.gpaDeg * Math.PI) / 180);
    return { "116": right * 6076.12, "117": (heightM - path) / 0.3048, "201": before };
  }

  private assemble(input: GpsInput | null, satellites: Satellite[], s: Solution | null): GpsBus {
    const mode = this.currentMode;
    const navSsm: Ssm = mode === "SELF_TEST" ? "FT" : mode === "FAULT" ? "FW" : s ? "NORMAL" : "NCD";
    const word = <T>(value: T): Word<T> => (navSsm === "NORMAL" ? { value, ssm: "NORMAL" } : { value: null, ssm: navSsm });
    const bus: Partial<GpsBus> = {};
    const level = this.approachLevel(s);
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
        "247": (2 * s.hdop * s.sigma) / 1852, "136": (2 * s.vdop * s.sigma) / 0.3048,
        ...this.deviations(lat, lon, (msl + this.o.geoidSeparation / 0.3048) * 0.3048),
      };
      for (const label of NUMBER_LABELS) {
        const value = values[label];
        // The integrity limits: failure warning when a fault is detected and cannot be excluded, no computed data
        // without the redundancy for RAIM.
        // The deviations: failure warning on a navigation alert (no vertical guidance below LNAV/VNAV, none at all
        // without a level).
        const alert = (label === "130" || label === "133") && s.detected || label === "116" && level === "NONE" || label === "117" && (level === "NONE" || level === "LNAV");
        bus[label] = value === null ? { value: null, ssm: "NCD" } : alert ? { value: null, ssm: "FW" } : word(value);
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
      value: { mode, used: satellites.filter(sat => sat.used).length, visible: satellites.filter(sat => !sat.sbas).length, baroAiding: baro && mode !== "FAULT", integrity: this.integrity },
      ssm: "NORMAL",
    };
    bus["156"] = { value: this.approachStatus(), ssm: "NORMAL" };
    const sbasMode = mode === "SBAS_NAV" || mode === "SBAS_PA";
    const provider = !sbasMode ? null : PROVIDERS[this.approach?.fas?.sbasProvider ?? 0] ?? "ANY";
    bus["305"] = { value: { paActive: mode === "SBAS_PA", provider, level }, ssm: "NORMAL" };
    bus["355"] = {
      value: { unit: mode === "FAULT", rfInput: this.faults.has("RF_INPUT"), buses: { irsFms: false, airData: input !== null && !baro, crossTalk: false, ils: false, dme: false } },
      ssm: "NORMAL",
    };
    bus["060"] = satellites.map(sat => ({
      value: { prn: sat.prn, elevation: sat.elevation, azimuth: sat.azimuth, cn0: sat.cn0, tracked: sat.tracked, used: sat.used, excluded: sat.excluded, ephemeris: this.ephemeris, sbas: sat.sbas },
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

/** CRC-32Q (polynomial 0x814141AB, initial value 0, not reflected, no final XOR), the CRC of the SBAS FAS data block. */
export function crc32q(bytes: Uint8Array): number {
  let crc = 0;
  for (const byte of bytes) {
    crc = (crc ^ (byte << 24)) >>> 0;
    for (let bit = 0; bit < 8; bit += 1) crc = (crc & 0x80000000 ? ((crc << 1) ^ 0x814141ab) : crc << 1) >>> 0;
  }
  return crc;
}

/** The FAS block's CRC, over its fields in DO-229 order (this module's serialization, not the DO-229 bit packing). */
export function fasCrc(fas: Omit<FasDataBlock, "crc">): number {
  const fields = [
    fas.operationType, fas.sbasProvider, fas.airport, fas.runway, fas.designator, fas.performance, fas.routeIndicator,
    fas.referencePathSelector, fas.referencePathId, fas.ltp.lat, fas.ltp.lon, fas.ltp.heightM, fas.fpapDelta.lat, fas.fpapDelta.lon,
    fas.tchFt, fas.gpaDeg, fas.courseWidthM, fas.lengthOffsetM, fas.halM, fas.valM,
  ];
  return crc32q(new TextEncoder().encode(JSON.stringify(fields)));
}
