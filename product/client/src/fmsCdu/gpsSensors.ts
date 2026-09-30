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
 *
 * An RNAV approach that is intentionally LNAV only (its data says so: no path point record and no vertical path
 * published, such as the 87N COPTER RNAV 190 point-in-space approach, whose only minimum is LNAV) selects nothing in the
 * receiver: 156, 116 and 117 belong to FAS approaches. It is flown on the FMS's lateral guidance, annunciated LNAV
 * (lateral, no vertical), with only step 1: the selected receiver usable in the approach phase, which judges its HIL
 * against the approach HAL of 0.3 NM (AC 20-138, TSO-C146 practice). An approach whose required FAS data block is
 * missing, or published but unreadable, is not LNAV only: it is NO APPR (FAS DATA MISSING, FAS DATA INVALID), and every
 * FAS check above still applies to a FAS approach (Astra Q4).
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
  if (approach.kind !== "APPROACH" || approach.approachType !== "RNAV") return null;
  // A published FAS data block (ARINC 424 path point) is flown as published. Its CRC here is this model's CRC over the
  // fields (fasCrc), because the receiver checks that one; the published CRC over the DO-229 packing stays on the record.
  if (approach.publishedFas) {
    const { publishedCrc: _published, ...fields } = approach.publishedFas;
    return { ...fields, crc: fasCrc(fields) };
  }
  // A published block that could not be read is not replaced by a derived one, and an LNAV-only approach has none.
  if (approach.fasInvalid !== undefined || approach.lnavOnly) return null;
  if (!runway || !fafPosition || !approach.faf) return null;
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
 * What an RNAV approach needs of a FAS data block: one (FAS), none because it is intentionally LNAV only (the data says
 * so: navData Procedure.lnavOnly), or one it should have and does not (missing, or published but unreadable).
 */
export type FasRequirement = "FAS" | "LNAV ONLY" | "FAS DATA MISSING" | "FAS DATA INVALID";

/** An approach's FAS requirement, from its data and the FAS block derived for it (null when none could be). */
export function fasRequirement(approach: Procedure, fas: FasDataBlock | null): FasRequirement {
  if (approach.fasInvalid !== undefined) return "FAS DATA INVALID";
  if (approach.lnavOnly) return "LNAV ONLY";
  return fas ? "FAS" : "FAS DATA MISSING";
}

/**
 * May the approach be flown on the selected receiver: the approach half of the precedence table at the top of this file.
 * `receiver` is that receiver's assessment (null when none is selected).
 */
export function approachAuthority(bus: GpsBus | null, receiver: ReceiverAssessment | null, fas: FasRequirement = "FAS"): ApproachAuthority {
  const none = (reason: string): ApproachAuthority => ({ annunciation: "NO APPR", lateral: false, vertical: false, reason });
  if (!bus || !receiver?.usable) return none(receiver ? `GPS ${receiver.detail}` : "NO GPS SELECTED");
  // A FAS the approach needs but does not have is not LNAV only: it may not be flown (Astra Q4).
  if (fas === "FAS DATA MISSING" || fas === "FAS DATA INVALID") return none(fas);
  // LNAV only: the FMS steers the approach laterally; the receiver's 116 is not used, and nothing is descended on.
  if (fas === "LNAV ONLY") return { annunciation: "LNAV", lateral: false, vertical: false, reason: "NO FAS: LNAV ONLY" };
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

// ------------------------------------------------------------------ approach-aware AUTO selection

/**
 * AUTO receiver selection, approach-aware: the AeroLink simulator policy, not CMC's. These are proposed engineering
 * requirements for the bench (decided 29 September), not a reproduction of the CMA-9000 or CMA-5024 installation logic,
 * whose transfer behaviour is not verified. Sources: FAA AC 20-138D Change 2 §21-2.2(g) (evaluate switching to an
 * alternate source, annunciate it clearly, and let the switch give no inaccurate guidance); FAA AC 90-107 §8(e)
 * (fail-down behaviour varies by installation); the CMC CMA-5024 brochure (dual and triple receiver interfaces, LPV).
 *
 * In every flight phase AUTO retains the current receiver while it remains suitable for the required operation; recovery
 * of the other does not by itself move it, and GPS1 is only the initial tie-break when there is no current eligible
 * receiver (Astra, option A: an engineering choice, not a regulatory requirement; FMS_TEST_BENCH.md has the sources).
 * "Equally suitable" is validity, freshness, integrity and capability, never HPL or HFOM compared.
 *
 * A receiver can be usable for position and still not usable for the approach being flown, so AUTO judges both:
 *   1. both receivers support the selected approach: the current one is kept (no needless switching);
 *   2. before capture, the current one offers less than the approach needs and the other supports it: the other is
 *      selected once its eligibility has held for ELIGIBILITY_DWELL_S;
 *   3. during the approach, the current one loses what the approach needs and the other is still eligible: a qualified
 *      transfer, which keeps the guidance going without an unacceptable jump;
 *   4. the other merely reports the level, with stale, invalid, mismatched or inconsistent data: no transfer;
 *   5. neither can continue: the current receiver is kept, and the existing downgrade or disengagement follows;
 *   6. a receiver chosen by hand (GPS1 or GPS2): the manual contract of `candidates`, never overridden.
 * A receiver the approach may be transferred to (eligibleForApproach, then transferRefusal) must have: the same accepted
 * approach (the FAS the FMS sent is the executed one, and 156 shows it selected, valid, available, not parked and inside
 * the approach region); the LPV level (305); valid 116 and 117 and its deviation scaling; fresh words (116, 117 and 201
 * each changed once the aircraft has moved FRESH_MOVE_NM); eligibility held for ELIGIBILITY_DWELL_S; no GPS1/GPS2
 * disagreement; a scaling within TRANSFER_SCALE_TOLERANCE of the one flown; and deviations within TRANSFER_JUMP_FRACTION
 * of full scale of the ones flown (at the KBTV threshold about 5 ft vertically and 40 ft laterally, where two healthy
 * receivers differ by under 3 ft). When the source has to change during the approach without those checks (the current
 * receiver became unusable and no other qualifies), the change is not qualified and the approach may not be flown on
 * it: the existing loss of approach integrity follows. Transfers happen only while the approach is armed (and so while
 * captured); the flight disarms a lost approach, so recovery of the first receiver does not switch back (1), and a lost
 * approach is not recaptured: the flight's latched hold (D03) stays. The jump check compares with the last full guidance
 * flown (an eligible receiver's, within TRANSFER_REFERENCE_MS): a degraded receiver's words are never the reference. "Highest level wins" is deliberately not the
 * rule: below the approach's need there is no ranking.
 */
export const ELIGIBILITY_DWELL_S = 2;
export const FRESH_MOVE_NM = 0.01;
export const TRANSFER_JUMP_FRACTION = 0.1;
export const TRANSFER_SCALE_TOLERANCE = 0.05;
/** How old the guidance flown may be for a transfer to count as continuing it, ms. */
export const TRANSFER_REFERENCE_MS = 1000;
/** The level the selected approach needs: a FAS data block is flown to LPV. */
export const REQUIRED_APPROACH_LEVEL: ApproachLevel = "LPV";

export type SelectionInput = {
  /** A position source retained with uncertainty by the civil navigation core, never approach authority. */
  positionSource?: number | null;
  choice: GpsChoice;
  selected: boolean;
  assessed: readonly ReceiverAssessment[];
  buses: readonly (GpsBus | null)[];
  /** ms, and the aircraft's true position (the FMS's own motion, for the freshness of the receivers' words). */
  time: number;
  position: LatLon;
  /** The CRC of the executed approach's FAS (pinned with the plan), and of the FAS the FMS last sent; null for none. */
  executedCrc: number | null;
  sentCrc: number | null;
  /** The approach is armed (and so captured, if it is): the flight disarms it when the approach is lost or cancelled. */
  approachArmed: boolean;
};

export type SelectionResult = {
  chosen: number | null;
  /** A qualified approach transfer happened at this update (cases 2 and 3), to `chosen`. */
  transferred: boolean;
  /** The approach may be flown on `chosen`: false after an unqualified source change during the approach. */
  qualified: boolean;
  /** Why the other receiver was not taken, when a transfer was wanted and refused (case 4); "" otherwise. */
  refused: string;
};

type Guidance = { at: number; lateralFt: number; verticalFt: number; scale: DeviationScale };
type Snapshot = { at: LatLon; words: (number | null)[] };

export class AutoSelection {
  private current: number | null = null;
  private eligibleSince: (number | null)[] = [null, null];
  private snapshots: (Snapshot | null)[] = [null, null];
  private fresh = [false, false];
  /** The last full approach guidance flown (an eligible receiver's 116, 117 and scaling, and when), for the jump check. */
  private flown: Guidance | null = null;
  private approach: number | null = null;
  private approachQualified = true;

  choose(input: SelectionInput): SelectionResult {
    const { assessed, time } = input;
    if (input.executedCrc !== this.approach) { this.approach = input.executedCrc; this.approachQualified = true; this.flown = null; }
    const words = input.buses.map(approachWords);
    [0, 1].forEach(i => this.judgeFreshness(i, input.position, words[i]));
    const eligible = [0, 1].map(i => this.eligibleForApproach(i, input, words[i]));
    eligible.forEach((ok, i) => { this.eligibleSince[i] = ok ? this.eligibleSince[i] ?? time : null; });
    // Case 6: a receiver chosen by hand, or GPS deselected: the manual contract, unchanged.
    const result: SelectionResult = input.choice !== "AUTO" || !input.selected
      ? { chosen: candidates(input.choice, input.selected).find(i => assessed[i].usable) ?? null, transferred: false, qualified: true, refused: "" }
      : this.auto(input, words, eligible);
    if (!result.qualified) this.approachQualified = false;
    this.current = result.chosen;
    const flown = result.chosen === null ? null : words[result.chosen];
    // Only full guidance is a reference: a degraded receiver's words (LNAV, no 117) would let a jump through unchecked.
    if (result.chosen !== null && eligible[result.chosen] && flown) this.flown = { at: time, lateralFt: flown.lateralFt!, verticalFt: flown.verticalFt!, scale: flown.scale! };
    return { ...result, qualified: this.approachQualified };
  }

  private auto(input: SelectionInput, words: GpsApproachWords[], eligible: boolean[]): SelectionResult {
    const usable = [0, 1].filter(i => input.assessed[i].usable);
    const current = this.current !== null && usable.includes(this.current) ? this.current : null;
    const keep = (chosen: number | null, refused = ""): SelectionResult => ({ chosen, transferred: false, qualified: true, refused });
    // No approach being flown (none, not armed, or lost, which disarms it): the current receiver while usable (no
    // needless switching, and no switching back after a loss), else GPS1, else GPS2.
    if (input.executedCrc === null || !input.approachArmed) return keep(current
      ?? (input.positionSource !== null && input.positionSource !== undefined && usable.includes(input.positionSource) ? input.positionSource : null)
      ?? usable[0] ?? null);
    if (current !== null && eligible[current]) return keep(current);
    const others = usable.filter(i => i !== current && eligible[i]);
    const refusals = others.map(i => this.transferRefusal(i, input, words));
    const to = others.find((_, n) => refusals[n] === "");
    // Annunciated only as a transfer from a source; the first choice, with none before it, is not one.
    if (to !== undefined) return { chosen: to, transferred: this.current !== null, qualified: true, refused: "" };
    const refused = refusals.find(reason => reason !== "") ?? "";
    if (current !== null) return keep(current, refused);
    // The current receiver cannot be navigated on and no receiver qualifies: navigate on what is usable, but if the
    // approach was being guided the change is not qualified, and the approach may not be flown on it.
    const chosen = usable[0] ?? null;
    return { chosen, transferred: false, qualified: !(chosen !== null && this.guiding(input.time)), refused };
  }

  /** Full approach guidance was being flown just now (within TRANSFER_REFERENCE_MS). */
  private guiding(time: number) { return this.flown !== null && time - this.flown.at <= TRANSFER_REFERENCE_MS;
  }

  /** Why receiver i may not take the approach over now (case 4), or "" when it may. */
  private transferRefusal(i: number, input: SelectionInput, words: GpsApproachWords[]): string {
    const since = this.eligibleSince[i];
    if (since === null || input.time - since < ELIGIBILITY_DWELL_S * 1000) return `GPS${i + 1} NOT YET ESTABLISHED`;
    const [one, two] = input.assessed.map(a => a.fix);
    if (one && two && distanceNm(one, two) > GPS_DISAGREE_NM) return "GPS1/GPS2 DISAGREE";
    // Guidance being flown is continued only without a jump; before any is flown (or long after) there is none to jump from.
    const flown = this.flown, next = words[i];
    if (!flown || !this.guiding(input.time) || next.lateralFt === null || next.verticalFt === null || next.scale === null) return "";
    const off = (a: number, b: number) => Math.abs(a - b) > TRANSFER_SCALE_TOLERANCE * Math.max(a, b);
    if (off(flown.scale.lateralFullScaleFt, next.scale.lateralFullScaleFt) || off(flown.scale.verticalFullScaleFt, next.scale.verticalFullScaleFt)) return `GPS${i + 1} SCALING DIFFERS`;
    if (Math.abs(next.lateralFt - flown.lateralFt) > TRANSFER_JUMP_FRACTION * next.scale.lateralFullScaleFt) return `GPS${i + 1} LATERAL JUMP`;
    if (Math.abs(next.verticalFt - flown.verticalFt) > TRANSFER_JUMP_FRACTION * next.scale.verticalFullScaleFt) return `GPS${i + 1} VERTICAL JUMP`;
    return "";
  }

  /** May receiver i fly the selected approach: usable, the executed approach accepted, LPV, and fresh valid guidance. */
  private eligibleForApproach(i: number, input: SelectionInput, words: GpsApproachWords): boolean {
    if (!input.buses[i] || !input.assessed[i].usable || input.executedCrc === null || input.sentCrc !== input.executedCrc) return false;
    const status = words.status;
    if (!status || !status.selected || !status.available || status.crcInvalid || status.mismatch || status.incomplete || status.parked || status.armed) return false;
    if (words.level !== REQUIRED_APPROACH_LEVEL) return false;
    if (words.lateralFt === null || words.verticalFt === null || words.scale === null) return false;
    return this.fresh[i];
  }

  /** A receiver's guidance is fresh when 116, 117 and 201 have each changed since the aircraft last moved FRESH_MOVE_NM. */
  private judgeFreshness(i: number, position: LatLon, words: GpsApproachWords) {
    const now = [words.lateralFt, words.verticalFt, words.toThresholdNm];
    if (now.some(value => value === null)) { this.snapshots[i] = null; this.fresh[i] = false; return; }
    const before = this.snapshots[i];
    if (!before) { this.snapshots[i] = { at: position, words: now }; return; }
    if (distanceNm(before.at, position) < FRESH_MOVE_NM) return;
    this.fresh[i] = now.every((value, n) => value !== before.words[n]);
    this.snapshots[i] = { at: position, words: now };
  }
}

/**
 * What happened to the receivers and the FMS's choice of source, one entry per event, newest first: a receiver lost
 * (it may no longer be navigated on, with the veto) or recovered (usable again, which by itself changes nothing), and
 * each actual transfer of the FMS's source with the previous and the new source and why. A recovery is its own entry,
 * never a transfer.
 */
export type SelectionEvent =
  | { at: Date; kind: "LOST" | "RECOVERED"; receiver: "GPS1" | "GPS2"; reason: string }
  | { at: Date; kind: "TRANSFER"; from: string; to: string; reason: string };

export class SelectionLog {
  private usable: boolean[] | null = null;
  private chosen: number | null = null;
  private choice: string | null = null;
  private log: SelectionEvent[] = [];

  get entries(): readonly SelectionEvent[] { return this.log; }

  /**
   * Records this update against the last; returns the receivers just lost, whose failure is annunciated whatever the
   * FMS does about it (a transfer never suppresses it). `approach` is true when the change was an approach transfer.
   */
  update(at: Date, assessed: readonly ReceiverAssessment[], chosen: number | null, choice: string, approach: boolean): number[] {
    const usable = assessed.map(a => a.usable);
    const first = this.usable === null;
    const lost: number[] = [];
    const add = (event: SelectionEvent) => { this.log = [event, ...this.log].slice(0, 50); };
    if (!first) usable.forEach((now, i) => {
      if (now === this.usable![i]) return;
      add({ at, kind: now ? "RECOVERED" : "LOST", receiver: `GPS${i + 1}` as "GPS1" | "GPS2", reason: now ? "USABLE AGAIN" : assessed[i].detail });
      if (!now) lost.push(i);
    });
    if (!first && chosen !== this.chosen) {
      const name = (index: number | null) => (index === null ? "NONE" : `GPS${index + 1}`);
      const previous = this.chosen;
      const reason = choice !== this.choice ? `GPS NAV ${choice}`
        : previous !== null && !usable[previous] ? `${name(previous)} NOT USABLE: ${assessed[previous].detail}`
          : approach ? `${name(previous)} CANNOT CONTINUE THE APPROACH`
            : chosen === null ? "NO USABLE RECEIVER" : "NO CURRENT RECEIVER";
      add({ at, kind: "TRANSFER", from: name(previous), to: name(chosen), reason });
    }
    this.usable = usable;
    this.chosen = chosen;
    this.choice = choice;
    return lost;
  }
}
