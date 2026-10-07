import type { ScriptedFms } from "./scriptedFms";
import { RADIO_NAMES, type DmeDevice, type RadioDevice, type RadioFaults } from "./radioManagement";
import { stimulusFor } from "./gpsStimulus";

/** Stage F14 laboratory stimuli. UI, recording and authored replay use this same admitted operation. */
export type SensorStimulus =
  | { kind: "radioFault"; device: RadioDevice | DmeDevice; controlPath?: RadioFaults["controlPath"]; measurementBus?: RadioFaults["measurementBus"]; receiver?: RadioFaults["receiver"] }
  | { kind: "stationOffAir"; ident: string; off: boolean }
  | { kind: "stationFault"; ident: string; component: "DME"; reply?: boolean; reportedIdent?: string | null }
  | { kind: "stationFault"; ident: string; component: "VOR"; biasDeg: number }
  | { kind: "ndb"; ident: string; offAir: boolean }
  | { kind: "airInput"; tasValid?: boolean; headingValid?: boolean; headingBiasDeg?: number }
  | { kind: "dvsInput"; surface: "LAND" | "SEA" }
  | { kind: "powerInterrupt"; durationMs: number }
  /** A deliberate APIRS accelerometer bias outside the nominal model (plan C2, F14), m/s² north and east; 0 and 0 clears it. */
  | { kind: "apirsBias"; northMs2: number; eastMs2: number }
  | { kind: "gpsPair"; mode: "NORMAL" | "INTEGRITY_ONLY" | "POSITION_GONE" };

const KINDS = new Set(["radioFault", "stationOffAir", "stationFault", "ndb", "airInput", "dvsInput", "powerInterrupt", "apirsBias", "gpsPair"]);
export function isSensorStimulus(value: { kind: string }): value is SensorStimulus { return KINDS.has(value.kind); }
const ident = (value: unknown) => typeof value === "string" && /^[A-Z0-9]{1,4}$/.test(value);
const finite = (value: unknown, min: number, max: number) => typeof value === "number" && Number.isFinite(value) && value >= min && value <= max;
export function sensorStimulusProblem(value: unknown): string | null {
  if (!value || typeof value !== "object" || Array.isArray(value)) return "a sensor stimulus is required";
  const a = value as Record<string, unknown>;
  const fields: Record<string, string[]> = {
    radioFault: ["device", "controlPath", "measurementBus", "receiver"], stationOffAir: ["ident", "off"], ndb: ["ident", "offAir"],
    stationFault: a.component === "DME" ? ["ident", "component", "reply", "reportedIdent"] : ["ident", "component", "biasDeg"],
    airInput: ["tasValid", "headingValid", "headingBiasDeg"], dvsInput: ["surface"], powerInterrupt: ["durationMs"], apirsBias: ["northMs2", "eastMs2"], gpsPair: ["mode"],
  };
  const allowed = typeof a.kind === "string" && Object.hasOwn(fields, a.kind) ? fields[a.kind] : null;
  if (allowed && Object.keys(a).some(key => key !== "kind" && !allowed.includes(key))) return `${String(a.kind)} contains unsupported fields`;
  switch (a.kind) {
    case "radioFault":
      if (typeof a.device !== "string" || !Object.hasOwn(RADIO_NAMES, a.device)) return `radioFault needs a radio (${Object.keys(RADIO_NAMES).join(", ")})`;
      if ((a.device === "dme1" || a.device === "dme2") && a.controlPath !== undefined) return "DME control path stimuli are unsupported: DME tuning follows its paired NAV receiver";
      if (a.controlPath === undefined && a.measurementBus === undefined && a.receiver === undefined) return "radioFault needs at least one of controlPath, measurementBus and receiver";
      if (a.controlPath !== undefined && a.controlPath !== "NORMAL" && a.controlPath !== "LOST") return "radioFault controlPath is NORMAL or LOST";
      if (a.measurementBus !== undefined && a.measurementBus !== "NORMAL" && a.measurementBus !== "LOST") return "radioFault measurementBus is NORMAL or LOST";
      return a.receiver === undefined || typeof a.receiver === "string" && ["NORMAL", "FAILED", "SILENT"].includes(a.receiver) ? null : "radioFault receiver is NORMAL, FAILED or SILENT";
    case "stationOffAir": return ident(a.ident) && typeof a.off === "boolean" ? null : "stationOffAir needs a station ident (1 to 4 letters or digits) and off true or false";
    case "ndb": return typeof a.ident === "string" && /^[A-Z0-9]{1,7}$/.test(a.ident) && typeof a.offAir === "boolean" ? null : "ndb needs a station ident and offAir true or false";
    case "stationFault":
      if (!ident(a.ident)) return "stationFault needs a station ident (1 to 4 letters or digits)";
      if (a.component === "VOR") return finite(a.biasDeg, -180, 180) ? null : "VOR stationFault needs biasDeg within ±180 degrees";
      if (a.component !== "DME") return "stationFault component is DME or VOR";
      if (a.reply === undefined && a.reportedIdent === undefined) return "DME stationFault needs reply or reportedIdent";
      if (a.reply !== undefined && typeof a.reply !== "boolean") return "DME reply is true or false";
      return a.reportedIdent === undefined || a.reportedIdent === null || ident(a.reportedIdent) ? null : "DME reportedIdent is a station ident or null to restore";
    case "airInput":
      if (a.tasValid === undefined && a.headingValid === undefined && a.headingBiasDeg === undefined) return "airInput needs tasValid, headingValid or headingBiasDeg";
      if ([a.tasValid, a.headingValid].some(flag => flag !== undefined && typeof flag !== "boolean")) return "airInput validity is true or false";
      return a.headingBiasDeg === undefined || finite(a.headingBiasDeg, -180, 180) ? null : "airInput headingBiasDeg is within ±180 degrees";
    case "powerInterrupt": return finite(a.durationMs, 0, 3_600_000) ? null : "powerInterrupt needs durationMs from 0 to 3600000 ms";
    case "apirsBias": return finite(a.northMs2, -10, 10) && finite(a.eastMs2, -10, 10) ? null : "apirsBias needs northMs2 and eastMs2 within ±10 m/s²";
    case "dvsInput": return a.surface === "LAND" || a.surface === "SEA" ? null : "dvsInput surface is LAND or SEA";
    case "gpsPair": return typeof a.mode === "string" && ["NORMAL", "INTEGRITY_ONLY", "POSITION_GONE"].includes(a.mode) ? null : "gpsPair mode is NORMAL, INTEGRITY_ONLY or POSITION_GONE";
    default: return `unsupported sensor stimulus ${String(a.kind)}`;
  }
}

export function describeSensorStimulus(a: SensorStimulus): string {
  switch (a.kind) {
    case "radioFault": return `set ${a.device.toUpperCase()}'s ${[a.controlPath && `control path ${a.controlPath}`, a.measurementBus && `measurement bus ${a.measurementBus}`, a.receiver && `receiver ${a.receiver}`].filter(Boolean).join(", ")}`;
    case "stationOffAir": return `${a.off ? "take" : "put"} the station ${a.ident} ${a.off ? "off" : "back on"} the air`;
    case "ndb": return `${a.offAir ? "take" : "restore"} NDB ${a.ident} ${a.offAir ? "off" : "on"} the air`;
    case "stationFault": return a.component === "VOR" ? `bias station ${a.ident}'s VOR radial by ${a.biasDeg} degrees (laboratory)`
      : `set station ${a.ident}'s DME ${[a.reply !== undefined && `reply ${a.reply}`, a.reportedIdent !== undefined && `reported ident ${a.reportedIdent ?? "database ident"}`].filter(Boolean).join(", ")} (laboratory)`;
    case "airInput": return `set measured navigation air inputs: ${[a.tasValid !== undefined && `TAS valid ${a.tasValid}`, a.headingValid !== undefined && `heading valid ${a.headingValid}`, a.headingBiasDeg !== undefined && `heading bias ${a.headingBiasDeg} degrees`].filter(Boolean).join(", ")} (laboratory)`;
    case "powerInterrupt": return `interrupt this FMS's KALMAN power for ${a.durationMs} ms (C2 laboratory rule)`;
    case "apirsBias": return `bias the APIRS accelerometers by ${a.northMs2} m/s² north and ${a.eastMs2} m/s² east (fault outside the nominal model, laboratory)`;
    case "dvsInput": return `set the measured Doppler surface to ${a.surface} (laboratory)`;
    case "gpsPair": return `set both GPS receivers to ${a.mode} (laboratory word overrides: HIL and position)`;
  }
}

/** Throws on an unavailable target; an unapplied stimulus is an execution error, never a pass. One kernel computation (#1518). */
export function applySensorStimulus(fms: ScriptedFms, a: SensorStimulus): void { fms.compute(() => applyStimulus(fms, a)); }

function applyStimulus(fms: ScriptedFms, a: SensorStimulus): void {
  const problem = sensorStimulusProblem(a);
  if (problem) throw new Error(problem);
  switch (a.kind) {
    case "radioFault": {
      const { kind: _kind, device, ...faults } = a;
      if (!fms.setRadioFaults(device, faults)) throw new Error(`no radios to fault: ${device.toUpperCase()}`);
      return;
    }
    case "ndb":
      if (!fms.setNdbOffAir(a.ident, a.offAir)) throw new Error(`NDB ${a.ident} requires one unambiguous NDB in the active database`);
      return;
    case "stationOffAir":
      if (!fms.setStationOffAir(a.ident, a.off)) throw new Error(`station ${a.ident} requires one unambiguous non-NDB facility in the active database`);
      return;
    case "stationFault": {
      const change = a.component === "VOR" ? { vorBiasDeg: a.biasDeg } : { ...(a.reply !== undefined ? { dmeReply: a.reply } : {}), ...(a.reportedIdent !== undefined ? { dmeIdent: a.reportedIdent } : {}) };
      if (!fms.setStationFault(a.ident, a.component, change)) throw new Error(`${a.component} ${a.ident} requires one unambiguous compatible facility in the active database`);
      return;
    }
    case "airInput": {
      const { kind: _kind, ...change } = a;
      if (!fms.setAirInputFaults(change)) throw new Error("an external input adapter owns this FMS's air words");
      return;
    }
    case "powerInterrupt": fms.powerInterrupt(a.durationMs); return;
    case "apirsBias": fms.setApirsFaultBias(a.northMs2, a.eastMs2); return;
    case "dvsInput":
      if (!fms.setDvsInputSurface(a.surface)) throw new Error("an external input adapter owns this FMS's Doppler word");
      return;
    case "gpsPair": {
      if (!fms.gpsStimulusAvailable) throw new Error("an external input adapter owns this FMS's GPS words");
      const port = stimulusFor(fms);
      for (const receiver of [0, 1] as const) {
        // Only the declared words are overridden. Accuracy, RF reception, and physical position remain independent.
        port.apply(receiver, { op: "clearOverride", label: "130" });
        for (const label of ["110", "120", "111", "121"] as const) port.apply(receiver, { op: "clearOverride", label });
        if (a.mode === "INTEGRITY_ONLY") port.apply(receiver, { op: "override", label: "130", kind: "FORCE", amount: 0, ssm: "NCD" });
        if (a.mode === "POSITION_GONE") for (const label of ["110", "120", "111", "121"] as const) port.apply(receiver, { op: "override", label, kind: "FORCE", amount: 0, ssm: "NCD" });
      }
      return;
    }
  }
}
