import type { FmsSide, RadioManagementPort } from "./crossTalk";

export type RadioState = { com1: string; com1Stby: string; com2: string; com2Stby: string; nav1: string; nav2: string; adf: string; adf2: string; tpdr: string; tpdr2: string };
export type RadioKey = keyof RadioState;
export type RadioDevice = Exclude<RadioKey, "com1Stby" | "com2Stby">;
export type RadioRequest = { id: number; side: FmsSide; device: RadioDevice; value: string; at: number; status: "PENDING" | "ACK" | "FAILED" };
export const DEFAULT_RADIOS: RadioState = { com1: "121.500", com1Stby: "126.700", com2: "119.100", com2Stby: "133.600", nav1: "113.90", nav2: "116.70", adf: "0350", adf2: "0280", tpdr: "1200", tpdr2: "1200" };

/** M300 3-26: shared radio devices remain accessible when the FMS cross-talk link fails.
 * Burst/feedback latency and timeout are declared laboratory parameters, not OEM bus timing. */
export class RadioManagementSystem {
  private active = { ...DEFAULT_RADIOS };
  private standby: [Pick<RadioState, "com1Stby" | "com2Stby">, Pick<RadioState, "com1Stby" | "com2Stby">] = [
    { com1Stby: DEFAULT_RADIOS.com1Stby, com2Stby: DEFAULT_RADIOS.com2Stby },
    { com1Stby: DEFAULT_RADIOS.com1Stby, com2Stby: DEFAULT_RADIOS.com2Stby },
  ];
  private sequence = 0;
  private history: RadioRequest[] = [];
  private failed = new Set<RadioDevice>();
  private swaps = new Map<number, { side: FmsSide; key: "com1Stby" | "com2Stby"; previous: string }>();
  private readonly clock: () => number;
  private readonly linked: () => boolean;
  private readonly notify: () => void;
  private readonly latency: number;
  private readonly timeout: number;
  constructor(clock: () => number, linked: () => boolean, notify: () => void, latency: number, timeout: number) {
    this.clock = clock; this.linked = linked; this.notify = notify; this.latency = latency; this.timeout = timeout;
  }
  get requests() { return structuredClone(this.history); }
  injectFailure(device: RadioDevice, failed: boolean) { if (failed) this.failed.add(device); else this.failed.delete(device); this.notify(); }
  private tune(side: FmsSide, key: RadioKey, value: string): number | null {
    if (key === "com1Stby" || key === "com2Stby") {
      this.standby[side - 1][key] = value;
      if (this.linked()) this.standby[2 - side][key] = value;
      this.notify(); return null;
    }
    // One physical device serializes bursts from either computer. A new request supersedes an unacknowledged one.
    for (const request of this.history) if (request.device === key && request.status === "PENDING") {
      request.status = "FAILED"; this.swaps.delete(request.id);
    }
    const id = ++this.sequence;
    this.history.unshift({ id, side, device: key, value, at: this.clock(), status: "PENDING" });
    this.history = this.history.slice(0, 30); this.notify(); return id;
  }
  port(side: FmsSide): RadioManagementPort {
    // Port getter receiver differs from the physical radio system.
    // eslint-disable-next-line @typescript-eslint/no-this-alias
    const system = this;
    return {
      get state() { return { ...system.active, ...system.standby[side - 1] }; },
      get requests() { return system.requests.filter(request => request.side === side); },
      tune(key, value) { system.tune(side, key, value); },
      swap(key) {
        const standby = `${key}Stby` as "com1Stby" | "com2Stby";
        const id = system.tune(side, key, system.standby[side - 1][standby]);
        if (id !== null) system.swaps.set(id, { side, key: standby, previous: system.active[key] });
      },
    };
  }
  tick() {
    let changed = false;
    for (const request of this.history) {
      if (request.status !== "PENDING") continue;
      const elapsed = (this.clock() - request.at) / 1000;
      if (this.failed.has(request.device)) {
        if (elapsed < this.timeout) continue;
        request.status = "FAILED"; this.swaps.delete(request.id); changed = true;
      } else if (elapsed >= this.latency) {
        this.active[request.device] = request.value; request.status = "ACK"; changed = true;
        const swap = this.swaps.get(request.id);
        if (swap) { this.tune(swap.side, swap.key, swap.previous); this.swaps.delete(request.id); }
      }
    }
    if (changed) this.notify();
  }
}
