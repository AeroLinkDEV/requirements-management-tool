import type { BaroSetting } from "../baro";
import type { ConditionId } from "../conditions";
import type { FmsSide } from "../crossTalk";
import type { DualFmsSystem } from "../dualFms";
import type { FlightSimulator } from "../flight";
import type { PageId } from "../fmsModel";
import type { GpsChoice } from "../gpsSensors";
import { stimulusFor, type GpsOp } from "../gpsStimulus";
import { START_STATES, loadKbtvDemonstration, type StartStateId } from "../kbtvDemo";
import type { RadioDevice } from "../radioManagement";
import type { ScenarioRunner } from "../scenario";
import type { ScriptedFms } from "../scriptedFms";
import { applySensorStimulus, type SensorStimulus } from "../sensorStimulus";
import type { CduFunction } from "../variants";
import { WMM2025_DATABASE } from "../wmm2025";

/**
 * The submission boundary's action vocabulary (#1502 D5 7.3; #1517 I1b): every way outside code changes simulation
 * state, as a journal kind and its payload. An action is plain data. A toggle is submitted as a toggle and resolved
 * when it executes: the journal records the resolved value (on true or false, a PRN list), never "toggle". A target
 * named "guidance" is likewise resolved at execution to the computer the AFCS follows then.
 */

/** Who made a submission (D5 7.1): the kind of source, the control, the surface it was on, and the CDU or unit side. */
export type ActionSource = {
  readonly kind: "ui" | "cdu" | "scenario" | "f14" | "import" | "ios";
  readonly id: string;
  readonly surface: "main" | "popout";
  readonly side?: FmsSide;
};

/** The unit an action acts on, as journaled: a computer, the AFCS, the plant, the radios, a GPS receiver or the run. */
export type ActionTarget = "fms1" | "fms2" | "afcs" | "plant" | "rms" | "gps1" | "gps2" | "ios" | "scenario";

/** A crew or bench selection on the one AFCS (D4 6.1). */
export type AfcsSelection =
  /** HDG SEL in degrees TRUE, as journaled. */
  | { select: "heading"; heading: number }
  /** HDG SEL as the crew enters it, in the angle reference the guidance computer shows; it resolves to TRUE. */
  | { select: "heading"; entry: number }
  | { select: "lnav" }
  | { select: "approach"; on: boolean | "toggle" }
  | { select: "toga" }
  | { select: "vnav" }
  | { select: "altitude"; altitude: number }
  | { select: "verticalSpeed"; fpm: number }
  | { select: "altitudeHold" }
  | { select: "speed"; knots: number }
  | { select: "groundSpeed"; knots: number }
  | { select: "forceTrimRelease" }
  /** The crew's declaration for the published PinS continuation, as the bench holds it (logged with the continuation). */
  | { select: "pinsContinue"; declaration: { readonly basicVfr: boolean; readonly landingAreaVisible: boolean; readonly publishedVisibility: boolean } };

/** A GPS tab toggle, resolved at execution against the stimulus record into the GpsOp it is then. */
export type GpsToggle =
  | { op: "toggle"; field: "doNotUse" | "baroLost" | "receiver" | "rfInput" | "stopped" | "maskLow" }
  | { op: "toggleMask"; prn: number }
  | { op: "toggleOutage"; geo: number };

/** A computer by side, or the one the AFCS follows when the action executes. */
export type UnitChoice = FmsSide | "guidance";

export type KernelAction =
  | { kind: "cdu.key"; side: FmsSide; fn: CduFunction; held: boolean }
  /** The bench's preflight page buttons: the CDU shows a page (an entry point D5 7.3's inventory does not list). */
  | { kind: "cdu.open"; side: FmsSide; page: PageId }
  | { kind: "afcs.select"; selection: AfcsSelection }
  | { kind: "afcs.sourceSelect"; side: FmsSide }
  | { kind: "ios.link"; available: boolean | "toggle" }
  | { kind: "ios.unitPower"; unit: FmsSide; mode: "OFF" | "COLD" | "WARM"; onGround: boolean }
  | { kind: "ios.condition"; unit: FmsSide; condition: ConditionId; on: boolean }
  | { kind: "ios.alert"; unit: FmsSide; text: string }
  /** The atmosphere's declared QNH and the injected baro error (FMS1's sensors, PLANT under D3), with the logged reason. */
  | { kind: "ios.atmosphere"; declaredQnh?: number; baroErrorFt?: number; reason: string }
  | { kind: "ios.wind"; direction: number; speed: number }
  | { kind: "ios.jump"; unit: UnitChoice; op: "sequence" | "overrideDiscontinuity" }
  | { kind: "ios.place"; startState: StartStateId }
  | { kind: "ios.flightFreeze"; on: boolean }
  | { kind: "fms.baroSetting"; unit: FmsSide; setting: BaroSetting }
  | { kind: "fms.gpsSelect"; unit: FmsSide; choice: GpsChoice | "OFF" }
  | { kind: "f14.sensor"; unit: FmsSide; stimulus: SensorStimulus }
  | { kind: "f14.gps"; receiver: 1 | 2; op: GpsOp | GpsToggle }
  | { kind: "f14.radio"; device: RadioDevice; feedback: "NORMAL" | "FAILED" }
  | { kind: "f14.radio"; device: RadioDevice; rejectNext: true }
  | { kind: "config.navdb"; unit: FmsSide; op: "loadKbtv" }
  | { kind: "config.navdb"; unit: FmsSide; op: "activate" }
  /** Content bytes ride in the payload until I1c's content store replaces them with a content hash. */
  | { kind: "config.navdb"; unit: FmsSide; op: "loadArinc424"; name: string; airports: string[]; text: string }
  | { kind: "config.userdb"; unit: FmsSide; text: string }
  | { kind: "config.magvar"; unit: FmsSide; op: "load"; package: unknown }
  | { kind: "config.magvar"; unit: FmsSide; op: "restoreWmm2025" }
  | { kind: "scenario.stop"; runId: string };

export type ActionKind = KernelAction["kind"];

/** An executed action's outcome (D5 7.1): accepted, with what it produced where the bench shows it, or refused with why. */
export type KernelOutcome = { readonly status: "accepted"; readonly detail?: unknown } | { readonly status: "refused"; readonly reason: string };

/** The units the actions reach: the legacy plant adapter's composition (one computer, or the bench's two). */
export type KernelUnits = {
  readonly computers: readonly ScriptedFms[];
  readonly flights: readonly FlightSimulator[];
  readonly system: DualFmsSystem | null;
};

/** What executing an action did: its outcome, the payload as resolved, and the unit it acted on. */
export type Execution = { readonly outcome: KernelOutcome; readonly payload: KernelAction; readonly target: ActionTarget };

/** The kernel state an action can reach besides the units: the flight freeze and the v1 run. */
export interface ActionContext {
  readonly units: KernelUnits;
  readonly runner: Pick<ScenarioRunner, "runId" | "finished" | "abandon"> | null;
  readonly canFreeze: boolean;
  setFlightFreeze(on: boolean): void;
}

const accepted = (detail?: unknown): KernelOutcome => (detail === undefined ? { status: "accepted" } : { status: "accepted", detail });
const refused = (reason: string): KernelOutcome => ({ status: "refused", reason });
const fmsTarget = (side: FmsSide): ActionTarget => (side === 1 ? "fms1" : "fms2");

function unit(units: KernelUnits, side: FmsSide): ScriptedFms {
  const fms = units.computers[side - 1];
  if (!fms) throw new RangeError(`this composition has no FMS ${side}`);
  return fms;
}

/** The side whose computer the AFCS follows now. */
const guidanceSide = (units: KernelUnits): FmsSide => units.system?.guidanceSide ?? 1;
/** The flight simulation the AFCS flies now. */
const afcs = (units: KernelUnits): FlightSimulator => units.system?.simulator ?? units.flights[0];

/** A GPS tab toggle as the operation it is against the stimulus record now. */
function resolveGps(fms: ScriptedFms, index: 0 | 1, op: GpsOp | GpsToggle): GpsOp {
  const state = stimulusFor(fms).state(index);
  switch (op.op) {
    case "toggle":
      switch (op.field) {
        case "doNotUse": return { op: "sbas", doNotUse: !state.doNotUse };
        case "baroLost": return { op: "baroLost", on: !state.baroLost };
        case "maskLow": return { op: "maskLow", on: state.lowPrns.length === 0 };
        case "receiver": return { op: "fault", fault: "RECEIVER", on: !state.receiver };
        case "rfInput": return { op: "fault", fault: "RF_INPUT", on: !state.rfInput };
        case "stopped": return { op: "fault", fault: "STOP_TRANSMITTING", on: !state.stopped };
      }
      break;
    case "toggleMask": return { op: "mask", prns: state.masked.includes(op.prn) ? state.masked.filter(prn => prn !== op.prn) : [...state.masked, op.prn] };
    case "toggleOutage": return { op: "sbas", outage: state.outage.includes(op.geo) ? state.outage.filter(geo => geo !== op.geo) : [...state.outage, op.geo] };
    default: return op;
  }
}

/**
 * Executes one action in the open ACTION phase. Each makes exactly the unit calls the bench's control made before
 * I1b, in the same order and in the same computations, so routing it through the kernel changes no state (#1517: the
 * census stays bit-identical). It never polls the runner or integrates.
 */
export function executeAction(context: ActionContext, action: KernelAction): Execution {
  const { units } = context;
  switch (action.kind) {
    case "cdu.key":
      unit(units, action.side).press(action.fn, { held: action.held });
      return { outcome: accepted(), payload: action, target: fmsTarget(action.side) };
    case "cdu.open":
      unit(units, action.side).open(action.page);
      return { outcome: accepted(), payload: action, target: fmsTarget(action.side) };
    case "afcs.select": return selectAfcs(units, action);
    case "afcs.sourceSelect": {
      if (!units.system) return { outcome: refused("this composition has one computer"), payload: action, target: "afcs" };
      units.system.selectGuidance(action.side);
      return { outcome: accepted(), payload: action, target: "afcs" };
    }
    case "ios.link": {
      const system = units.system;
      if (!system) return { outcome: refused("this composition has no cross-talk link"), payload: action, target: "ios" };
      const available = action.available === "toggle" ? !system.linked : action.available;
      system.setLinkAvailable(available);
      return { outcome: accepted(), payload: { ...action, available }, target: "ios" };
    }
    case "ios.unitPower": {
      const fms = unit(units, action.unit);
      if (action.mode === "OFF") fms.powerOff(); else fms.powerOn(action.mode, action.onGround);
      return { outcome: accepted(), payload: action, target: fmsTarget(action.unit) };
    }
    case "ios.condition":
      unit(units, action.unit).setCondition(action.condition, action.on);
      return { outcome: accepted(), payload: action, target: fmsTarget(action.unit) };
    case "ios.alert":
      unit(units, action.unit).raiseAlert(action.text);
      return { outcome: accepted(), payload: action, target: fmsTarget(action.unit) };
    case "ios.atmosphere": {
      // The atmosphere is FMS1's sensors' (the bench's sensor owner): the QNH, then the error, as a scenario applies them.
      const owner = units.computers[0];
      const qnh = action.declaredQnh === undefined || owner.declareQnh(action.declaredQnh, action.reason);
      const error = action.baroErrorFt === undefined || owner.setBaroError(action.baroErrorFt, action.reason);
      return { outcome: qnh && error ? accepted() : refused("out of the bench's range"), payload: action, target: "plant" };
    }
    case "ios.wind":
      Object.assign(units.computers[0].wind, { direction: action.direction, speed: action.speed });
      return { outcome: accepted(), payload: action, target: "plant" };
    case "ios.jump": {
      const side = action.unit === "guidance" ? guidanceSide(units) : action.unit;
      const fms = unit(units, side);
      const payload = { ...action, unit: side };
      if (action.op === "sequence") {
        const result = fms.sequence();
        return { outcome: result === "jumped" ? accepted(result) : refused(result), payload, target: fmsTarget(side) };
      }
      return { outcome: fms.overrideDiscontinuity() ? accepted() : refused("no discontinuity at the head of the route"), payload, target: fmsTarget(side) };
    }
    case "ios.place": return place(units, action);
    case "ios.flightFreeze":
      if (!context.canFreeze) return { outcome: refused("this composition has no flight freeze"), payload: action, target: "plant" };
      if (context.runner && !context.runner.finished) return { outcome: refused("a run is in progress: pausing it halts the kernel (control plane)"), payload: action, target: "plant" };
      context.setFlightFreeze(action.on);
      return { outcome: accepted(), payload: action, target: "plant" };
    case "fms.baroSetting":
      return { outcome: unit(units, action.unit).setBaroSetting(action.setting) ? accepted() : refused("outside the altimeter's range"), payload: action, target: fmsTarget(action.unit) };
    case "fms.gpsSelect":
      unit(units, action.unit).selectGpsReceiver(action.choice);
      return { outcome: accepted(), payload: action, target: fmsTarget(action.unit) };
    case "f14.sensor":
      try {
        applySensorStimulus(unit(units, action.unit), action.stimulus);
        return { outcome: accepted(), payload: action, target: fmsTarget(action.unit) };
      } catch (error) {
        return { outcome: refused(error instanceof Error ? error.message : String(error)), payload: action, target: fmsTarget(action.unit) };
      }
    case "f14.gps": {
      // The receivers are the sensor owner's (FMS1), as the GPS sensors tab and the scenarios address them.
      const owner = units.computers[0], index = (action.receiver - 1) as 0 | 1;
      const op = resolveGps(owner, index, action.op);
      const target: ActionTarget = action.receiver === 1 ? "gps1" : "gps2";
      return { outcome: stimulusFor(owner).apply(index, op) ? accepted() : refused("the receiver refused the operation"), payload: { ...action, op }, target };
    }
    case "f14.radio": {
      const rms = units.system?.rms;
      if (!rms) return { outcome: refused("this composition has no shared radios"), payload: action, target: "rms" };
      if ("rejectNext" in action) rms.rejectNext(action.device);
      else rms.injectFailure(action.device, action.feedback === "FAILED");
      return { outcome: accepted(), payload: action, target: "rms" };
    }
    case "config.navdb": {
      const fms = unit(units, action.unit), target = fmsTarget(action.unit);
      if (action.op === "activate") { fms.swapCycles(); return { outcome: accepted(), payload: action, target }; }
      const result = action.op === "loadKbtv" ? loadKbtvDemonstration(fms) : fms.loadArinc424(action.text, action.name, action.airports);
      return { outcome: "refused" in result ? refused(result.refused) : accepted(result), payload: action, target };
    }
    case "config.userdb": {
      const result = unit(units, action.unit).importUserDatabase(action.text);
      return { outcome: "refused" in result ? refused(result.refused.join("; ")) : accepted(result.imported), payload: action, target: fmsTarget(action.unit) };
    }
    case "config.magvar": {
      const fms = unit(units, action.unit);
      const loaded = fms.loadMagvar(action.op === "restoreWmm2025" ? WMM2025_DATABASE : action.package);
      return { outcome: loaded ? accepted({ valid: fms.magvar.valid, name: fms.magvar.database.name }) : refused("unsupported model package"), payload: action, target: fmsTarget(action.unit) };
    }
    case "scenario.stop": {
      const runner = context.runner;
      if (!runner || runner.runId !== action.runId) return { outcome: refused("no such run"), payload: action, target: "scenario" };
      if (runner.finished) return { outcome: refused("the run has already ended"), payload: action, target: "scenario" };
      runner.abandon();
      return { outcome: accepted(), payload: action, target: "scenario" };
    }
  }
}

/** The AFCS selections, each as the bench's control made it: on the AFCS's flight simulation and guidance computer now. */
function selectAfcs(units: KernelUnits, action: Extract<KernelAction, { kind: "afcs.select" }>): Execution {
  const sim = afcs(units), fms = unit(units, guidanceSide(units)), selection = action.selection;
  const done = (ok: unknown, payload: KernelAction = action, reason = "not available now"): Execution =>
    ({ outcome: ok === false ? refused(reason) : accepted(), payload, target: "afcs" });
  switch (selection.select) {
    case "heading": {
      // The crew's entry is in the reference the guidance computer shows (TRUE while it has failed); journaled TRUE.
      const heading = "heading" in selection ? selection.heading : fms.hasCondition("fmsFail") ? selection.entry : fms.angleFromEntry(selection.entry);
      if (heading === null) return done(false, action, "no magnetic reference for the entry");
      sim.selectHeading(heading);
      return done(true, { kind: "afcs.select", selection: { select: "heading", heading } });
    }
    case "lnav": return done(sim.armLnav());
    case "approach": {
      const on = selection.on === "toggle" ? !fms.approachArmed : selection.on;
      const result = fms.armApproach(on);
      return done(!on || result, { kind: "afcs.select", selection: { select: "approach", on } }, "the approach cannot be armed");
    }
    case "toga": {
      // A TOGA the FMS refuses still engages the AFCS go-around, as the bench always has (no behaviour fix in I1).
      const missed = fms.goAround();
      sim.engageGoAround();
      return done(missed, action, "the FMS refused the missed approach: none ahead, or it has failed");
    }
    case "vnav": return done(sim.engageVnav());
    case "altitude": return done(sim.selectAltitude(selection.altitude));
    case "verticalSpeed": return done(sim.engageVerticalSpeed(selection.fpm));
    case "altitudeHold": return done(sim.engageAltitudeHold());
    case "speed": return done(sim.selectSpeed(selection.knots));
    case "groundSpeed": return done(sim.engageGroundSpeed(selection.knots));
    case "forceTrimRelease": return done(sim.releaseForceTrim());
    case "pinsContinue": {
      return done(sim.proceedFromPins(selection.declaration), action, "MAP, chart or crew conditions unavailable");
    }
  }
}

/**
 * A start state's placement (T16): the plant and the route set up as the start state says, in one computation. On the
 * bench's two computers its result is then copied to FMS 2, as the bench always has.
 */
function place(units: KernelUnits, action: Extract<KernelAction, { kind: "ios.place" }>): Execution {
  const fms = units.computers[0], flight = units.flights[0], system = units.system;
  const result = fms.compute(() => {
    const outcome = START_STATES[action.startState].setUp(fms, flight);
    if (system) { fms.dualOperation?.settingsChanged(); fms.dualOperation?.finishEdit(true); system.computers[1].observeAircraft(fms); }
    return outcome;
  });
  return { outcome: "refused" in result ? refused(result.refused) : accepted(), payload: action, target: "plant" };
}
