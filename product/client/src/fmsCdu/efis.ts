import type { AxisModeLists, FlightSimulator, VerticalMode } from "./flight";
import { formatSetting } from "./baro";
import { courseDeg, distanceNm, offset, type LatLon, type Route } from "./fmsModel";
import type { ScriptedFms } from "./scriptedFms";
import { ACTIVE_PROFILE } from "./profile";
import { formatConstraint } from "./vnav";
import type { IntegrityBasis } from "./sensorState";
import type { CommandStatus, DmeDevice, RadioDevice, RadioFaults } from "./radioManagement";

// The FMS output bus and the aircraft data an EFIS draws from.
//
// On an aircraft the FMS does not draw the primary flight display or the navigation display: an EFIS does, from data
// words the FMS publishes (ARINC 429 labels such as 114 desired track, 116 cross-track, 117 vertical deviation, 121
// roll command and 251 distance to go) plus the aircraft's own sensors. The symbology therefore belongs to the EFIS
// installation. This module is that boundary for the bench: fmsOutputs() is everything the displays may take from the
// FMS, each word with a status like ARINC 429's sign/status matrix (normal, no computed data, failure). Nothing else in
// the FMS is visible to the EFIS. aircraftData() is what the EFIS takes from the aircraft (attitude, air data, heading)
// rather than from the FMS. A future FMS under test drives the displays by producing the same bus.
//
// The bus is generic engineering practice (FAA-H-8083-6, Boeing 737 FCOM 10 and 11 conventions), not a CMA-9000 or
// installation interface control document.

/** Word status, after ARINC 429's sign/status matrix: valid data, no computed data, or failure warning. */
export type WordStatus = "NORMAL" | "NCD" | "FAIL";
export type Word<T> = { value: T | null; status: WordStatus };

const normal = <T>(value: T): Word<T> => ({ value, status: "NORMAL" });
const ncd = <T>(): Word<T> => ({ value: null, status: "NCD" });
const fail = <T>(): Word<T> => ({ value: null, status: "FAIL" });

/** The navigation radios on the bus (the COM radios and transponders carry no navigation data). */
export const BUS_RADIOS = ["nav1", "nav2", "dme1", "dme2", "adf", "adf2", "tacan"] as const;
export type BusRadio = (typeof BUS_RADIOS)[number];
/** One radio's state words (plan C3): the frequency or channel it reports, this computer's last command to it, its health. */
export type RadioWords = {
  activeFrequency: Word<string>;
  commandStatus: Word<CommandStatus>;
  controlPath: Word<RadioFaults["controlPath"]>;
  measurementBus: Word<RadioFaults["measurementBus"]>;
  receiver: Word<RadioFaults["receiver"]>;
};
type NavMeasurement = { stationIdent: Word<string>; vorRadial: Word<number> };
type DmeMeasurement = { stationIdent: Word<string>; dmeDistance: Word<number> };
/** Per radio, what it measures (plan C4): VOR radial (magnetic), DME slant range (NM), ADF relative bearing, TACAN bearing and distance. */
export type RadioMeasurements = {
  nav1: NavMeasurement; nav2: NavMeasurement; dme1: DmeMeasurement; dme2: DmeMeasurement;
  adf: { adfBearing: Word<number> }; adf2: { adfBearing: Word<number> };
  tacan: { tacanBearing: Word<number>; tacanDistance: Word<number> };
};
export type SourceStatus = { available: boolean; accuracy95Nm: number | null };

/** A point of the route as the navigation display draws it. */
export type RoutePoint = { ident: string; position: LatLon; active: boolean; constraint: string | null };

export type FmsOutputs = {
  /** Which FMS the displays show; the source annunciation keeps the crew from following the wrong one. */
  source: "FMS1" | "FMS2";
  failed: boolean;
  /** Angular display reference. Geometry and desired-track values remain true; displays convert at this boundary. */
  angleReference: "MAG" | "TRUE";
  magneticVariation: Word<number>;
  /** Label 114: desired track, degrees true. */
  desiredTrack: Word<number>;
  /** Label 116: cross-track distance, NM, positive right of the desired track. */
  crossTrack: Word<number>;
  /**
   * Where the lateral guidance and cross-track come from: the selected GPS's 116 on an RNAV final (GPS), or the route
   * geometry (ROUTE); null without managed lateral guidance. A change is a change of source, shown, never substituted.
   */
  lateralSource: "GPS" | "ROUTE" | null;
  /** Label 117: vertical deviation, feet, positive above the path; `coupled` false for advisory information only. */
  verticalDeviation: Word<number>;
  verticalSource: "VNAV" | "APPR" | null;
  verticalCoupled: boolean;
  /** Label 121: roll steering command, degrees, positive right. */
  rollCommand: Word<number>;
  /** Label 251: distance to go to the active waypoint, NM. */
  distanceToGo: Word<number>;
  /** Active (TO) waypoint and its predicted time of arrival (epoch ms). */
  toWaypoint: Word<string>;
  eta: Word<number>;
  /** Targets the FMS commands (drawn magenta): speed in knots, altitude in feet. */
  targetSpeed: Word<number>;
  targetAltitude: Word<number>;
  /** Engaged and armed modes, from the controller state. */
  lateralMode: string;
  lateralArmed: string[];
  verticalMode: VerticalMode | null;
  verticalArmed: string[];
  approach: { type: string | null; state: "OFF" | "ARMED" | "CAPTURED" };
  /**
   * Full-scale lateral deviation for the phase (5 NM en route, 1 NM terminal, 0.3 NM approach: FAA-H-8083-6); on an RNAV
   * final, the selected GPS's angular scaling (GPS phase 3b).
   */
  lateralFullScaleNm: number;
  /**
   * Full-scale vertical deviation, feet: 400 ft for the VNAV path, 150 ft on the approach (laboratory values); on an RNAV
   * final, the selected GPS's angular scaling (GPS phase 3b).
   */
  verticalFullScaleFt: number;
  phase: string;
  rnp: number;
  /** The selected sensor's 95% accuracy, NM; null (NCD) when it gives none (plan C1). */
  anp: number | null;
  navMode: string;
  /*
   * Stage F C4 (F13): the selected navigation solution's C1 values (sensorState.ts), each its own word. The accuracy is
   * the sensor's: unlike `anp`, never the bench's forced TEST value. A failed FMS sends FAIL; a value the solution does not
   * have is NCD.
   */
  /** The 95% radial position error, NM (M300 15-1). */
  accuracy95Nm: Word<number>;
  /** Whose figure the accuracy is: the receiver's own (the GPS HFOM), or a laboratory estimate (any declared model). */
  accuracyBasis: Word<"receiver" | "laboratory">;
  /** Whether the solution depends on GPS, directly or through anything it was derived from (C1, transitive provenance). */
  gpsDependent: Word<boolean>;
  /** The integrity bound NP, NM (the GPS HIL): a separate word from the accuracy, never standing in for it. */
  integrityBoundNm: Word<number>;
  /** How integrity is established: NP against the limit, the radio modes' criteria (M300 15-3), or none (DR). */
  integrityBasis: Word<IntegrityBasis>;
  /** Whether the solution has integrity against the active error limit (M300 1-3). */
  integrityValid: Word<boolean>;
  /** GPS INT's independent raise/clear state (plan F3, M300 1-4), retained after a forced GPS reversion. */
  gpsIntegrityAnnunciation: Word<boolean>;
  /** The GPS position is held as uncertain (GPS POS UNCERTAIN): integrity lost, the position kept. */
  positionUncertain: Word<boolean>;
  /**
   * The laboratory NAIM comparison, NM (C1, F5): |GPS - backup| + the backup's 95% accuracy, when an uncertain GPS was
   * judged against a qualifying radio fix. It never gives integrity and is never the integrity bound.
   */
  naimComparisonNm: Word<number>;
  /**
   * Stage F C3, C4 (F13 part 2): each navigation radio's state words, from this computer's port on the shared radios: what
   * it reports it is on, this computer's last command to it, and its separate health states (control path, measurement
   * bus, receiver). FAIL from a failed FMS, or for a reading the radio cannot give (its bus lost or its receiver failed).
   */
  radios: Record<BusRadio, RadioWords>;
  /** Each radio's measurements, each its own word: the station it measures, and the radial, range or bearing. */
  radioMeasurements: RadioMeasurements;
  /** The KALMAN and DVS solutions' availability and 95% accuracy (F11), whether or not one is selected; NCD when not equipped. */
  kalman: Word<SourceStatus>;
  dvs: Word<SourceStatus>;
  /** For the navigation display. */
  activeRoute: RoutePoint[];
  modifiedRoute: RoutePoint[] | null;
  /** The executed offset track, drawn dashed magenta, as a polyline. */
  offsetTrack: LatLon[] | null;
  holdFix: LatLon | null;
  /** Top and end of descent positions from the VNAV profile. */
  topOfDescent: LatLon | null;
  endOfDescent: LatLon | null;
};

/** What the EFIS takes from the aircraft, not the FMS: attitude, heading, air data and ground speed. */
export type AircraftData = {
  pitch: number;
  bank: number;
  heading: number;
  track: number;
  airspeed: number;
  groundSpeed: number;
  /** What the altimeter indicates with the crew's setting (baro.ts): the PFD's altitude. */
  altitude: number;
  /** The crew's altimeter setting as the PFD writes it: STD, or QNH and hPa. */
  baroSetting: string;
  /**
   * Truth: the physical height above MSL, where the out-the-window view and synthetic vision place the eye (the world as
   * it is, not as a mis-set or erroneous altimeter would put it).
   */
  physicalAltitude: number;
  verticalSpeed: number;
  wind: { direction: number; speed: number };
  position: LatLon;
  /**
   * The crew's autopilot selections (drawn cyan): the preselected altitude and the selected speed. Null where the
   * profile's FMS commands them instead (the laboratory airline-style VNAV).
   */
  selectedAltitude: number | null;
  selectedSpeed: number | null;
  /** The heading the crew selected, or HDG holds (drawn cyan on the heading scale): the heading bug. */
  selectedHeading: number;
  /**
   * The missed approach altitude the selected altitude does not meet, as the CDU writes it ("5600A"), for the PFD to
   * show amber (FlightSimulator.missedAltitudeConflict); null when there is no conflict to show.
   */
  missedAltitudeConflict: string | null;
  /** Indicated airspeed (ISA from the true airspeed), or null where it is unreliable (below 30 kt, again from 33). */
  ias: number | null;
  /**
   * The helicopter profile's autopilot and hover data (null under the laboratory airline-style VNAV profile): the modes
   * per axis, the radio height and the selected hover height, the low-height caption, the ground velocity in aircraft
   * axes, and whether the hover data belongs on the display (the low-speed regime or a radio-height mode).
   */
  helicopter: {
    axes: { collective: string; pitch: string; roll: string };
    /** Per axis, the modes armed (white beside the engaged mode) and those a failure just took away (amber) (B3.4, B4.1). */
    armed: AxisModeLists;
    degraded: AxisModeLists;
    /** In the low-speed regime (below the coordinated-flight speed, with its hysteresis): the ND shows the ground velocity. */
    lowSpeed: boolean;
    radioHeight: { value: number | null; status: "NORMAL" | "NCD" | "FAIL" };
    hoverHeight: number;
    lowHeight: string | null;
    /** Measured ground velocity in aircraft axes (knots), or null without eligible feedback. */
    vx: number | null;
    vy: number | null;
    /** The ground velocity HOV or GSPD holds, in the same axes (drawn cyan beside VX/VY), or null in neither. */
    selectedVelocity: { vx: number; vy: number } | null;
    hoverData: boolean;
  } | null;
};

/** The ND's low-speed ground-velocity arrow (B4.5): pixels per knot, and the speed at which it stops growing. */
export const VELOCITY_VECTOR_PX_PER_KT = 3, VELOCITY_VECTOR_MAX_KT = 40;

const LATERAL_FULL_SCALE = { "EN ROUTE": 5, TERMINAL: 1, APPROACH: 0.3 } as const;

/**
 * The HSI full-scale deviation, NM (M300 15-1, Table 15-1): the phase default's, or for a crew RNP entry, by the entry
 * whatever the phase: above 1.01 NM, 5.0; above 0.31, 1.0; any other entry, 0.3.
 */
export function cdiFullScaleNm(phase: keyof typeof LATERAL_FULL_SCALE, performance: { rnp: number; rnpSource: "PHASE" | "MANUAL" | "TEST" }) {
  if (performance.rnpSource !== "MANUAL") return LATERAL_FULL_SCALE[phase];
  return performance.rnp > 1.01 ? 5 : performance.rnp > 0.31 ? 1 : 0.3;
}

/**
 * The connected points of a route for the map, up to the first discontinuity or the first fix without a position: the
 * line never bridges geometry the plan does not have. The active marker is on the active leg's fix only (the first leg
 * of the route), so an unresolved active fix leaves nothing marked active.
 */
function routePoints(fms: ScriptedFms, route: Route): RoutePoint[] {
  const points: RoutePoint[] = [];
  for (const [index, leg] of route.legs.entries()) {
    if (leg.kind === "disco") break;
    if (leg.kind !== "wpt") continue;
    const position = fms.coordinates(leg.ident, route);
    if (!position) break;
    points.push({ ident: leg.ident, position, active: index === 0, constraint: leg.altitude ?? null });
  }
  return points;
}

/** The position a given distance along a polyline, or null past its end. */
function alongPolyline(line: LatLon[], nm: number): LatLon | null {
  let left = nm;
  for (let i = 1; i < line.length; i += 1) {
    const leg = distanceNm(line[i - 1], line[i]);
    if (left <= leg) return offset(line[i - 1], courseDeg(line[i - 1], line[i]), left);
    left -= leg;
  }
  return null;
}

type NavigationWords = Pick<FmsOutputs, "accuracy95Nm" | "accuracyBasis" | "gpsDependent" | "integrityBoundNm" | "integrityBasis" | "integrityValid" | "gpsIntegrityAnnunciation" | "positionUncertain" | "naimComparisonNm">;

/** The selected solution's C1 values as words (Stage F C4): FAIL from a failed FMS, NCD for a value it does not have. */
function navigationWords(fms: ScriptedFms, failed: boolean): NavigationWords {
  if (failed) return { accuracy95Nm: fail(), accuracyBasis: fail(), gpsDependent: fail(), integrityBoundNm: fail(), integrityBasis: fail(), integrityValid: fail(), gpsIntegrityAnnunciation: fail(), positionUncertain: fail(), naimComparisonNm: fail() };
  const sensor = fms.navPerformance.sensor;
  const word = <T>(value: T | null): Word<T> => (value === null ? ncd() : normal(value));
  return {
    accuracy95Nm: word(sensor.accuracy95Nm), accuracyBasis: word(sensor.accuracyBasis), gpsDependent: normal(sensor.gpsDependent),
    integrityBoundNm: word(sensor.integrityNm), integrityBasis: normal(sensor.integrityBasis), integrityValid: normal(sensor.integrity),
    gpsIntegrityAnnunciation: normal(fms.gpsIntegrityAnnunciation),
    positionUncertain: normal(fms.navState.uncertain), naimComparisonNm: word(sensor.naimComparisonNm),
  };
}

type RadioBusWords = Pick<FmsOutputs, "radios" | "radioMeasurements" | "kalman" | "dvs">;

/**
 * The radios and the KALMAN and DVS sources as words (Stage F C3, C4). A radio's health states are facts and always
 * NORMAL words; what it reports and measures is FAIL when its measurement bus is lost or its receiver has failed, and NCD
 * when it simply has nothing (no station, no command yet). The TACAN's bearing and distance are NCD: the bench does not
 * measure them yet (F7).
 */
function radioBusWords(fms: ScriptedFms, failed: boolean): RadioBusWords {
  const port = fms.radioPort;
  const word = <T>(value: T | null | undefined, broken: boolean): Word<T> => (failed || broken ? fail() : value === null || value === undefined ? ncd() : normal(value));
  const broken = (device: RadioDevice | DmeDevice) => { const faults = port?.faults(device); return !!faults && (faults.measurementBus === "LOST" || faults.receiver === "FAILED"); };
  const radios = Object.fromEntries(BUS_RADIOS.map(device => {
    const faults = port?.faults(device);
    const dme = device === "dme1" || device === "dme2";
    const reported = !port ? null : dme ? (port.dmeReceiving(device) ? port.dmeHold(device) ?? port.receiving(device === "dme1" ? "nav1" : "nav2") : null) : port.receiving(device);
    // A DME is tuned through its NAV (or held): it takes no command of its own.
    const command = dme ? null : [...(port?.requests ?? [])].reverse().find(request => request.device === device)?.status ?? null;
    return [device, {
      activeFrequency: word(reported, broken(device)), commandStatus: word(command, false),
      controlPath: word(faults?.controlPath, false), measurementBus: word(faults?.measurementBus, false), receiver: word(faults?.receiver, false),
    } satisfies RadioWords];
  })) as Record<BusRadio, RadioWords>;
  const nav = (device: "nav1" | "nav2"): NavMeasurement => ({ stationIdent: word(fms.navStation(device)?.ident, broken(device)), vorRadial: word(fms.navRadial(device), broken(device)) });
  const dme = (device: DmeDevice): DmeMeasurement => {
    const ranging = !!port?.dmeReceiving(device);
    return { stationIdent: word(ranging ? fms.dmeReportedIdent(device) : null, broken(device)), dmeDistance: word(fms.dmeSlantRangeNm(device), broken(device)) };
  };
  const source = (mode: "KALMAN" | "DVS"): Word<SourceStatus> => {
    const solution = fms.sensorSolutions.find(candidate => candidate.mode === mode);
    return word(solution ? { available: solution.available, accuracy95Nm: solution.accuracy95Nm } : null, false);
  };
  return {
    radios,
    radioMeasurements: {
      nav1: nav("nav1"), nav2: nav("nav2"), dme1: dme("dme1"), dme2: dme("dme2"),
      adf: { adfBearing: word(fms.adfRelativeBearing("adf"), broken("adf")) }, adf2: { adfBearing: word(fms.adfRelativeBearing("adf2"), broken("adf2")) },
      tacan: { tacanBearing: word<number>(null, broken("tacan")), tacanDistance: word<number>(null, broken("tacan")) },
    },
    kalman: source("KALMAN"), dvs: source("DVS"),
  };
}

export function fmsOutputs(fms: ScriptedFms, sim: FlightSimulator): FmsOutputs {
  // Dual computers retain their attached side through link loss, independent operation and power changes.
  // A standalone ScriptedFms uses side 1 (its own RMS port); preserve that single-computer identity.
  const side = fms.dualOperation?.side ?? 1;
  const failed = fms.hasCondition("fmsFail");
  const g = sim.guidance;
  const active = fms.activeRoute;
  const next = active.legs[0];
  const phase = fms.flightPhase;
  const empty: FmsOutputs = {
    source: `FMS${side}`, failed, angleReference: fms.angleReference, magneticVariation: failed ? fail() : fms.magneticField ? normal(fms.magneticField.declination) : ncd(),
    desiredTrack: fail(), crossTrack: fail(), lateralSource: null, verticalDeviation: fail(), verticalSource: null, verticalCoupled: false,
    rollCommand: fail(), distanceToGo: fail(), toWaypoint: fail(), eta: fail(), targetSpeed: fail(), targetAltitude: fail(),
    lateralMode: sim.lateralMode === "HDG" ? (sim.headingHeld ? "HDG HOLD" : "HDG SEL") : g.mode, lateralArmed: [],
    verticalMode: sim.verticalMode, verticalArmed: [], approach: { type: null, state: "OFF" },
    lateralFullScaleNm: cdiFullScaleNm(phase, fms.navPerformance), verticalFullScaleFt: 400, phase, rnp: fms.navPerformance.rnp, anp: fms.navPerformance.anp,
    navMode: fms.navState.mode, activeRoute: [], modifiedRoute: null, offsetTrack: null, holdFix: null, topOfDescent: null, endOfDescent: null,
    ...navigationWords(fms, failed),
    ...radioBusWords(fms, failed),
  };
  // A failed FMS publishes failure warnings; the displays remove its data and flag it. The modes remain: they are the
  // autopilot's (basic heading and altitude hold after the reversion).
  if (failed) return empty;

  const managed = !fms.needsActiveLeg && sim.lateralMode === "LNAV" && g.desiredTrack !== null;
  const path = sim.verticalPath;
  const advisory = fms.advisoryVertical;
  const profile = fms.profile();
  const toIdent = next?.kind === "wpt" ? next.ident : null;
  const distanceToGo = g.distanceToGo;
  const activeRoute = routePoints(fms, active);
  const line = [fms.position, ...activeRoute.map(point => point.position)];
  const offsetNm = active.offset?.nm;
  // Armed, the vertical column names the approach when it has a vertical level: ILS, or the GPS's LPV or LNAV/VNAV (305).
  const type = fms.approachType;
  const verticalLevel = type === "ILS" || type === "LPV" || type === "LNAV/VNAV";
  // On an RNAV final the deviations are the GPS's, scaled as it scales them (the scaling beside 116/117).
  const onFinal = path?.source === "APPR" || sim.verticalFlag;
  const gpsScale = onFinal ? fms.gpsApproach?.scale ?? null : null;
  // On an RNAV final with GPS vertical guidance, the receiver's own 117 as it stands.
  const gpsVertical = onFinal && fms.gpsApproachVertical ? fms.gpsApproach!.verticalFt : null;
  // With GPS lateral authority (GPS-01), the receiver's own 116 as it stands, and its lateral scaling.
  const gpsLateral = sim.gpsLateralActive && fms.gpsApproachLateral ? fms.gpsApproach?.lateralFt ?? null : null;
  const lateralScale = gpsLateral !== null ? fms.gpsApproach?.scale ?? null : null;
  return {
    ...empty,
    desiredTrack: managed ? normal(g.desiredTrack!) : ncd(),
    // Captured on an RNAV final, the receiver's own 116 as it stands, converted to NM (GPS phase 3b).
    crossTrack: !managed ? ncd() : gpsLateral !== null ? normal(gpsLateral / 6076.12) : normal(g.crossTrack),
    lateralSource: !managed ? null : gpsLateral !== null ? "GPS" : "ROUTE",
    // Flagged on an RNAV final without GPS vertical guidance: the receiver withdrew it, so no path is shown (3b).
    verticalDeviation: advisory ? advisory.available ? normal(advisory.deviationFt!) : ncd()
      : sim.verticalFlag ? fail() : gpsVertical !== null ? normal(gpsVertical) : path ? normal(fms.altitude - path.altitude) : ncd(),
    verticalSource: advisory?.available ? "VNAV" : path?.source ?? null,
    verticalCoupled: advisory ? false : path?.coupled ?? false,
    // Invalid with the active waypoint a moving one whose rendezvous is unachievable (M300 11-37, condition 1).
    rollCommand: managed && !fms.rendezvousRollInvalid ? normal(g.bankCommand) : ncd(),
    distanceToGo: distanceToGo !== null && toIdent ? normal(distanceToGo) : ncd(),
    toWaypoint: toIdent ? normal(toIdent) : ncd(),
    // No ETA without measurable progress: a time from an invented speed would be a plausible falsehood. In a manual hold,
    // the next crossing along the pattern (Astra F1), as the pages show it.
    eta: (() => { const at = distanceToGo === null ? null : fms.shownEta(0, distanceToGo); return at === null ? ncd() : normal(at); })(),
    // Under the ADVISORY policy the FMS commands no speed or altitude: the crew selects them (aircraftData).
    targetSpeed: sim.advisory ? ncd() : normal(fms.targetSpeed),
    targetAltitude: sim.advisory || sim.altitudeHoldReference !== null ? ncd() : normal(g.targetAltitude),
    lateralArmed: sim.lnavIsArmed ? ["LNAV"] : [],
    verticalArmed: sim.approachMode === "ARMED" && verticalLevel ? [type] : [],
    approach: { type, state: sim.approachMode },
    lateralFullScaleNm: lateralScale ? lateralScale.lateralFullScaleFt / 6076.12 : cdiFullScaleNm(phase, fms.navPerformance),
    verticalFullScaleFt: advisory ? advisory.fullScaleFt : gpsScale ? gpsScale.verticalFullScaleFt : path?.source === "APPR" ? 150 : 400,
    activeRoute,
    modifiedRoute: fms.routeStatus === "MOD" ? routePoints(fms, fms.route) : null,
    offsetTrack: offsetNm ? line.slice(1).map((p, i) => offset(p, courseDeg(line[i], p) + (offsetNm > 0 ? 90 : -90), Math.abs(offsetNm))) : null,
    holdFix: active.hold ? fms.coordinates(active.hold.fix) ?? null : null,
    topOfDescent: profile.topOfDescent !== null ? alongPolyline(line, profile.topOfDescent) : null,
    endOfDescent: profile.endOfDescent ? fms.coordinates(profile.endOfDescent) ?? null : null,
  };
}

export function aircraftData(fms: ScriptedFms, sim: FlightSimulator): AircraftData {
  const track = fms.track;
  const airspeed = sim.tas;
  // The modelled attitude (flight.ts attitudeFor): the one the cameras show and the GPS antennas tilt with.
  const { pitch, bank } = fms.attitude;
  return {
    pitch, bank, heading: fms.heading, track, airspeed, groundSpeed: fms.groundSpeed,
    altitude: fms.indicatedAltitude, baroSetting: formatSetting(fms.baro.setting), physicalAltitude: fms.physicalAltitude, verticalSpeed: fms.verticalSpeed, wind: fms.wind, position: fms.truePosition,
    selectedAltitude: sim.advisory ? sim.selectedAltitude : null, selectedSpeed: sim.advisory ? sim.selectedSpeed : null,
    selectedHeading: sim.selectedHeading,
    missedAltitudeConflict: sim.missedAltitudeConflict ? formatConstraint(sim.missedAltitudeConflict.target) : null,
    ias: sim.iasReliable ? sim.indicatedAirspeed : null,
    helicopter: sim.advisory ? {
      axes: sim.axisModes, armed: sim.axisArmed, degraded: sim.axisDegraded(ACTIVE_PROFILE.parameters.fmaCaptureBox.value), lowSpeed: sim.inLowSpeedRegime,
      radioHeight: fms.radioHeight, hoverHeight: sim.hoverHeight, lowHeight: sim.lowHeightCaption,
      ...sim.groundVelocityAxes, selectedVelocity: sim.selectedGroundVelocity,
      hoverData: sim.inLowSpeedRegime || ["RHT", "TD", "TD/H", "TU"].includes(sim.axisModes.collective),
    } : null,
  };
}
