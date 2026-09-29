import type { GpsLabel, GpsReceiver, Override, StatusLabel, StatusPatch } from "./gps";
import type { ScriptedFms } from "./scriptedFms";

/**
 * What the bench has injected into each of the FMS's receivers (the GPS sensors tab's faults, word overrides and status
 * patches), held for the life of the bench session alongside the receivers themselves, not in the tab. The tab's
 * controls, fault chips and override badges are derived from it, so leaving and reopening the tab shows what is still
 * applied, and each control changes only its own stimulus: nothing is re-applied from a fresh default.
 */
export type SatFault = { prn: number; kind: "RAMP" | "STEP"; amount: number };
export type Spoof = { northM: number; driftEastMps: number };
export type ReceiverStimulus = {
  /** The satellites below 15° when "mask low satellites" was pressed (a terrain mask snapshot), and ones masked by PRN. */
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

const initial = (): ReceiverStimulus => ({
  lowPrns: [], masked: [], jamDb: 0, satFault: null, doNotUse: false, outage: [], ionoStorm: 1,
  receiver: false, rfInput: false, baroLost: false, stopped: false, spoof: null, overrides: {}, statusPatches: {},
});

export class GpsStimulus {
  private readonly states: [ReceiverStimulus, ReceiverStimulus] = [initial(), initial()];
  private readonly fms: ScriptedFms;

  constructor(fms: ScriptedFms) { this.fms = fms; }

  state(index: number): Readonly<ReceiverStimulus> { return this.states[index]; }

  private receiver(index: number): GpsReceiver { return this.fms.gps[index]; }
  /** Record a change to one receiver's stimulus, then let the FMS re-read the receivers. */
  private change(index: number, update: Partial<ReceiverStimulus>) {
    this.states[index] = { ...this.states[index], ...update };
    this.fms.gpsUpdated();
  }

  /** Masking, unless the GPS integrity condition holds the satellite selection (it replaces and then clears it). */
  private deselect(index: number, state: ReceiverStimulus) {
    if (!this.fms.hasCondition("gpsIntegrity")) this.receiver(index).deselect([...new Set([...state.lowPrns, ...state.masked])]);
  }
  setMaskLow(index: number, prns: number[]) { const next = { ...this.states[index], lowPrns: prns }; this.deselect(index, next); this.change(index, { lowPrns: prns }); }
  toggleMasked(index: number, prn: number) {
    const current = this.states[index].masked;
    const masked = current.includes(prn) ? current.filter(p => p !== prn) : [...current, prn];
    this.deselect(index, { ...this.states[index], masked });
    this.change(index, { masked });
  }
  /** The integrity condition has taken the satellite selection: the bench's masking no longer applies. */
  clearMasking() {
    this.states.forEach((state, index) => { if (state.lowPrns.length || state.masked.length) this.states[index] = { ...state, lowPrns: [], masked: [] }; });
  }

  setJamming(index: number, db: number) { this.receiver(index).setJamming(db); this.change(index, { jamDb: db }); }

  setSatFault(index: number, fault: SatFault | null) {
    const previous = this.states[index].satFault;
    if (previous && previous.prn !== fault?.prn) this.receiver(index).satelliteFault(previous.prn, null);
    if (fault) this.receiver(index).satelliteFault(fault.prn, fault.kind === "RAMP" ? { kind: "RAMP", metresPerSecond: fault.amount } : { kind: "STEP", metres: fault.amount });
    else if (previous) this.receiver(index).satelliteFault(previous.prn, null);
    this.change(index, { satFault: fault });
  }

  setSbas(index: number, sbas: Partial<Pick<ReceiverStimulus, "doNotUse" | "outage" | "ionoStorm">>) {
    const next = { ...this.states[index], ...sbas };
    this.receiver(index).setSbas({ doNotUse: next.doNotUse, outage: next.outage, ionoStorm: next.ionoStorm });
    this.change(index, sbas);
  }

  setFault(index: number, fault: "receiver" | "rfInput" | "stopped", on: boolean) {
    this.receiver(index).injectFault(fault === "receiver" ? "RECEIVER" : fault === "rfInput" ? "RF_INPUT" : "STOP_TRANSMITTING", on);
    this.change(index, { [fault]: on });
  }

  setBaroLost(index: number, lost: boolean) { this.fms.setGpsBaro(index, !lost); this.change(index, { baroLost: lost }); }

  setSpoof(index: number, spoof: Spoof | null) {
    this.receiver(index).setSpoof(spoof ? { northM: spoof.northM, eastM: 0, driftNorthMps: 0, driftEastMps: spoof.driftEastMps } : null);
    this.change(index, { spoof });
  }

  setOverride(index: number, label: GpsLabel, override: Override | null, text = "") {
    this.receiver(index).override(label, override);
    const overrides = { ...this.states[index].overrides };
    if (override) overrides[label] = { override, text }; else delete overrides[label];
    this.change(index, { overrides });
  }

  /** A typed status patch; false, and nothing recorded, when the receiver refuses it. null clears the label's patch. */
  setStatusPatch<L extends StatusLabel>(index: number, label: L, patch: StatusPatch[L] | null): boolean {
    if (!this.receiver(index).overrideStatus(label, patch)) return false;
    const statusPatches = { ...this.states[index].statusPatches };
    if (patch) statusPatches[label] = patch as Record<string, unknown>; else delete statusPatches[label];
    this.change(index, { statusPatches });
    return true;
  }
}

/** One stimulus record per bench session: keyed to the FMS that owns the receivers, so a restart starts clean. */
const stimuli = new WeakMap<ScriptedFms, GpsStimulus>();
export function stimulusFor(fms: ScriptedFms): GpsStimulus {
  let stimulus = stimuli.get(fms);
  if (!stimulus) { stimulus = new GpsStimulus(fms); stimuli.set(fms, stimulus); }
  return stimulus;
}
