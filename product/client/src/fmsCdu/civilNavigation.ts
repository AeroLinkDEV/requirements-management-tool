import { distanceNm, longitudeDelta, offset, type LatLon } from "./fmsModel";
import type { NavMode } from "./navigation";
import type { RadioFix } from "./radioNavigation";
import type { AirData } from "./sensorPorts";
import { ELIGIBILITY, withinLimit, type SensorSolution } from "./sensorState";
import { HELICOPTER_PROFILE, type AircraftProfile } from "./profile";

/** A receiver's position with its 95% accuracy (HFOM) and integrity bound (HIL) kept apart (plan F2); `anp` is the value
 * the estimator reports as ANP when it navigates on this measurement. */
export type PositionMeasurement = { position: LatLon; anp: number; accuracy95Nm: number | null; hilNm: number | null; receiver: 1 | 2;
  northKt: number | null; eastKt: number | null };
export type CivilSolution = { position: LatLon; mode: NavMode; anp: number; gpsSource: 1 | 2 | null;
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

const drSensor = (available: boolean, accuracy95Nm: number | null): SensorSolution => ({ mode: "DR", available, accuracy95Nm,
  integrityNm: null, integrityBasis: "none", integrity: false, eligibility: ELIGIBILITY.DR });

/** The estimator has no aircraft-truth input. Values for uncertainty growth are declared bench assumptions. */
export class CivilNavigation {
  private solution: CivilSolution;
  private wind = { north: 0, east: 0 };
  private previousRadio: { position: LatLon; at: number } | null = null;
  private readonly parameters: AircraftProfile["parameters"];
  constructor(initial: LatLon, parameters: AircraftProfile["parameters"] = HELICOPTER_PROFILE.parameters) {
    this.parameters = parameters;
    const dr = drSensor(false, 1);
    this.solution = { position: { ...initial }, mode: "DR", anp: 1, gpsSource: null, dmes: [], vor: null, uncertain: true, airValid: false, windComputed: false,
      sensors: [dr], selected: dr };
  }
  get current(): CivilSolution { return structuredClone(this.solution); }
  get windEstimate() { return { ...this.wind }; }
  accept(solution: CivilSolution, wind: { north: number; east: number }) {
    this.solution = structuredClone(solution);
    this.wind = { ...wind };
  }
  initialize(position: LatLon) {
    this.solution = { ...this.solution, position: { ...position }, anp: 1, uncertain: true };
    if (this.solution.mode === "DR") {
      this.solution.sensors = this.solution.sensors.map(sensor => sensor.mode === "DR" ? { ...sensor, accuracy95Nm: 1 } : sensor);
      this.solution.selected = this.solution.sensors.find(sensor => sensor.mode === "DR")!;
    }
  }
  update(input: { dt: number; air: AirData | null; gps: PositionMeasurement | null; uncertainGps: PositionMeasurement | null;
    radio: RadioFix | null; radioApproved: boolean; rnp: number }): CivilSolution {
    const { air, radio } = input;
    const airValid = air !== null && [air.headingTrue, air.tasKt, air.altitudeFt].every(Number.isFinite)
      && air.tasKt >= 0 && air.tasKt <= 600;
    let gps = input.gps;
    let anp = gps?.anp ?? 0;
    let uncertain = false;
    // The NAIM-style bound when an uncertain GPS was judged against an approved radio fix (plan F5 formalizes it).
    let naim: number | null = null;
    // Filled in below, once the selection is made.
    const pending = { sensors: [] as SensorSolution[], selected: drSensor(false, null) };
    if (!gps && input.uncertainGps) {
      // S300 1-7: retain a valid uncertain GPS position when it is the only available position source. Where a radio
      // source is approved, judge GPS against its independent position and accuracy before retaining it.
      const comparison = radio && input.radioApproved ? Math.hypot(distanceNm(input.uncertainGps.position, radio.position), radio.anp) : null;
      naim = comparison;
      if (comparison === null || comparison <= input.rnp) {
        gps = input.uncertainGps;
        anp = comparison ?? Math.max(gps.anp, input.rnp + 0.001);
        uncertain = true;
      }
    }
    if (gps) {
      this.previousRadio = null;
      this.solution = { position: { ...gps.position }, mode: "GPS", anp: Math.max(0.02, anp), gpsSource: gps.receiver,
        dmes: radio?.dmes ?? [], vor: radio?.vor ?? null, uncertain, airValid, windComputed: false, ...pending };
      if (airValid && !uncertain && gps.northKt !== null && gps.eastKt !== null) {
        const heading = air!.headingTrue * Math.PI / 180;
        this.wind = { north: gps.northKt - air!.tasKt * Math.cos(heading), east: gps.eastKt - air!.tasKt * Math.sin(heading) };
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
      }
      const windComputed = airValid && previous !== null && elapsed > 0 && elapsed <= this.parameters.windRadioMaxGap.value;
      if (!previous || radio.at > previous.at) this.previousRadio = { position: { ...radio.position }, at: radio.at };
      this.solution = { ...radio, gpsSource: null, uncertain: false, airValid, windComputed, ...pending };
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
      this.solution = { position, mode: "DR", anp: this.solution.anp + growth * dt / 3600,
        gpsSource: null, dmes: [], vor: null, uncertain: true, airValid, windComputed: false, ...pending };
    }
    // The split state (plan F2). GPS: 95% accuracy is the receiver's HFOM, its integrity bound the HIL (the NAIM result
    // when an uncertain GPS was retained against a radio fix), judged strictly against the active RNP (DEC-150). Radio:
    // 95% accuracy is the fix's estimate; its integrity rests on the solver's reasonableness checks (M300 15-3), a
    // criteria basis with no NP. DR never has integrity, and is available only from valid air data.
    const sensors: SensorSolution[] = [];
    const gpsInput = input.gps ?? input.uncertainGps;
    if (gpsInput) {
      const integrityNm = input.gps === null && naim !== null ? naim : gpsInput.hilNm;
      sensors.push({ mode: "GPS", available: true, accuracy95Nm: gpsInput.accuracy95Nm, integrityNm, integrityBasis: "NP",
        integrity: input.gps !== null && withinLimit(integrityNm, input.rnp), eligibility: ELIGIBILITY.GPS });
    }
    if (radio) sensors.push({ mode: radio.mode, available: input.radioApproved, accuracy95Nm: radio.anp, integrityNm: null,
      integrityBasis: "criteria", integrity: withinLimit(radio.anp, input.rnp), eligibility: ELIGIBILITY[radio.mode] });
    sensors.push(drSensor(airValid, this.solution.mode === "DR" ? this.solution.anp : null));
    this.solution.sensors = sensors;
    this.solution.selected = sensors.find(sensor => sensor.mode === this.solution.mode)!;
    return this.current;
  }
}
