import { NUMBER_LABELS, STATUS_FIELDS, validPatch, type GpsBus, type GpsLabel, type GpsReceiver, type NumberLabel, type Override, type Ssm, type StatusLabel, type StatusPatch } from "./gps";
import type { ScriptedFms } from "./scriptedFms";

/**
 * What the bench has injected into each of the FMS's receivers (faults, word overrides, status patches), held for the
 * life of the bench session alongside the receivers themselves, not in the tab. Every change is one typed operation
 * (GpsOp) through apply: the GPS sensors tab, a scenario step and a recording's replay all go the same way, so they
 * converge, and the tab, derived from this record, shows scripted stimuli as well as its own.
 */
export type SatFault = { prn: number; kind: "RAMP" | "STEP"; amount: number };
export type Spoof = { northM: number; driftEastMps: number };
export type ReceiverStimulus = {
  /** The satellites below 15° when "mask low satellites" was applied (a terrain mask snapshot), and ones masked by PRN. */
  lowPrns: number[]; masked: number[];
  jamDb: number; satFault: SatFault | null;
  doNotUse: boolean; outage: number[]; ionoStorm: number;
  receiver: boolean; rfInput: boolean; baroLost: boolean; stopped: boolean;
  spoof: Spoof | null;
  /** Numeric word overrides, with how the bench describes each. */
  overrides: Partial<Record<GpsLabel, { override: Override; text: string }>>;
  /** Typed status patches (273, 355, 156, 305), as applied. */
  statusPatches: Partial<Record<StatusLabel, Record<string, unknown>>>;
};

/** One stimulus on one receiver: what a tab control does, and what a scenario step names. */
export type GpsOp =
  /** Mask the GPS satellites below 15° as they are now (a snapshot), or remove that mask. */
  | { op: "maskLow"; on: boolean }
  /** Mask exactly these PRNs (besides mask low); an empty list unmasks them. */
  | { op: "mask"; prns: number[] }
  | { op: "jam"; db: number }
  | { op: "satelliteFault"; prn: number; fault: "RAMP" | "STEP"; value: number }
  | { op: "clearSatelliteFault" }
  | { op: "sbas"; doNotUse?: boolean; outage?: number[]; ionoStorm?: number }
  | { op: "fault"; fault: "RECEIVER" | "RF_INPUT" | "STOP_TRANSMITTING"; on: boolean }
  | { op: "baroLost"; on: boolean }
  | { op: "spoof"; northM: number; driftEastMps: number }
  | { op: "clearSpoof" }
  | { op: "override"; label: NumberLabel; kind: Override["kind"]; amount?: number; ssm?: Ssm }
  | { op: "clearOverride"; label: NumberLabel }
  | { op: "status"; label: StatusLabel; patch: Record<string, unknown> }
  | { op: "clearStatus"; label: StatusLabel };

const PRN_MAX = 32, GEOS = [131, 133];
const SSMS = ["NORMAL", "NCD", "FT", "FW"];
const OVERRIDE_KINDS = ["FORCE", "FREEZE", "BIAS", "RAMP"];
const num = (value: unknown, min: number, max: number) => typeof value === "number" && Number.isFinite(value) && value >= min && value <= max;
const prn = (value: unknown) => Number.isInteger(value) && num(value, 1, PRN_MAX);
const bool = (value: unknown) => typeof value === "boolean";
const numberLabel = (value: unknown) => typeof value === "string" && (NUMBER_LABELS as string[]).includes(value);
const statusLabel = (value: unknown) => typeof value === "string" && value in STATUS_FIELDS;

/** Why a GPS operation is not one the receivers accept, or null if it is (the domain of each field is checked). */
export function gpsOpProblem(value: unknown): string | null {
  const o = value as Record<string, unknown> | null;
  if (!o || typeof o !== "object") return "a GPS operation is required";
  switch (o.op) {
    case "maskLow": return bool(o.on) ? null : "maskLow needs on true or false";
    case "mask": return Array.isArray(o.prns) && o.prns.every(prn) ? null : `mask needs a list of PRNs from 1 to ${PRN_MAX}`;
    case "jam": return num(o.db, 0, 60) ? null : "jam needs db from 0 to 60";
    case "satelliteFault": {
      if (!prn(o.prn)) return `satelliteFault needs a PRN from 1 to ${PRN_MAX}`;
      if (o.fault !== "RAMP" && o.fault !== "STEP") return "satelliteFault needs fault RAMP or STEP";
      return num(o.value, -1000, 1000) ? null : "satelliteFault needs a value from -1000 to 1000 (m/s for a ramp, m for a step)";
    }
    case "clearSatelliteFault":
    case "clearSpoof": return null;
    case "sbas": {
      if (o.doNotUse === undefined && o.outage === undefined && o.ionoStorm === undefined) return "sbas needs doNotUse, outage or ionoStorm";
      if (o.doNotUse !== undefined && !bool(o.doNotUse)) return "sbas doNotUse must be true or false";
      if (o.outage !== undefined && !(Array.isArray(o.outage) && o.outage.every(geo => GEOS.includes(geo as number)))) return `sbas outage must list geostationary PRNs (${GEOS.join(", ")})`;
      return o.ionoStorm === undefined || num(o.ionoStorm, 1, 100) ? null : "sbas ionoStorm must be from 1 to 100";
    }
    case "fault": return (o.fault === "RECEIVER" || o.fault === "RF_INPUT" || o.fault === "STOP_TRANSMITTING") && bool(o.on) ? null : "fault needs RECEIVER, RF_INPUT or STOP_TRANSMITTING and on true or false";
    case "baroLost": return bool(o.on) ? null : "baroLost needs on true or false";
    case "spoof": return num(o.northM, -100_000, 100_000) && num(o.driftEastMps, -1000, 1000) ? null : "spoof needs northM within ±100000 and driftEastMps within ±1000";
    case "override": {
      if (!numberLabel(o.label)) return `override needs a numeric label (${NUMBER_LABELS.join(", ")})`;
      if (!OVERRIDE_KINDS.includes(o.kind as string)) return "override needs kind FORCE, FREEZE, BIAS or RAMP";
      if (o.kind !== "FREEZE" && !num(o.amount, -1e9, 1e9)) return `override ${String(o.kind)} needs a finite amount`;
      if (o.ssm !== undefined && (o.kind !== "FORCE" || !SSMS.includes(o.ssm as string))) return "override ssm is NORMAL, NCD, FT or FW, and only with FORCE";
      return null;
    }
    case "clearOverride": return numberLabel(o.label) ? null : "clearOverride needs a numeric label";
    case "status": {
      if (!statusLabel(o.label)) return "status needs a status label (273, 355, 156 or 305)";
      return o.patch && typeof o.patch === "object" && validPatch(STATUS_FIELDS[o.label as StatusLabel], o.patch) ? null : `status patch is not valid for ${String(o.label)}`;
    }
    case "clearStatus": return statusLabel(o.label) ? null : "clearStatus needs a status label (273, 355, 156 or 305)";
    default: return `unsupported GPS operation "${String(o.op)}"`;
  }
}

/** The operation in words, with its fields and values, for step lists, the run report and the test procedure. */
export function describeGpsOp(o: GpsOp): string {
  switch (o.op) {
    case "maskLow": return o.on ? "mask the satellites below 15°" : "remove the low-satellite mask";
    case "mask": return o.prns.length ? `mask PRN ${o.prns.join(", ")}` : "unmask the PRNs masked by hand";
    case "jam": return o.db ? `jam every signal by ${o.db} dB` : "stop the jamming";
    case "satelliteFault": return `put a ${o.fault === "RAMP" ? `${o.value} m/s range ramp` : `${o.value} m range step`} on PRN ${o.prn}`;
    case "clearSatelliteFault": return "clear the satellite range error";
    case "sbas": return [
      o.doNotUse !== undefined ? `SBAS ${o.doNotUse ? "do not use" : "usable"}` : null,
      o.outage !== undefined ? (o.outage.length ? `GEO ${o.outage.join(", ")} out` : "no GEO outage") : null,
      o.ionoStorm !== undefined ? `ionospheric storm ×${o.ionoStorm}` : null,
    ].filter(Boolean).join(", ");
    case "fault": return `${o.on ? "set" : "clear"} the ${o.fault === "RECEIVER" ? "receiver fault" : o.fault === "RF_INPUT" ? "RF input fault" : "stop-transmitting fault"}`;
    case "baroLost": return o.on ? "take away the baro altitude input" : "restore the baro altitude input";
    case "spoof": return `spoof the position ${o.northM} m north, drifting ${o.driftEastMps} m/s east`;
    case "clearSpoof": return "end the spoofing";
    case "override": return `override ${o.label}: ${overrideText(o)}`;
    case "clearOverride": return `clear the override of ${o.label}`;
    case "status": return `patch status word ${o.label}: ${Object.entries(o.patch).map(([k, v]) => `${k} ${typeof v === "object" ? JSON.stringify(v) : String(v)}`).join(", ")}`;
    case "clearStatus": return `clear the patch of status word ${o.label}`;
  }
}

const overrideText = (o: Extract<GpsOp, { op: "override" }>) => (o.kind === "FREEZE" ? "FREEZE" : `${o.kind} ${o.amount}${o.ssm ? ` ${o.ssm}` : ""}`);
const overrideOf = (o: Extract<GpsOp, { op: "override" }>): Override =>
  o.kind === "FORCE" ? (o.ssm ? { kind: "FORCE", value: o.amount!, ssm: o.ssm } : { kind: "FORCE", value: o.amount! })
    : o.kind === "FREEZE" ? { kind: "FREEZE" } : o.kind === "BIAS" ? { kind: "BIAS", amount: o.amount! } : { kind: "RAMP", perSecond: o.amount! };

/** The GPS satellites in view below an elevation (terrain masking on the bench): the geostationary ones are left alone. */
export function lowSatellites(bus: GpsBus, belowDeg = 15): number[] {
  return bus["060"].map(word => word.value!).filter(s => !s.sbas && s.elevation < belowDeg).map(s => s.prn);
}

const initial = (): ReceiverStimulus => ({
  lowPrns: [], masked: [], jamDb: 0, satFault: null, doNotUse: false, outage: [], ionoStorm: 1,
  receiver: false, rfInput: false, baroLost: false, stopped: false, spoof: null, overrides: {}, statusPatches: {},
});

export class GpsStimulus {
  private readonly states: [ReceiverStimulus, ReceiverStimulus] = [initial(), initial()];
  private readonly fms: ScriptedFms;
  /** Told of every applied operation (receiver index 0 or 1): the bench's recorder, while recording. */
  listener: ((index: number, op: GpsOp) => void) | null = null;

  constructor(fms: ScriptedFms) { this.fms = fms; }

  state(index: number): Readonly<ReceiverStimulus> { return this.states[index]; }

  private receiver(index: number): GpsReceiver { return this.fms.gps[index]; }

  /** Masking, unless the GPS integrity condition holds the satellite selection (it replaces and then clears it). */
  private deselect(index: number, state: ReceiverStimulus) {
    if (!this.fms.hasCondition("gpsIntegrity")) this.receiver(index).deselect([...new Set([...state.lowPrns, ...state.masked])]);
  }

  /**
   * Applies one operation to one receiver (index 0 or 1): it changes only that stimulus, records it, lets the FMS
   * re-read the receivers and tells the listener. Returns false, and changes nothing, when the operation is invalid or
   * the receiver refuses it (a status patch it does not accept).
   */
  apply(index: number, op: GpsOp): boolean {
    if ((index !== 0 && index !== 1) || gpsOpProblem(op) !== null) return false;
    const rx = this.receiver(index), state = this.states[index];
    let update: Partial<ReceiverStimulus>;
    switch (op.op) {
      case "maskLow": {
        const lowPrns = op.on ? lowSatellites(rx.rawBus()) : [];
        this.deselect(index, { ...state, lowPrns });
        update = { lowPrns };
        break;
      }
      case "mask": this.deselect(index, { ...state, masked: op.prns }); update = { masked: [...op.prns] }; break;
      case "jam": rx.setJamming(op.db); update = { jamDb: op.db }; break;
      case "satelliteFault":
      case "clearSatelliteFault": {
        const previous = state.satFault, next = op.op === "satelliteFault" ? { prn: op.prn, kind: op.fault, amount: op.value } : null;
        if (previous && previous.prn !== next?.prn) rx.satelliteFault(previous.prn, null);
        if (next) rx.satelliteFault(next.prn, next.kind === "RAMP" ? { kind: "RAMP", metresPerSecond: next.amount } : { kind: "STEP", metres: next.amount });
        else if (previous) rx.satelliteFault(previous.prn, null);
        update = { satFault: next };
        break;
      }
      case "sbas": {
        const next = { doNotUse: op.doNotUse ?? state.doNotUse, outage: op.outage ? [...op.outage] : state.outage, ionoStorm: op.ionoStorm ?? state.ionoStorm };
        rx.setSbas(next);
        update = next;
        break;
      }
      case "fault": {
        rx.injectFault(op.fault, op.on);
        update = op.fault === "RECEIVER" ? { receiver: op.on } : op.fault === "RF_INPUT" ? { rfInput: op.on } : { stopped: op.on };
        break;
      }
      case "baroLost": this.fms.setGpsBaro(index, !op.on); update = { baroLost: op.on }; break;
      case "spoof":
      case "clearSpoof": {
        const spoof = op.op === "spoof" ? { northM: op.northM, driftEastMps: op.driftEastMps } : null;
        rx.setSpoof(spoof ? { northM: spoof.northM, eastM: 0, driftNorthMps: 0, driftEastMps: spoof.driftEastMps } : null);
        update = { spoof };
        break;
      }
      case "override":
      case "clearOverride": {
        const overrides = { ...state.overrides };
        if (op.op === "override") { const override = overrideOf(op); rx.override(op.label, override); overrides[op.label] = { override, text: overrideText(op) }; }
        else { rx.override(op.label, null); delete overrides[op.label]; }
        update = { overrides };
        break;
      }
      case "status":
      case "clearStatus": {
        const patch = op.op === "status" ? op.patch : null;
        if (!rx.overrideStatus(op.label, patch as StatusPatch[typeof op.label] | null)) return false;
        const statusPatches = { ...state.statusPatches };
        if (patch) statusPatches[op.label] = patch; else delete statusPatches[op.label];
        update = { statusPatches };
        break;
      }
    }
    this.states[index] = { ...state, ...update };
    this.fms.gpsUpdated();
    this.listener?.(index, structuredClone(op));
    return true;
  }

  // The tab's controls, as operations.
  setMaskLow(index: number, on: boolean) { this.apply(index, { op: "maskLow", on }); }
  toggleMasked(index: number, value: number) {
    const current = this.states[index].masked;
    this.apply(index, { op: "mask", prns: current.includes(value) ? current.filter(p => p !== value) : [...current, value] });
  }
  /** The integrity condition has taken the satellite selection: the bench's masking no longer applies. */
  clearMasking() {
    this.states.forEach((state, index) => { if (state.lowPrns.length || state.masked.length) this.states[index] = { ...state, lowPrns: [], masked: [] }; });
  }
  setJamming(index: number, db: number) { this.apply(index, { op: "jam", db }); }
  setSatFault(index: number, fault: SatFault | null) {
    this.apply(index, fault ? { op: "satelliteFault", prn: fault.prn, fault: fault.kind, value: fault.amount } : { op: "clearSatelliteFault" });
  }
  setSbas(index: number, sbas: Partial<Pick<ReceiverStimulus, "doNotUse" | "outage" | "ionoStorm">>) { this.apply(index, { op: "sbas", ...sbas }); }
  setFault(index: number, fault: "receiver" | "rfInput" | "stopped", on: boolean) {
    this.apply(index, { op: "fault", fault: fault === "receiver" ? "RECEIVER" : fault === "rfInput" ? "RF_INPUT" : "STOP_TRANSMITTING", on });
  }
  setBaroLost(index: number, lost: boolean) { this.apply(index, { op: "baroLost", on: lost }); }
  setSpoof(index: number, spoof: Spoof | null) { this.apply(index, spoof ? { op: "spoof", ...spoof } : { op: "clearSpoof" }); }
  /** A numeric word override from the bus monitor's form (FORCE with an optional status, FREEZE, BIAS, RAMP), or null to clear. */
  setOverride(index: number, label: NumberLabel, override: { kind: Override["kind"]; amount?: number; ssm?: Ssm } | null) {
    this.apply(index, override ? { op: "override", label, ...override } : { op: "clearOverride", label });
  }
  /** A typed status patch; false, and nothing recorded, when the receiver refuses it. null clears the label's patch. */
  setStatusPatch<L extends StatusLabel>(index: number, label: L, patch: StatusPatch[L] | null): boolean {
    return this.apply(index, patch ? { op: "status", label, patch: patch as Record<string, unknown> } : { op: "clearStatus", label });
  }
}

/** One stimulus record per bench session: keyed to the FMS that owns the receivers, so a restart starts clean. */
const stimuli = new WeakMap<ScriptedFms, GpsStimulus>();
export function stimulusFor(fms: ScriptedFms): GpsStimulus {
  let stimulus = stimuli.get(fms);
  if (!stimulus) { stimulus = new GpsStimulus(fms); stimuli.set(fms, stimulus); }
  return stimulus;
}
