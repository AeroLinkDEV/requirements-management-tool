import { distanceNm, offset, type LatLon } from "./fmsModel";
import { DEFAULT_GEOID_SEPARATION_M, fasCrc, type ApproachLevel, type ApproachStatus, type DeviationScale, type FasDataBlock, type GpsBus, type GpsMode, type Integrity } from "./gps";
import type { Procedure, Runway } from "./navData";
import type { FlightPhase } from "./navigation";

/**
 * How the FMS judges a CMA-5024 receiver (gps.ts) from its bus alone (GPS phase 3a). The FMS never reads the receiver's
 * internals. One precedence table decides what it may do with what a receiver transmits (the GPS review's GPS-06, with
 * GPS-04's numeric domain), implemented here once and used by source selection, approach capability, the alerts and the
 * reason text the pages and the bench show. Each step vetoes; the first that applies is the reason given.
 *
 * May the receiver be navigated on (assessReceiver), in order:
 *   1. transmission: the bus is silent (SILENT);
 *   2. receiver state: 273 or 355 not Normal, 273 mode FAULT, or 355 unit fault (RECEIVER FAULT). An explicit fault
 *      makes the receiver unusable even when its position and HIL words still look valid (conservative default);
 *   3. position word status: 110, 120, 111 or 121 not Normal (NO FIX);
 *   4. position domain: the assembled coarse + fine latitude and longitude finite and within ±90 and ±180 (BAD DATA);
 *   5. integrity: 273 integrity DETECTED, or 130 not Normal (INTEGRITY);
 *   6. integrity domain: HIL finite and not negative, HFOM (when Normal) finite and not negative (BAD DATA);
 *   7. integrity limit: HIL within the phase's horizontal alert limit (INTEGRITY).
 * May the approach be flown on the selected receiver (approachAuthority), in order:
 *   1. the receiver may be navigated on (above);
 *   2. approach identity and availability (156): Normal, selected, CRC valid, for the approach selected, complete,
 *      available and not parked; any of these inhibits coupling (conservative default);
 *   3. level (305): Normal and not NONE;
 *   4. region: outside the approach region (156 armed) the approach is annunciated at its level but not yet guided;
 *   5. lateral (116): Normal and finite, or the approach cannot be flown at all;
 *   6. vertical: a level with vertical guidance (LPV, LNAV/VNAV) and 117 Normal and finite, or it is flown laterally
 *      only, annunciated LNAV (the LPV-to-LNAV downgrade).
 * The ability to navigate laterally (a usable receiver) is separate from permission to descend on an approach.
 */

/**
 * Horizontal alert limits by phase of flight, NM: en route 2, terminal 1, approach (LNAV) 0.3 (DO-229D and
 * TSO-C145/C146 as commonly cited; see the CMA-5024 design notes in FMS_TEST_BENCH.md).
 */
export const HAL_NM: Record<FlightPhase, number> = { "EN ROUTE": 2, TERMINAL: 1, APPROACH: 0.3 };

/** The FMS's GPS1/GPS2 position compare limit, NM. A laboratory parameter, not a CMA value (GPS DISAGREE). */
export const GPS_DISAGREE_NM = 0.1;

/** The smallest ANP the FMS reports from a receiver's HFOM, NM. A laboratory parameter. */
export const ANP_FLOOR_NM = 0.02;

export type GpsChoice = "AUTO" | "GPS1" | "GPS2";

/** Why a receiver may not be navigated on: the class of the first veto in the precedence table. */
export type ReceiverReason = "OK" | "SILENT" | "RECEIVER FAULT" | "NO FIX" | "BAD DATA" | "INTEGRITY";

export type ReceiverAssessment = {
  /** May be navigated on: no veto in the precedence table applies. */
  usable: boolean;
  reason: ReceiverReason;
  /** The veto itself, as the pages and the bench show it ("273 MODE FAULT", "HIL -1.00 INVALID"); "" when usable. */
  detail: string;
  /** The assembled position, when its words are Normal and in range; null otherwise. */
  fix: LatLon | null;
  /** HIL (130) and HFOM (247), NM; null when the word is not NORMAL. */
  hil: number | null;
  hfom: number | null;
  mode: GpsMode | null;
  /** The RAIM state (273), or null when the word is not NORMAL. */
  integrity: Integrity | null;
  used: number;
  visible: number;
  /** The approach level the receiver reports it can support (305). */
  level: ApproachLevel;
  provider: string | null;
};

const normal = (word: { value: number | null; ssm: string }) => (word.ssm === "NORMAL" ? word.value : null);

/** A number as a veto shows it: two decimals when finite, otherwise what it is (NaN, Infinity). */
const shown = (value: number) => (Number.isFinite(value) ? value.toFixed(2) : String(value));
/** A consumed quantity in its numeric domain: finite, and not negative where it cannot be. */
const valid = (value: number, nonNegative = false) => Number.isFinite(value) && (!nonNegative || value >= 0);

/**
 * The fix from the coarse and fine position words (110 + 120, 111 + 121): null unless all four are Normal, and null when
 * the assembled position is not finite or out of range (a Normal word is not a valid number: GPS-04).
 */
export function busFix(bus: GpsBus): LatLon | null {
  const position = assembledPosition(bus);
  return position && !positionVeto(position) ? position : null;
}

function assembledPosition(bus: GpsBus): LatLon | null {
  const parts = [normal(bus["110"]), normal(bus["120"]), normal(bus["111"]), normal(bus["121"])];
  if (parts.some(part => part === null)) return null;
  const [lat, latFine, lon, lonFine] = parts as number[];
  return { lat: lat + latFine, lon: lon + lonFine };
}

function positionVeto(position: LatLon) {
  if (!valid(position.lat) || Math.abs(position.lat) > 90) return `LAT ${shown(position.lat)} OUT OF RANGE`;
  if (!valid(position.lon) || Math.abs(position.lon) > 180) return `LON ${shown(position.lon)} OUT OF RANGE`;
  return null;
}

/** May this receiver be navigated on: the first veto of the precedence table above, with its detail. */
export function assessReceiver(bus: GpsBus | null, halNm: number): ReceiverAssessment {
  if (!bus) return { usable: false, reason: "SILENT", detail: "NOT TRANSMITTING", fix: null, hil: null, hfom: null, mode: null, integrity: null, used: 0, visible: 0, level: "NONE", provider: null };
  const status = bus["273"].ssm === "NORMAL" ? bus["273"].value : null;
  const faults = bus["355"].ssm === "NORMAL" ? bus["355"].value : null;
  const sbas = bus["305"].ssm === "NORMAL" ? bus["305"].value : null;
  const position = assembledPosition(bus), hil = normal(bus["130"]), hfom = normal(bus["247"]);
  const positionBad = position ? positionVeto(position) : null;
  const notNormal = (labels: ("110" | "120" | "111" | "121")[]) => labels.find(label => bus[label].ssm !== "NORMAL");
  const missing = notNormal(["110", "120", "111", "121"]);
  const veto = ((): [ReceiverReason, string] | null => {
    if (!status) return ["RECEIVER FAULT", `273 ${bus["273"].ssm}`];
    if (!faults) return ["RECEIVER FAULT", `355 ${bus["355"].ssm}`];
    if (status.mode === "FAULT") return ["RECEIVER FAULT", "273 MODE FAULT"];
    if (faults.unit) return ["RECEIVER FAULT", "355 UNIT FAULT"];
    if (missing) return ["NO FIX", `${missing} ${bus[missing].ssm}`];
    if (positionBad) return ["BAD DATA", positionBad];
    if (status.integrity === "DETECTED") return ["INTEGRITY", "273 INTEGRITY DETECTED"];
    if (hil === null) return ["INTEGRITY", `130 ${bus["130"].ssm}`];
    if (!valid(hil, true)) return ["BAD DATA", `HIL ${shown(hil)} INVALID`];
    if (hfom !== null && !valid(hfom, true)) return ["BAD DATA", `HFOM ${shown(hfom)} INVALID`];
    if (hil > halNm) return ["INTEGRITY", `HIL ${shown(hil)} > HAL ${shown(halNm)}`];
    return null;
  })();
  const [reason, detail] = veto ?? ["OK", ""];
  return {
    usable: veto === null, reason, detail, fix: position && !positionBad ? position : null, hil, hfom,
    mode: status?.mode ?? null, integrity: status?.integrity ?? null, used: status?.used ?? 0, visible: status?.visible ?? 0,
    level: sbas?.level ?? "NONE", provider: sbas?.provider ?? null,
  };
}

/** The receivers the FMS may use, in order: both (GPS1 first) in AUTO, one when chosen by hand, none when GPS is deselected. */
export function candidates(choice: GpsChoice, selected: boolean): number[] {
  if (!selected) return [];
  return choice === "AUTO" ? [0, 1] : choice === "GPS1" ? [0] : [1];
}

export type GpsAssessment = { assessed: ReceiverAssessment[]; chosen: number | null };

/** The receiver the pages describe: the one navigated on, else the one chosen by hand, else GPS1. */
export function shownReceiver(status: GpsAssessment, choice: GpsChoice) {
  const index = status.chosen ?? (choice === "GPS2" ? 1 : 0);
  return { index, name: `GPS${index + 1}`, assessment: status.assessed[index] as ReceiverAssessment | undefined };
}

/** Short names for the receiver modes (273), to fit beside a satellite count on a 24-column row. */
export const MODE_TEXT: Record<GpsMode, string> = {
  SELF_TEST: "TEST", INITIALIZATION: "INIT", ACQUISITION: "ACQ", NAV: "NAV", SBAS_NAV: "SBAS", SBAS_PA: "SBAS PA", ALT_AIDING: "ALT AID", FAULT: "FAULT",
};

// ------------------------------------------------------------------ GPS approach guidance (GPS phase 3b)

/**
 * The FAS block alert limits the FMS sends for an LPV approach, m: HAL 40 and VAL 50, the LPV values as commonly cited
 * (DO-229 through public summaries). Laboratory values, like the rest of the block: the demonstration database has no
 * published FAS data.
 */
export const FAS_HAL_M = 40, FAS_VAL_M = 50;
/** The FAS course width at the threshold, m (the usual 105 m, a lateral full scale of about 350 ft there). */
export const FAS_COURSE_WIDTH_M = 105;
/** The threshold crossing height when the approach's runway leg gives none, ft. */
export const DEFAULT_TCH_FT = 50;

/** A leading number in a procedure altitude ("1500A", "168"), ft; null when there is none. */
const feet = (altitude: string | undefined) => { const match = /^(\d+)/.exec(altitude ?? ""); return match ? Number(match[1]) : null; };

/**
 * The FAS data block the FMS builds for an RNAV approach from its navigation database (GPS phase 3b): the landing
 * threshold point (height above the ellipsoid through the receiver's default geoid), the flight path alignment point at
 * the runway's far end, the threshold crossing height from the runway leg's altitude above the runway, and the glide
 * path angle through the FAF altitude and the TCH. The path identifier is the procedure ident. Null for anything that is
 * not an RNAV approach with its runway and FAF.
 */
export function buildFas(approach: Procedure, runway: Runway | undefined, airport: string, fafPosition: LatLon | undefined): FasDataBlock | null {
  if (approach.kind !== "APPROACH" || approach.approachType !== "RNAV" || !runway || !fafPosition || !approach.faf) return null;
  const altitudeAt = (ident: string) => {
    const leg = approach.legs.find(entry => "ident" in entry && entry.ident === ident);
    return leg && "ident" in leg && typeof leg.altitude === "string" ? feet(leg.altitude) : null;
  };
  const fafAltitude = altitudeAt(approach.faf);
  if (fafAltitude === null) return null;
  const tch = (altitudeAt(runway.ident) ?? runway.elevation + DEFAULT_TCH_FT) - runway.elevation;
  const fromThresholdFt = distanceNm(runway.threshold, fafPosition) * 6076.12;
  const gpaDeg = Math.round(((Math.atan((fafAltitude - runway.elevation - tch) / fromThresholdFt) * 180) / Math.PI) * 100) / 100;
  const fpap = offset(runway.threshold, runway.course, runway.length / 6076.12);
  const [, number, designator] = /^RW(\d{2})([LRC]?)$/.exec(runway.ident) ?? ["", "0", ""];
  const block: Omit<FasDataBlock, "crc"> = {
    operationType: 0, sbasProvider: 0, airport, runway: Number(number), designator: designator as FasDataBlock["designator"],
    performance: 0, routeIndicator: "A", referencePathSelector: 0, referencePathId: approach.ident,
    ltp: { lat: runway.threshold.lat, lon: runway.threshold.lon, heightM: runway.elevation * 0.3048 + DEFAULT_GEOID_SEPARATION_M },
    fpapDelta: { lat: fpap.lat - runway.threshold.lat, lon: fpap.lon - runway.threshold.lon },
    tchFt: tch, gpaDeg, courseWidthM: FAS_COURSE_WIDTH_M, lengthOffsetM: 0, halM: FAS_HAL_M, valM: FAS_VAL_M,
  };
  return { ...block, crc: fasCrc(block) };
}

export type GpsApproachWords = {
  /** 305: the approach level the receiver can support. */
  level: ApproachLevel;
  /** 156: the selection status (armed outside the approach region), or null when the word is not Normal. */
  status: ApproachStatus | null;
  /** 116 lateral (ft, positive right of the course) and 117 vertical (ft, positive above the path); null unless Normal. */
  lateralFt: number | null;
  verticalFt: number | null;
  /** 201, NM to the threshold, and the deviation scaling; null unless Normal. */
  toThresholdNm: number | null;
  scale: DeviationScale | null;
};

/** A deviation word's value when Normal and finite; null otherwise (a Normal word is not a valid number: GPS-04). */
const deviation = (word: { value: number | null; ssm: string }) => { const value = normal(word); return value !== null && valid(value) ? value : null; };

/** The approach words of a receiver's bus, each used only when Normal and in its domain (GPS phase 3b). */
export function approachWords(bus: GpsBus | null): GpsApproachWords {
  if (!bus) return { level: "NONE", status: null, lateralFt: null, verticalFt: null, toThresholdNm: null, scale: null };
  return {
    level: bus["305"].ssm === "NORMAL" ? bus["305"].value?.level ?? "NONE" : "NONE",
    status: bus["156"].ssm === "NORMAL" ? bus["156"].value : null,
    lateralFt: deviation(bus["116"]), verticalFt: deviation(bus["117"]),
    toThresholdNm: deviation(bus["201"]), scale: bus.scale.ssm === "NORMAL" ? bus.scale.value : null,
  };
}

/** The approach levels with vertical guidance: the laboratory approach contract captures only on these (and an ILS). */
export const VERTICAL_LEVELS: readonly ApproachLevel[] = ["LPV", "LNAV/VNAV"];

export type ApproachAuthority = {
  /** What is annunciated: 305's level, LNAV when only lateral guidance remains, NO APPR when it may not be flown. */
  annunciation: "LPV" | "LNAV/VNAV" | "LNAV" | "NO APPR";
  /** The receiver's 116 may steer the approach now. */
  lateral: boolean;
  /** The approach may be descended on now: lateral, a vertical level, and 117 valid. */
  vertical: boolean;
  /** The first veto in the precedence table, as shown ("156 FAS CRC INVALID", "117 FW"); "" when both are available. */
  reason: string;
};

/**
 * May the approach be flown on the selected receiver: the approach half of the precedence table at the top of this file.
 * `receiver` is that receiver's assessment (null when none is selected).
 */
export function approachAuthority(bus: GpsBus | null, receiver: ReceiverAssessment | null): ApproachAuthority {
  const none = (reason: string): ApproachAuthority => ({ annunciation: "NO APPR", lateral: false, vertical: false, reason });
  if (!bus || !receiver?.usable) return none(receiver ? `GPS ${receiver.detail}` : "NO GPS SELECTED");
  if (bus["156"].ssm !== "NORMAL") return none(`156 ${bus["156"].ssm}`);
  const approach = bus["156"].value!;
  if (!approach.selected) return none("156 NOT SELECTED");
  if (approach.crcInvalid) return none("156 FAS CRC INVALID");
  if (approach.mismatch) return none("156 FAS MISMATCH");
  if (approach.incomplete) return none("156 FAS INCOMPLETE");
  if (!approach.available) return none("156 UNAVAILABLE");
  if (approach.parked) return none("156 PARKED");
  if (bus["305"].ssm !== "NORMAL") return none(`305 ${bus["305"].ssm}`);
  const level = bus["305"].value!.level;
  if (level === "NONE") return none("305 NO LEVEL");
  if (approach.armed) return { annunciation: level, lateral: false, vertical: false, reason: "OUTSIDE APPROACH REGION" };
  if (bus["116"].ssm !== "NORMAL") return none(`116 ${bus["116"].ssm}`);
  if (deviation(bus["116"]) === null) return none(`116 ${shown(bus["116"].value!)} INVALID`);
  const lateralOnly = (reason: string): ApproachAuthority => ({ annunciation: "LNAV", lateral: true, vertical: false, reason });
  if (!VERTICAL_LEVELS.includes(level)) return lateralOnly(`305 ${level}`);
  if (bus["117"].ssm !== "NORMAL") return lateralOnly(`117 ${bus["117"].ssm}`);
  if (deviation(bus["117"]) === null) return lateralOnly(`117 ${shown(bus["117"].value!)} INVALID`);
  return { annunciation: level, lateral: true, vertical: true, reason: "" };
}
