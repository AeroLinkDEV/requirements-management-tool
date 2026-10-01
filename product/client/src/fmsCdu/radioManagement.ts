import type { FmsSide, RadioManagementPort } from "./crossTalk";

export type RadioState = { com1: string; com1Stby: string; com2: string; com2Stby: string; nav1: string; nav2: string; adf: string; adf2: string; tpdr: string; tpdr2: string; tacan: string };
export type RadioKey = keyof RadioState;
export type RadioDevice = Exclude<RadioKey, "com1Stby" | "com2Stby">;
/**
 * A tune command's status (plan C3): PENDING until the radio's feedback confirms it (ACK); REJECTED when the radio
 * refuses it; SUPERSEDED when a newer command for the same radio replaces it before it is confirmed; TIMEOUT when no
 * feedback comes within the timeout. Only REJECTED and TIMEOUT are a failed tuning (M300 13-3, amber).
 */
export type CommandStatus = "PENDING" | "ACK" | "REJECTED" | "SUPERSEDED" | "TIMEOUT";
export type RadioRequest = { id: number; side: FmsSide; device: RadioDevice; value: string; at: number; status: CommandStatus };
/**
 * A radio's separate internal states (plan C3). The control path carries tune commands; the measurement bus carries the
 * radio's words to the FMS; the receiver is the radio's own failure report. Station reception is per measurement. These
 * are internal facts: each Appendix E message is raised from its own row's predicate over them (RADIO_MESSAGE_ROWS).
 */
export type RadioFaults = { controlPath: "NORMAL" | "LOST"; measurementBus: "NORMAL" | "LOST"; receiver: "NORMAL" | "FAILED" };
export const NO_FAULTS: RadioFaults = { controlPath: "NORMAL", measurementBus: "NORMAL", receiver: "NORMAL" };
/** The DME transceivers: they scan for DME/DME and pair with their NAV for VOR/DME (M300 12-16, 13-21). */
export type DmeDevice = "dme1" | "dme2";
export type NavMode = "AUTO" | "MAN";
/** A message one computer raises, with the inhibit its row declares (evaluated by the computer, which knows its state). */
export type RadioEvent = { kind: "alert" | "advisory"; text: string; row: string; inhibit?: "polarOrRoll"; configuredBy?: string };
/** M300 13-23, 13-25: a radio self-test from the NAV and ADF pages. */
export type RadioTestState = "READY" | "CONFIRM?" | "STARTED" | "PASS" | "FAIL" | "TIMEOUT";
export type TestableDevice = "nav1" | "nav2" | "dme1" | "dme2" | "adf" | "adf2";
/** Laboratory: how long a self-test runs before its result. */
export const RADIO_TEST_S = 3;
/** M300 13-24: the ADF's mode (ANT gives no bearing), its BFO, and how its bearing is shown. */
export type AdfSettings = { mode: "ADF" | "ANT"; bfo: boolean; bearing: "REL" | "MAG" | "TRUE" };
/** The M300 name of each device in its messages (NAV-configured names; the transponder is ATC). */
export const RADIO_NAMES: Record<RadioDevice | DmeDevice, string> = {
  com1: "COM1", com2: "COM2", nav1: "NAV1", nav2: "NAV2", adf: "ADF1", adf2: "ADF2", tpdr: "ATC1", tpdr2: "ATC2", dme1: "DME1", dme2: "DME2", tacan: "TACAN",
};

/**
 * The fault-driven Appendix E rows (plan C3), each raised from its own row's predicate. One fault can meet an alert row
 * and an advisory row, and then both are raised. A row is raised once when its predicate becomes true, on both computers
 * (the radios are shared), and may be raised again after it has cleared.
 */
export const RADIO_MESSAGE_ROWS: readonly { row: string; kind: "alert" | "advisory"; devices: readonly (RadioDevice | DmeDevice)[];
  text: (name: string) => string; met: (faults: RadioFaults) => boolean; inhibit?: "polarOrRoll"; configuredBy?: string }[] = [
  // E-2: a failure of the ADF radio or of its receiver communication bus; only if configured; inhibited in the polar
  // area or above 20 degrees of roll.
  { row: "E-2", kind: "alert", devices: ["adf", "adf2"], text: name => `${name} CONTROL LOST`,
    met: faults => faults.receiver === "FAILED" || faults.measurementBus === "LOST", inhibit: "polarOrRoll", configuredBy: "adfControlLostAlert" },
  // E-21: the same ADF condition, as a maintenance advisory.
  { row: "E-21", kind: "advisory", devices: ["adf", "adf2"], text: name => `${name} FAILED`, met: faults => faults.receiver === "FAILED" || faults.measurementBus === "LOST" },
  // E-23: a failure of the DME communication bus.
  { row: "E-23", kind: "advisory", devices: ["dme1", "dme2"], text: name => `${name} FAILED`, met: faults => faults.measurementBus === "LOST" },
  // E-27: a failure of the NAV (VOR/ILS) radio or of its communication bus.
  { row: "E-27", kind: "advisory", devices: ["nav1", "nav2"], text: name => `${name} FAILED`, met: faults => faults.receiver === "FAILED" || faults.measurementBus === "LOST" },
];
/**
 * The command-timeout rows (plan C3): the requesting computer cannot control the radio. NAV is E-13 and the ADF's E-2
 * (its control lost); the others keep their catalogue CONTROL LOST. A command-only timeout leaves a live measurement bus
 * untouched: no FAILED advisory row is met by it.
 */
const TIMEOUT_ROW: Partial<Record<RadioDevice, { row: string; inhibit?: "polarOrRoll"; configuredBy?: string }>> = {
  nav1: { row: "E-13" }, nav2: { row: "E-13" }, adf: { row: "E-2", inhibit: "polarOrRoll", configuredBy: "adfControlLostAlert" },
  adf2: { row: "E-2", inhibit: "polarOrRoll", configuredBy: "adfControlLostAlert" },
};

export const DEFAULT_RADIOS: RadioState = { com1: "121.500", com1Stby: "126.700", com2: "119.100", com2Stby: "133.600", nav1: "113.90", nav2: "116.70", adf: "0350", adf2: "0280", tpdr: "1200", tpdr2: "1200", tacan: "017X" };

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
  private faultState = new Map<RadioDevice | DmeDevice, RadioFaults>();
  /** Bench stimulus: the radio refuses its next tune command (REJECTED). */
  private rejecting = new Set<RadioDevice>();
  private navModes: Record<"nav1" | "nav2", NavMode> = { nav1: "AUTO", nav2: "AUTO" };
  private events: [RadioEvent[], RadioEvent[]] = [[], []];
  /** The fault rows currently met, so each is raised once per episode. */
  private metRows = new Set<string>();
  /** DME HOLD (M300 13-22): the frequency a held DME stays on, whatever its NAV is retuned to. */
  private held: Record<DmeDevice, string | null> = { dme1: null, dme2: null };
  private adfSettings: Record<"adf" | "adf2", AdfSettings> = { adf: { mode: "ADF", bfo: false, bearing: "REL" }, adf2: { mode: "ADF", bfo: false, bearing: "REL" } };
  private tests = new Map<TestableDevice, { state: RadioTestState; startedAt: number | null }>();
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
  /** #1350's device failure: the device stops taking commands (its control path is lost; it keeps reporting). */
  injectFailure(device: RadioDevice, failed: boolean) { this.setFaults(device, { controlPath: failed ? "LOST" : "NORMAL" }); }
  faults(device: RadioDevice | DmeDevice): RadioFaults { return { ...(this.faultState.get(device) ?? NO_FAULTS) }; }
  /** Bench stimulus (plan C3, F14): set any of a radio's internal states. Fault rows are re-evaluated at once. */
  setFaults(device: RadioDevice | DmeDevice, change: Partial<RadioFaults>) {
    const next = { ...this.faults(device), ...change };
    if (next.controlPath === "NORMAL" && next.measurementBus === "NORMAL" && next.receiver === "NORMAL") this.faultState.delete(device);
    else this.faultState.set(device, next);
    for (const row of RADIO_MESSAGE_ROWS) {
      if (!row.devices.includes(device)) continue;
      const key = `${row.row} ${device}`, met = row.met(next);
      if (met && !this.metRows.has(key)) {
        this.metRows.add(key);
        // The radios are shared: both computers see the fault and raise the row.
        for (const queue of this.events) queue.push({ kind: row.kind, text: row.text(RADIO_NAMES[device]), row: row.row,
          ...(row.inhibit ? { inhibit: row.inhibit } : {}), ...(row.configuredBy ? { configuredBy: row.configuredBy } : {}) });
      }
      if (!met) this.metRows.delete(key);
    }
    this.notify();
  }
  rejectNext(device: RadioDevice) { this.rejecting.add(device); }
  /** Whether the radio's words reach the FMS and are usable for navigation: bus and receiver healthy, and not testing. */
  private measuring(device: RadioDevice | DmeDevice) {
    const faults = this.faults(device);
    const testing = this.tests.get(device as TestableDevice)?.state === "STARTED";
    return faults.measurementBus === "NORMAL" && faults.receiver === "NORMAL" && !testing;
  }
  private tune(side: FmsSide, key: RadioKey, value: string): number | null {
    if (key === "com1Stby" || key === "com2Stby") {
      this.standby[side - 1][key] = value;
      if (this.linked()) this.standby[2 - side][key] = value;
      this.notify(); return null;
    }
    // One physical device serializes bursts from either computer. A new request supersedes an unacknowledged one.
    for (const request of this.history) if (request.device === key && request.status === "PENDING") {
      request.status = "SUPERSEDED"; this.swaps.delete(request.id);
    }
    const id = ++this.sequence;
    this.history.unshift({ id, side, device: key, value, at: this.clock(), status: "PENDING" });
    this.history = this.history.slice(0, 30); this.notify(); return id;
  }
  /** The frequency a radio is already on when the FMS powers up and reads it back (M300 13-1): the demonstration's warm start. */
  presetActive(device: RadioDevice, value: string) { this.active[device] = value; this.notify(); }
  // ---- plan F8b: the NAV and ADF page controls, kept with the shared devices.
  dmeHold(device: DmeDevice) { return this.held[device]; }
  /** DME HOLD ON freezes the DME on its NAV's present frequency; OFF returns it to follow the NAV (M300 13-22). */
  setDmeHold(device: DmeDevice, on: boolean) {
    this.held[device] = on ? this.active[device === "dme1" ? "nav1" : "nav2"] : null;
    this.notify();
  }
  adf(device: "adf" | "adf2"): AdfSettings { return { ...this.adfSettings[device] }; }
  setAdf(device: "adf" | "adf2", settings: Partial<AdfSettings>) { this.adfSettings[device] = { ...this.adfSettings[device], ...settings }; this.notify(); }
  testState(device: TestableDevice): RadioTestState { return this.tests.get(device)?.state ?? "READY"; }
  /** The test key: READY (or a result) asks CONFIRM?, CONFIRM? starts the test (M300 13-23). */
  pressTest(device: TestableDevice) {
    const state = this.testState(device);
    if (state === "STARTED") return;
    this.tests.set(device, state === "CONFIRM?" ? { state: "STARTED", startedAt: this.clock() } : { state: "CONFIRM?", startedAt: null });
    this.notify();
  }
  /** Whether a DME transceiver's ranges are usable (plan C3). */
  dmeReceiving(device: DmeDevice) { return this.measuring(device); }
  /** The frequency a radio reports it is on, or null while its words do not reach the FMS or it is testing (plan C3). */
  receiving(device: RadioDevice) { return this.measuring(device) ? this.active[device] : null; }
  port(side: FmsSide): RadioManagementPort {
    // Port getter receiver differs from the physical radio system.
    // eslint-disable-next-line @typescript-eslint/no-this-alias
    const system = this;
    return {
      get state() { return { ...system.active, ...system.standby[side - 1] }; },
      get requests() { return system.requests.filter(request => request.side === side); },
      tune(key, value) {
        if (key === "nav1" || key === "nav2") system.navModes[key] = "MAN";
        system.tune(side, key, value);
      },
      receiving(device) { return system.receiving(device); },
      dmeReceiving(device) { return system.dmeReceiving(device); },
      navMode(device) { return system.navModes[device]; },
      setNavMode(device, mode) { system.navModes[device] = mode; system.notify(); },
      autoTune(device, value) {
        if (system.navModes[device] !== "AUTO") return;
        const pending = system.history.find(request => request.device === device && request.status === "PENDING");
        if ((pending?.value ?? system.active[device]) !== value) system.tune(side, device, value);
      },
      drainEvents() { return system.events[side - 1].splice(0); },
      faults(device) { return system.faults(device); },
      dmeHold(device) { return system.dmeHold(device); },
      setDmeHold(device, on) { system.setDmeHold(device, on); },
      adf(device) { return system.adf(device); },
      setAdf(device, settings) { system.setAdf(device, settings); },
      testState(device) { return system.testState(device); },
      pressTest(device) { system.pressTest(device); },
      swap(key) {
        const standby = `${key}Stby` as "com1Stby" | "com2Stby";
        const id = system.tune(side, key, system.standby[side - 1][standby]);
        if (id !== null) system.swaps.set(id, { side, key: standby, previous: system.active[key] });
      },
    };
  }
  tick() {
    let changed = false;
    for (const [device, test] of this.tests) {
      if (test.state !== "STARTED" || test.startedAt === null || (this.clock() - test.startedAt) / 1000 < RADIO_TEST_S) continue;
      // The result is the radio's: a failed receiver reports FAIL; a lost bus never answers (TIMEOUT).
      const faults = this.faults(device);
      test.state = faults.measurementBus === "LOST" ? "TIMEOUT" : faults.receiver === "FAILED" ? "FAIL" : "PASS";
      changed = true;
    }
    for (const request of this.history) {
      if (request.status !== "PENDING") continue;
      const elapsed = (this.clock() - request.at) / 1000;
      if (elapsed < this.latency) continue;
      if (this.rejecting.delete(request.device)) {
        request.status = "REJECTED"; this.swaps.delete(request.id); changed = true; continue;
      }
      // A command needs the control path and, for its feedback, the measurement bus.
      if (this.faults(request.device).controlPath === "LOST" || this.faults(request.device).measurementBus === "LOST") {
        if (elapsed < this.timeout) continue;
        request.status = "TIMEOUT"; this.swaps.delete(request.id); changed = true;
        // The requesting computer cannot control the radio (its row: E-13 for NAV, E-2 for the ADF).
        const row = TIMEOUT_ROW[request.device];
        this.events[request.side - 1].push({ kind: "alert", text: `${RADIO_NAMES[request.device]} CONTROL LOST`, row: row?.row ?? "catalogue",
          ...(row?.inhibit ? { inhibit: row.inhibit } : {}), ...(row?.configuredBy ? { configuredBy: row.configuredBy } : {}) });
        continue;
      }
      this.active[request.device] = request.value; request.status = "ACK"; changed = true;
      const swap = this.swaps.get(request.id);
      if (swap) { this.tune(swap.side, swap.key, swap.previous); this.swaps.delete(request.id); }
    }
    if (changed) this.notify();
  }
}
