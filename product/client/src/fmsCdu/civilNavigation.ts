import { distanceNm, longitudeDelta, offset, type LatLon } from "./fmsModel";
import type { NavMode } from "./navigation";
import type { RadioFix } from "./radioNavigation";
import type { AirData } from "./sensorPorts";
import { accuracy95Isotropic, CIRCULAR_95, ELIGIBILITY, withinLimit, type SensorSolution } from "./sensorState";
import { chooseRadio } from "./sensorTransitions";
import { HELICOPTER_PROFILE, type AircraftProfile } from "./profile";

/** A receiver's position with its 95% accuracy (HFOM) and integrity bound (HIL) kept apart (plans F2, C1). ANP is the
 * 95% accuracy (DEC-150 item 6); the HIL is never presented as it. */
export type PositionMeasurement = { position: LatLon; accuracy95Nm: number | null; hilNm: number | null; receiver: 1 | 2;
  northKt: number | null; eastKt: number | null;
  /** Original receiver sample time; absent legacy metadata cannot qualify derived motion. */
  at?: number };
/** `anp` is the selected solution's 95% accuracy (plan C1), null when the sensor gives none (shown as dashes).
 * `gpsDependent` is its transitive GPS provenance (plan C1): a position, wind or prior derived from GPS carries it. */
export type CivilSolution = { position: LatLon; mode: NavMode; anp: number | null; gpsSource: 1 | 2 | null; gpsDependent: boolean;
  dmes: string[]; vor: string | null; uncertain: boolean; airValid: boolean;
  /**
   * Whether this update computed the wind: valid air data (TAS and heading) and a measured ground velocity (a GPS
   * with integrity and valid velocity words, or two radio fixes close enough in time). Otherwise the FMS cannot
   * compute the wind (M300 12-22), and dead reckoning carries the last one.
   */
  windComputed: boolean;
  /**
   * The low-speed regime (plan F10, Astra's amendment): valid air data with the indicated airspeed below the declared
   * limit. The wind is frozen there (M300 12-22: "the wind is frozen at low speed"), and dead reckoning from heading and
   * TAS is degraded: its accuracy grows at the declared no-motion rate.
   */
  lowSpeed: boolean;
  /** Each candidate this update weighed, with its availability, 95% accuracy, integrity and eligibility (plan F2). */
  sensors: SensorSolution[];
  /** The candidate navigated on: the entry of `sensors` whose mode is `mode`. */
  selected: SensorSolution };

const drSensor = (available: boolean, accuracy95Nm: number | null, gpsDependent: boolean): SensorSolution => ({ mode: "DR", available,
  accuracy95Nm, accuracyBasis: accuracy95Nm === null ? null : "laboratory", gpsDependent,
  integrityNm: null, naimComparisonNm: null, integrityBasis: "none", integrity: false, eligibility: ELIGIBILITY.DR });

/** M300 1-5, 12-23: the KALMAN mode carries navigation for about 2 minutes after GPS loss (DEC-150: 2 minutes). */
export const KALMAN_COAST_S = 120;
/** Laboratory (plan C2): the 1-sigma residual accelerometer bias after aiding, m/s², per axis: a random constant over
 * each coast (an AHRS-grade figure, not a CMA or APIRS value). */
export const APIRS_ACCEL_SIGMA_MS2 = 0.02;
/** Laboratory (plan C2): the 1-sigma error of the aiding GPS velocity, kt, per axis. */
export const AIDING_VELOCITY_SIGMA_KT = 0.2;
/** Laboratory: the DVS solution's 95% error grows by this fraction of the distance flown on it. */
export const DVS_DRIFT_FRACTION = 0.01;
const MS2_TO_KT_PER_S = 1.943844;
/**
 * Indicated airspeed from true airspeed at a pressure altitude, knots: TAS times the square root of the ICAO standard
 * atmosphere's density ratio in the troposphere, (1 - 6.8756e-6 h)^4.2559. The bench has no indicated-airspeed word,
 * so the low-speed limit (plan F10, in KIAS) is judged on this; compressibility is negligible below 100 kt.
 */
export function indicatedAirspeedKt(tasKt: number, altitudeFt: number) {
  const sigma = Math.pow(1 - 6.8756e-6 * Math.min(Math.max(altitudeFt, -2000), 36089), 4.2559);
  return tasKt * Math.sqrt(sigma);
}

/** A propagated mode entered from a solution with no known accuracy starts from the initialization value (1 NM). */
const UNKNOWN_START_NM = 1;

/** Earth-frame measurements the KALMAN and DVS modes use; the FMS rotates body axes with its heading before this. */
export type InertialInput = { northMs2: number; eastMs2: number };
export type DopplerInput = { northKt: number; eastKt: number };

/** The estimator has no aircraft-truth input. Values for uncertainty growth are declared bench assumptions. */
export class CivilNavigation {
  /** The emulated INS (M300 12-23): aided by GPS with integrity, then coasting on the APIRS accelerations. sigma0 is the
   * per-axis 1-sigma at aiding (plan C2): the aiding GPS's 95% accuracy / 2.448 (the isotropic case). */
  private kalman: { position: LatLon; north: number; east: number; coast: number; sigma0: number } | null = null;
  /** The DVS track since entry: its starting accuracy, the distance flown, and the start position's GPS dependency. */
  private dvsTrack: { startAnp: number; distance: number; gpsDependent: boolean } | null = null;
  private readonly equipment: { kalman: boolean; dvs: boolean };
  private solution: CivilSolution;
  private wind = { north: 0, east: 0 };
  private previousRadio: { position: LatLon; at: number; gpsDependent: boolean; qualifiedVelocity: boolean } | null = null;
  /** Whether the wind DR carries was computed from GPS velocity, or from radio fixes that were themselves GPS-dependent. */
  private windGpsDependent = false;
  private measuredWindSample: { north: number; east: number; at: number; gpsDependent: boolean } | null = null;
  private readonly parameters: AircraftProfile["parameters"];
  constructor(initial: LatLon, parameters: AircraftProfile["parameters"] = HELICOPTER_PROFILE.parameters,
    equipment: { kalman: boolean; dvs: boolean } = { kalman: false, dvs: false }) {
    this.parameters = parameters;
    this.equipment = equipment;
    const dr = drSensor(false, 1, false);
    this.solution = { position: { ...initial }, mode: "DR", anp: 1, gpsSource: null, gpsDependent: false, dmes: [], vor: null, uncertain: true, airValid: false, windComputed: false, lowSpeed: false,
      sensors: [dr], selected: dr };
  }
  get current(): CivilSolution { return structuredClone(this.solution); }
  get windEstimate() { return { ...this.wind }; }
  get measuredWind() { return this.measuredWindSample ? { ...this.measuredWindSample } : null; }
  accept(solution: CivilSolution, wind: { north: number; east: number }) {
    this.solution = structuredClone(solution);
    this.wind = { ...wind };
    // Peer/manual operational wind has no transferred measured stamp/dependency. It cannot borrow an earlier
    // computed wind's qualification. A later local sensor computation establishes its own sample.
    this.measuredWindSample = null;
  }
  /** KALMAN is available while aided, past its first minute and within its coast (M300 1-5, 12-24; DEC-150). */
  private kalmanAvailable(ready: boolean) { return this.equipment.kalman && ready && this.kalman !== null && this.kalman.coast <= KALMAN_COAST_S; }
  /**
   * The per-axis 1-sigma position error, NM (plan C2, DF-05): three independent contributors in quadrature, the same on
   * both axes: the aiding position, the aiding velocity (sigma_v t) and the residual bias (0.5 sigma_b t²).
   */
  private kalmanAxisSigma() {
    const k = this.kalman;
    if (!k) return Infinity;
    const t = k.coast, velocity = AIDING_VELOCITY_SIGMA_KT * t / 3600, bias = 0.5 * APIRS_ACCEL_SIGMA_MS2 * t * t / 1852;
    return Math.sqrt(k.sigma0 ** 2 + velocity ** 2 + bias ** 2);
  }
  /** KALMAN STATUS 2 SIGMA POS ERR (M300 12-24): 2 sigma; not a 95% radial figure (plan C1). */
  get kalmanTwoSigmaNm() { return this.kalman ? 2 * this.kalmanAxisSigma() : null; }
  /** The KALMAN solution's 95% accuracy: 2.448 sigma under the stated isotropic model (plan C1). */
  private kalmanAccuracy95() { return accuracy95Isotropic(this.kalmanAxisSigma()); }
  /** Power interruption (M300 12-24): the emulated INS starts again unaided. */
  resetKalman() { this.kalman = null; }
  /** A crew position entry (SET POS): the position no longer derives from GPS. */
  initialize(position: LatLon) {
    this.solution = { ...this.solution, position: { ...position }, anp: 1, uncertain: true, gpsDependent: false };
    if (this.solution.mode === "DR") {
      this.solution.sensors = this.solution.sensors.map(sensor => sensor.mode === "DR" ? { ...sensor, accuracy95Nm: 1 } : sensor);
      this.solution.selected = this.solution.sensors.find(sensor => sensor.mode === "DR")!;
    }
  }
  update(input: { dt: number; air: AirData | null; airAt?: number; gps: PositionMeasurement | null; uncertainGps: PositionMeasurement | null;
    radio: RadioFix | null; radioApproved: boolean; rnp: number;
    /** Every radio mode's fix this update (plan F3); the resolver's step 3 chooses among them. Without it, `radio` is the only one. */
    radios?: readonly RadioFix[];
    /** Plan C3: the time now and the oldest range a NAIM backup may contain; without them every fix counts as fresh. */
    now?: number; naimMaxAgeS?: number;
    /** The emulated INS accelerations (null when the APIRS is unavailable), the Doppler ground velocity relative to
     * the surface, the crew's water current, and whether the KALMAN mode is past its first minute (M300 12-24). */
    apirs?: InertialInput | null; dvs?: DopplerInput | null; waterCurrent?: DopplerInput | null; kalmanReady?: boolean }): CivilSolution {
    const dtSeconds = Math.max(0, Number.isFinite(input.dt) ? input.dt : 0);
    // The emulated INS propagates on the APIRS whenever GPS does not aid it; without the APIRS it is lost.
    if (this.kalman) {
      if (!input.apirs || !this.equipment.kalman) this.kalman = null;
      else {
        const k = this.kalman;
        k.north += input.apirs.northMs2 * MS2_TO_KT_PER_S * dtSeconds;
        k.east += input.apirs.eastMs2 * MS2_TO_KT_PER_S * dtSeconds;
        k.position = offset(k.position, Math.atan2(k.east, k.north) * 180 / Math.PI, Math.hypot(k.north, k.east) * dtSeconds / 3600);
        k.coast += dtSeconds;
      }
    }
    const { air } = input;
    const fixes = input.radios ?? (input.radio ? [input.radio] : []);
    // Plan F3 step 3 (and step 4's radio order when none has integrity).
    const radio = chooseRadio(fixes, this.solution.mode, input.rnp);
    const priorGpsDependent = this.solution.gpsDependent;
    const fixGpsDependent = (fix: RadioFix) => fix.motion?.gpsDependent === true || fix.priorResolved && priorGpsDependent;
    // Transitive provenance (plan C1): a fix the prior estimate had to disambiguate inherits the prior's GPS dependency.
    const radioGpsDependent = radio !== null && fixGpsDependent(radio);
    const airValid = air !== null && [air.headingTrue, air.tasKt, air.altitudeFt].every(Number.isFinite)
      && air.tasKt >= 0 && air.tasKt <= 600;
    const lowSpeed = airValid && indicatedAirspeedKt(air!.tasKt, air!.altitudeFt) < this.parameters.drLowSpeedIas.value;
    let gps = input.gps;
    let uncertain = false;
    // The NAIM-style bound when an uncertain GPS was judged against an approved radio fix (plan F5 formalizes it).
    let naim: number | null = null;
    // Filled in below, once the selection is made.
    const pending = { sensors: [] as SensorSolution[], selected: drSensor(false, null, false), lowSpeed };
    if (!gps && input.uncertainGps) {
      // S300 1-7: retain a valid uncertain GPS position when it is the only available position source. Where a radio
      // source is approved, judge GPS against its independent position and accuracy before retaining it.
      // The laboratory NAIM comparison (plan F5): |GPS - backup| + the backup's 95% accuracy. No formula is sourced. The
      // backup qualifies only as an approved radio fix with integrity that does not depend on GPS (plan C1).
      // Step 2 evaluates each fresh independent backup separately from step 3's radio winner.
      const fresh = (fix: RadioFix) => input.now === undefined || input.naimMaxAgeS === undefined
        || (input.now >= fix.oldestAt && input.now - fix.oldestAt <= input.naimMaxAgeS * 1000);
      const backup = input.radioApproved
        ? chooseRadio(fixes.filter(fix => fix.naimEligible !== false && fresh(fix) && withinLimit(fix.anp, input.rnp) && !fixGpsDependent(fix)), "GPS", input.rnp)
        : null;
      const comparison = backup ? distanceNm(input.uncertainGps.position, backup.position) + backup.anp : null;
      naim = comparison;
      // Retained while the laboratory comparison is strictly below the limit (plan F5); it never becomes the ANP (C1).
      if (comparison === null || withinLimit(comparison, input.rnp)) {
        gps = input.uncertainGps;
        uncertain = true;
      }
    }
    if (gps) {
      this.previousRadio = null;
      this.solution = { position: { ...gps.position }, mode: "GPS", anp: gps.accuracy95Nm, gpsSource: gps.receiver, gpsDependent: true,
        dmes: radio?.dmes ?? [], vor: radio?.vor ?? null, uncertain, airValid, windComputed: false, ...pending };
      if (airValid && !lowSpeed && !uncertain && gps.northKt !== null && gps.eastKt !== null) {
        const heading = air!.headingTrue * Math.PI / 180;
        this.wind = { north: gps.northKt - air!.tasKt * Math.cos(heading), east: gps.eastKt - air!.tasKt * Math.sin(heading) };
        this.windGpsDependent = true;
        this.measuredWindSample = !Number.isFinite(gps.at) || !Number.isFinite(input.airAt) ? null
          : { ...this.wind, at: Math.min(gps.at!, input.airAt!), gpsDependent: true };
        this.solution.windComputed = true;
      }
      // Aiding (plan C2): only a GPS with integrity, valid velocity words and a 95% accuracy; it restarts the coast clock.
      if (this.equipment.kalman && !uncertain && gps.northKt !== null && gps.eastKt !== null && gps.accuracy95Nm !== null && input.apirs) {
        this.kalman = { position: { ...gps.position }, north: gps.northKt, east: gps.eastKt, coast: 0, sigma0: gps.accuracy95Nm / CIRCULAR_95 };
      }
    } else if (radio && input.radioApproved) {
      const previous = this.previousRadio;
      // Wind needs actual renewed radio observations. A compensated epoch carries its source's velocity,
      // rather than measuring a new one; differentiating it would restamp that same source indefinitely.
      const qualifiedVelocity = radio.naimEligible !== false
        && (!radio.observations || radio.observations.every(observation => observation.slantRangeNm.at === radio.at));
      const elapsed = previous ? (radio.at - previous.at) / 1000 : 0;
      if (airValid && !lowSpeed && qualifiedVelocity && previous?.qualifiedVelocity && elapsed > 0 && elapsed <= this.parameters.windRadioMaxGap.value) {
        const north = (radio.position.lat - previous.position.lat) * 60 * 3600 / elapsed;
        const east = longitudeDelta(previous.position.lon, radio.position.lon) * 60
          * Math.cos((radio.position.lat + previous.position.lat) * Math.PI / 360) * 3600 / elapsed;
        const heading = air!.headingTrue * Math.PI / 180;
        this.wind = { north: north - air!.tasKt * Math.cos(heading), east: east - air!.tasKt * Math.sin(heading) };
        this.windGpsDependent = radioGpsDependent || previous.gpsDependent;
        this.measuredWindSample = !Number.isFinite(input.airAt) ? null
          : { ...this.wind, at: Math.min(radio.at, input.airAt!), gpsDependent: this.windGpsDependent };
      }
      const windComputed = airValid && !lowSpeed && qualifiedVelocity && previous?.qualifiedVelocity === true && elapsed > 0 && elapsed <= this.parameters.windRadioMaxGap.value;
      if (!previous || radio.at > previous.at) this.previousRadio = { position: { ...radio.position }, at: radio.at, gpsDependent: radioGpsDependent, qualifiedVelocity };
      const fix = { position: radio.position, mode: radio.mode, anp: radio.anp, dmes: radio.dmes, vor: radio.vor };
      this.solution = { ...fix, gpsSource: null, gpsDependent: radioGpsDependent, uncertain: false, airValid, windComputed, ...pending };
    } else if (this.kalmanAvailable(input.kalmanReady === true)) {
      // KALMAN is GPS-aided, so it is GPS-dependent (plan C1, transitive provenance).
      this.previousRadio = null;
      const k = this.kalman!;
      this.solution = { position: { ...k.position }, mode: "KALMAN", anp: this.kalmanAccuracy95(), gpsSource: null, gpsDependent: true,
        dmes: [], vor: null, uncertain: true, airValid, windComputed: false, ...pending };
    } else if (this.equipment.dvs && input.dvs) {
      // DVS (M300 12-20): the Doppler ground velocity relative to the surface plus the crew's water current, integrated
      // from the position at entry, whose GPS dependency it keeps.
      this.previousRadio = null;
      const north = input.dvs.northKt + (input.waterCurrent?.northKt ?? 0), east = input.dvs.eastKt + (input.waterCurrent?.eastKt ?? 0);
      const moved = Math.hypot(north, east) * dtSeconds / 3600;
      if (this.solution.mode !== "DVS" || !this.dvsTrack) this.dvsTrack = { startAnp: this.solution.anp ?? UNKNOWN_START_NM, distance: 0, gpsDependent: this.solution.gpsDependent };
      this.dvsTrack.distance += moved;
      const position = offset(this.solution.position, Math.atan2(east, north) * 180 / Math.PI, moved);
      this.solution = { position, mode: "DVS", anp: this.dvsTrack.startAnp + DVS_DRIFT_FRACTION * this.dvsTrack.distance, gpsSource: null,
        gpsDependent: this.dvsTrack.gpsDependent, dmes: [], vor: null, uncertain: true, airValid, windComputed: false, ...pending };
    } else {
      this.previousRadio = null;
      const dt = dtSeconds;
      let position = this.solution.position;
      if (airValid && dt > 0) {
        const heading = air!.headingTrue * Math.PI / 180;
        const north = air!.tasKt * Math.cos(heading) + this.wind.north;
        const east = air!.tasKt * Math.sin(heading) + this.wind.east;
        position = offset(position, Math.atan2(east, north) * 180 / Math.PI, Math.hypot(north, east) * dt / 3600);
      }
      // 2 kt wind uncertainty, 0.5 kt TAS uncertainty and 1 degree heading uncertainty. No measured motion without
      // valid air data: hold the last position and grow the bound at 10 NM/h, never substitute the plant's track. In the
      // low-speed regime heading and TAS no longer measure the motion (plan F10): the same no-motion rate.
      const growth = airValid && !lowSpeed ? Math.hypot(this.parameters.drWindUncertainty.value, this.parameters.drTasUncertainty.value,
        air!.tasKt * Math.sin(this.parameters.drHeadingUncertainty.value * Math.PI / 180)) : this.parameters.drNoAirGrowth.value;
      // DR carries the dependency of the position it started from, and of the wind it propagates with.
      const gpsDependent = this.solution.gpsDependent || airValid && this.windGpsDependent;
      this.solution = { position, mode: "DR", anp: (this.solution.anp ?? UNKNOWN_START_NM) + growth * dt / 3600,
        gpsSource: null, gpsDependent, dmes: [], vor: null, uncertain: true, airValid, windComputed: false, ...pending };
    }
    // The split state (plan F2). GPS: 95% accuracy is the receiver's HFOM, its integrity bound the HIL (the NAIM result
    // when an uncertain GPS was retained against a radio fix), judged strictly against the active RNP (DEC-150). Radio:
    // 95% accuracy is the fix's estimate; its integrity rests on the solver's reasonableness checks (M300 15-3), a
    // criteria basis with no NP. DR never has integrity, and is available only from valid air data.
    const sensors: SensorSolution[] = [];
    const gpsInput = input.gps ?? input.uncertainGps;
    if (gpsInput) {
      sensors.push({ mode: "GPS", available: true, accuracy95Nm: gpsInput.accuracy95Nm,
        accuracyBasis: gpsInput.accuracy95Nm === null ? null : "receiver", gpsDependent: true, integrityNm: gpsInput.hilNm,
        naimComparisonNm: input.gps === null ? naim : null, integrityBasis: "NP",
        integrity: input.gps !== null && withinLimit(gpsInput.hilNm, input.rnp), eligibility: ELIGIBILITY.GPS });
    }
    for (const fix of fixes) sensors.push({ mode: fix.mode, available: input.radioApproved, accuracy95Nm: fix.anp, accuracyBasis: "laboratory",
      gpsDependent: fixGpsDependent(fix), integrityNm: null, naimComparisonNm: null,
      integrityBasis: "criteria", integrity: withinLimit(fix.anp, input.rnp), eligibility: ELIGIBILITY[fix.mode] });
    if (this.equipment.kalman && this.kalman) sensors.push({ mode: "KALMAN", available: this.kalmanAvailable(input.kalmanReady === true),
      accuracy95Nm: this.kalmanAccuracy95(), accuracyBasis: "laboratory", gpsDependent: true,
      integrityNm: null, naimComparisonNm: null, integrityBasis: "none", integrity: false, eligibility: ELIGIBILITY.KALMAN });
    if (this.equipment.dvs && input.dvs) sensors.push({ mode: "DVS", available: true, accuracy95Nm: this.solution.mode === "DVS" ? this.solution.anp : null,
      accuracyBasis: this.solution.mode === "DVS" ? "laboratory" : null, gpsDependent: this.solution.gpsDependent,
      integrityNm: null, naimComparisonNm: null, integrityBasis: "none", integrity: false, eligibility: ELIGIBILITY.DVS });
    sensors.push(drSensor(airValid, this.solution.mode === "DR" ? this.solution.anp : null,
      this.solution.mode === "DR" ? this.solution.gpsDependent : this.solution.gpsDependent || airValid && this.windGpsDependent));
    this.solution.sensors = sensors;
    this.solution.selected = sensors.find(sensor => sensor.mode === this.solution.mode)!;
    return this.current;
  }
}
