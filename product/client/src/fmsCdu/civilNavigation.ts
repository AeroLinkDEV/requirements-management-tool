import { distanceNm, longitudeDelta, offset, type LatLon } from "./fmsModel";
import type { NavMode } from "./navigation";
import type { RadioFix } from "./radioNavigation";
import type { AirData } from "./sensorPorts";
import { ELIGIBILITY, withinLimit, type SensorSolution } from "./sensorState";
import { HELICOPTER_PROFILE, type AircraftProfile } from "./profile";

/** A receiver's position with its 95% accuracy (HFOM) and integrity bound (HIL) kept apart (plans F2, C1). ANP is the
 * 95% accuracy (DEC-150 item 6); the HIL is never presented as it. */
export type PositionMeasurement = { position: LatLon; accuracy95Nm: number | null; hilNm: number | null; receiver: 1 | 2;
  northKt: number | null; eastKt: number | null };
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
  /** Each candidate this update weighed, with its availability, 95% accuracy, integrity and eligibility (plan F2). */
  sensors: SensorSolution[];
  /** The candidate navigated on: the entry of `sensors` whose mode is `mode`. */
  selected: SensorSolution };

const drSensor = (available: boolean, accuracy95Nm: number | null, gpsDependent: boolean): SensorSolution => ({ mode: "DR", available,
  accuracy95Nm, accuracyBasis: accuracy95Nm === null ? null : "laboratory", gpsDependent,
  integrityNm: null, naimComparisonNm: null, integrityBasis: "none", integrity: false, eligibility: ELIGIBILITY.DR });

/** DR entered from a solution with no known accuracy starts from the initialization value (1 NM). */
const UNKNOWN_START_NM = 1;

/** The estimator has no aircraft-truth input. Values for uncertainty growth are declared bench assumptions. */
export class CivilNavigation {
  private solution: CivilSolution;
  private wind = { north: 0, east: 0 };
  private previousRadio: { position: LatLon; at: number; gpsDependent: boolean } | null = null;
  /** Whether the wind DR carries was computed from GPS velocity, or from radio fixes that were themselves GPS-dependent. */
  private windGpsDependent = false;
  private readonly parameters: AircraftProfile["parameters"];
  constructor(initial: LatLon, parameters: AircraftProfile["parameters"] = HELICOPTER_PROFILE.parameters) {
    this.parameters = parameters;
    const dr = drSensor(false, 1, false);
    this.solution = { position: { ...initial }, mode: "DR", anp: 1, gpsSource: null, gpsDependent: false, dmes: [], vor: null, uncertain: true, airValid: false, windComputed: false,
      sensors: [dr], selected: dr };
  }
  get current(): CivilSolution { return structuredClone(this.solution); }
  get windEstimate() { return { ...this.wind }; }
  accept(solution: CivilSolution, wind: { north: number; east: number }) {
    this.solution = structuredClone(solution);
    this.wind = { ...wind };
  }
  /** A crew position entry (SET POS): the position no longer derives from GPS. */
  initialize(position: LatLon) {
    this.solution = { ...this.solution, position: { ...position }, anp: 1, uncertain: true, gpsDependent: false };
    if (this.solution.mode === "DR") {
      this.solution.sensors = this.solution.sensors.map(sensor => sensor.mode === "DR" ? { ...sensor, accuracy95Nm: 1 } : sensor);
      this.solution.selected = this.solution.sensors.find(sensor => sensor.mode === "DR")!;
    }
  }
  update(input: { dt: number; air: AirData | null; gps: PositionMeasurement | null; uncertainGps: PositionMeasurement | null;
    radio: RadioFix | null; radioApproved: boolean; rnp: number }): CivilSolution {
    const { air, radio } = input;
    // Transitive provenance (plan C1): a fix the prior estimate had to disambiguate inherits the prior's GPS dependency.
    const radioGpsDependent = radio !== null && radio.priorResolved && this.solution.gpsDependent;
    const airValid = air !== null && [air.headingTrue, air.tasKt, air.altitudeFt].every(Number.isFinite)
      && air.tasKt >= 0 && air.tasKt <= 600;
    let gps = input.gps;
    let uncertain = false;
    // The NAIM-style bound when an uncertain GPS was judged against an approved radio fix (plan F5 formalizes it).
    let naim: number | null = null;
    // Filled in below, once the selection is made.
    const pending = { sensors: [] as SensorSolution[], selected: drSensor(false, null, false) };
    if (!gps && input.uncertainGps) {
      // S300 1-7: retain a valid uncertain GPS position when it is the only available position source. Where a radio
      // source is approved, judge GPS against its independent position and accuracy before retaining it.
      // The laboratory NAIM comparison (plan F5): |GPS - backup| + the backup's 95% accuracy. No formula is sourced. The
      // backup qualifies only as an approved radio fix with integrity that does not depend on GPS (plan C1).
      const qualifies = radio !== null && input.radioApproved && withinLimit(radio.anp, input.rnp) && !radioGpsDependent;
      const comparison = qualifies ? distanceNm(input.uncertainGps.position, radio.position) + radio.anp : null;
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
      if (airValid && !uncertain && gps.northKt !== null && gps.eastKt !== null) {
        const heading = air!.headingTrue * Math.PI / 180;
        this.wind = { north: gps.northKt - air!.tasKt * Math.cos(heading), east: gps.eastKt - air!.tasKt * Math.sin(heading) };
        this.windGpsDependent = true;
        this.solution.windComputed = true;
      }
    } else if (radio && input.radioApproved) {
      const previous = this.previousRadio;
      const elapsed = previous ? (radio.at - previous.at) / 1000 : 0;
      if (airValid && previous && elapsed > 0 && elapsed <= this.parameters.windRadioMaxGap.value) {
        const north = (radio.position.lat - previous.position.lat) * 60 * 3600 / elapsed;
        const east = longitudeDelta(previous.position.lon, radio.position.lon) * 60
          * Math.cos((radio.position.lat + previous.position.lat) * Math.PI / 360) * 3600 / elapsed;
        const heading = air!.headingTrue * Math.PI / 180;
        this.wind = { north: north - air!.tasKt * Math.cos(heading), east: east - air!.tasKt * Math.sin(heading) };
        this.windGpsDependent = radioGpsDependent || previous.gpsDependent;
      }
      const windComputed = airValid && previous !== null && elapsed > 0 && elapsed <= this.parameters.windRadioMaxGap.value;
      if (!previous || radio.at > previous.at) this.previousRadio = { position: { ...radio.position }, at: radio.at, gpsDependent: radioGpsDependent };
      const { priorResolved: _priorResolved, ...fix } = radio; void _priorResolved;
      this.solution = { ...fix, gpsSource: null, gpsDependent: radioGpsDependent, uncertain: false, airValid, windComputed, ...pending };
    } else {
      this.previousRadio = null;
      const dt = Math.max(0, Number.isFinite(input.dt) ? input.dt : 0);
      let position = this.solution.position;
      if (airValid && dt > 0) {
        const heading = air!.headingTrue * Math.PI / 180;
        const north = air!.tasKt * Math.cos(heading) + this.wind.north;
        const east = air!.tasKt * Math.sin(heading) + this.wind.east;
        position = offset(position, Math.atan2(east, north) * 180 / Math.PI, Math.hypot(north, east) * dt / 3600);
      }
      // 2 kt wind uncertainty, 0.5 kt TAS uncertainty and 1 degree heading uncertainty. No measured motion without
      // valid air data: hold the last position and grow the bound at 10 NM/h, never substitute the plant's track.
      const growth = airValid ? Math.hypot(this.parameters.drWindUncertainty.value, this.parameters.drTasUncertainty.value,
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
    if (radio) sensors.push({ mode: radio.mode, available: input.radioApproved, accuracy95Nm: radio.anp, accuracyBasis: "laboratory",
      gpsDependent: radioGpsDependent, integrityNm: null, naimComparisonNm: null,
      integrityBasis: "criteria", integrity: withinLimit(radio.anp, input.rnp), eligibility: ELIGIBILITY[radio.mode] });
    sensors.push(drSensor(airValid, this.solution.mode === "DR" ? this.solution.anp : null,
      this.solution.mode === "DR" ? this.solution.gpsDependent : this.solution.gpsDependent || airValid && this.windGpsDependent));
    this.solution.sensors = sensors;
    this.solution.selected = sensors.find(sensor => sensor.mode === this.solution.mode)!;
    return this.current;
  }
}
