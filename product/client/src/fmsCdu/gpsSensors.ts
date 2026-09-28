import type { LatLon } from "./fmsModel";
import type { ApproachLevel, GpsBus, GpsMode, Integrity } from "./gps";
import type { FlightPhase } from "./navigation";

/**
 * How the FMS judges a CMA-5024 receiver (gps.ts) from its bus alone (GPS phase 3a). The FMS never reads the receiver's
 * internals: a word is used only when its SSM is NORMAL, and a receiver supports navigation only when its fix is valid
 * and its HIL (label 130) is within the horizontal alert limit of the phase.
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

export type ReceiverAssessment = {
  /** Supports navigation: a valid fix with HIL within the alert limit. */
  usable: boolean;
  /** Why not: the bus is silent, there is no valid fix, or integrity (HIL over the limit, or not computed). */
  reason: "OK" | "SILENT" | "NO FIX" | "INTEGRITY";
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

/** The fix from the coarse and fine position words (110 + 120, 111 + 121), or null unless all four are NORMAL. */
export function busFix(bus: GpsBus): LatLon | null {
  const parts = [normal(bus["110"]), normal(bus["120"]), normal(bus["111"]), normal(bus["121"])];
  if (parts.some(part => part === null)) return null;
  const [lat, latFine, lon, lonFine] = parts as number[];
  return { lat: lat + latFine, lon: lon + lonFine };
}

export function assessReceiver(bus: GpsBus | null, halNm: number): ReceiverAssessment {
  if (!bus) return { usable: false, reason: "SILENT", fix: null, hil: null, hfom: null, mode: null, integrity: null, used: 0, visible: 0, level: "NONE", provider: null };
  const status = bus["273"].ssm === "NORMAL" ? bus["273"].value : null;
  const sbas = bus["305"].ssm === "NORMAL" ? bus["305"].value : null;
  const fix = busFix(bus), hil = normal(bus["130"]), hfom = normal(bus["247"]);
  const reason = !fix ? "NO FIX" : hil === null || hil > halNm ? "INTEGRITY" : "OK";
  return {
    usable: reason === "OK", reason, fix, hil, hfom, mode: status?.mode ?? null, integrity: status?.integrity ?? null, used: status?.used ?? 0, visible: status?.visible ?? 0,
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
