import { CONDITIONS, UNMODELLED_CONDITIONS, type ConditionId } from "./conditions";
import { FlightSimulator } from "./flight";
import { distanceNm } from "./fmsModel";
import { START_STATES, type StartStateId } from "./kbtvDemo";
import { GPS_MODEL_VERSION, type GpsMode } from "./gps";
import { PROFILES, profileById, profileSummary } from "./profile";
import { describeGpsOp, gpsOpProblem, stimulusFor, type GpsOp } from "./gpsStimulus";
import { ScriptedFms } from "./scriptedFms";
import { SCRATCHPAD_LINE, screenText, type Lamp } from "./screen";
import { SURFACES, surfaceById } from "./surface";
import type { CduFunction } from "./variants";

// Scripted test scenarios for the FMS Test Bench (product/docs/FMS_TEST_BENCH.md, step 8). A scenario is an ordered
// list of steps; each waits for its trigger, then acts on the simulation or checks what the crew would see. Steps
// run one after another, so a scenario reads as a test procedure does. Scenarios are plain data: they can be
// recorded on the bench, saved as JSON, played back, and written out as test procedure text.
//
// The run contract (independent review of 27 and 28 September, N01 to N08):
// - Time moves in fixed ticks of TICK_SECONDS. Each tick advances the clock, integrates the flight and then lets the
//   runner observe, in that order, whether the bench or a headless test drives it, and at any bench rate.
// - A step due at time t runs at the first tick at or after t. Nothing runs after maxSeconds: at the first tick past
//   it, every step not yet finished is not reached.
// - An expectation is checked at each tick from its trigger. With `within` w it passes at the first tick where it
//   holds no later than w seconds after the trigger, and fails at the first tick at or after the deadline where it
//   does not hold; without `within` it is checked once. An observation strictly after the deadline never satisfies it,
//   even when the deadline falls between ticks (a window shorter than a tick can only pass at its trigger).
// - A scenario is validated before it runs. An unknown step, a malformed payload or a step that throws is an
//   invalid scenario or an execution error, never a pass. A run with no checks is "no checks", not a pass.

export const TICK_SECONDS = 0.25;

/** When a step runs. Times are simulated seconds from the start of the run. */
export type Trigger =
  | { kind: "start" }
  | { kind: "time"; seconds: number }
  | { kind: "distance"; waypoint: string; nm: number }
  | { kind: "fafDistance"; nm: number }
  | { kind: "active"; waypoint: string }
  /** This many seconds after the step before it finished: the timing inside a stage, wherever the stage began. */
  | { kind: "after"; seconds: number }
  /** When the aircraft is at or below this altitude, feet MSL (over the declared sea, its radio height). */
  | { kind: "below"; feet: number }
  /**
   * When the altimeter reads at or above this altitude, feet MSL, to the foot: a crew action on reaching an altitude
   * (the truth settles onto a captured altitude without ever reaching it exactly).
   */
  | { kind: "above"; feet: number };

export type Action =
  | { kind: "keys"; keys: CduFunction[] }
  | { kind: "type"; text: string }
  | { kind: "condition"; condition: ConditionId; on: boolean }
  | { kind: "alert"; text: string }
  | { kind: "procedure"; procedure: "SID" | "STAR" | "APPROACH"; ident: string; transition?: string }
  /** APPR: arms the approach, or (on false) presses it off: a disarm, or after capture a cancellation. */
  | { kind: "armApproach"; on?: boolean }
  | { kind: "goAround" }
  | { kind: "proceedPins"; basicVfr: boolean; landingAreaVisible: boolean; publishedVisibility: boolean }
  /** The wind the air mass moves with, from `direction` (degrees true) at `speed` kt: a laboratory stimulus. */
  | { kind: "wind"; direction: number; speed: number }
  /**
   * The crew's autopilot selections under the helicopter profile: preselect an altitude, engage a vertical speed (fpm)
   * toward it, hold the present altitude, or select a speed (knots). Each field given is applied, in that order.
   */
  | { kind: "autopilot"; altitude?: number; verticalSpeed?: number; hold?: boolean; speed?: number; heading?: number; lnav?: boolean; hover?: boolean; transitionUp?: boolean }
  /**
   * The autopilot's engaged mode on each axis (collective, pitch, roll), as the helicopter FMA shows them; and the
   * low-height protection caption the PFD shows ("LOW HT", "LOW HT OFF", or "NONE" for no caption).
   */
  | { kind: "expectAfcs"; collective?: string; pitch?: string; roll?: string; lowHeight?: "LOW HT" | "LOW HT OFF" | "NONE" }
  /**
   * The aircraft's state; each part given is checked. Ground speed within [minGroundSpeed, maxGroundSpeed] kt; track
   * within trackTolerance (5 by default) of track; radio height radioHeight, or altitude (MSL) altitude, ± heightTolerance
   * ft (10 by default), and altitude within [minAltitude, maxAltitude]; within nearMetres (50 by default) of the waypoint
   * near, and no nearer than minNearMetres; cross-track from the active leg at most maxCrossTrack NM.
   */
  | {
    kind: "expectAircraft"; minGroundSpeed?: number; maxGroundSpeed?: number; track?: number; trackTolerance?: number;
    radioHeight?: number; altitude?: number; heightTolerance?: number; minAltitude?: number; maxAltitude?: number;
    near?: string; nearMetres?: number; minNearMetres?: number; maxCrossTrack?: number;
  }
  /** The hover procedure's refusal at TDN, as the crew sees it (refused) and as the planner or check put it (reason). */
  | { kind: "expectHover"; refused?: string; reason?: string }
  | { kind: "expectLine"; line: number; pattern: string }
  | { kind: "expectScratchpad"; text: string }
  /** An alert in the recall list; with fresh, only one raised since the last action step began (its own alerts count). */
  | { kind: "expectAlert"; text: string; fresh?: boolean }
  | { kind: "expectNoAlert"; text: string }
  | { kind: "expectLamp"; lamp: Lamp; lit: boolean }
  | { kind: "expectActive"; waypoint: string }
  /**
   * The approach as the flight simulation and the FMS have it: its type (ILS, LPV, LNAV/VNAV, LNAV, NO APPR), the
   * approach mode (OFF, ARMED, CAPTURED), the vertical mode, and the vertical deviation within a limit, feet. Each field
   * given is checked; the others are not.
   */
  | { kind: "expectApproach"; type?: string; state?: "OFF" | "ARMED" | "CAPTURED"; verticalMode?: string; maxVerticalFt?: number }
  /** A stimulus on GPS receiver 1 or 2, as the GPS sensors tab applies it (gpsStimulus.ts). */
  | { kind: "gps"; receiver: 1 | 2; stimulus: GpsOp }
  /** The FMS's navigation source: a receiver, or NONE when it navigates on something else. */
  | { kind: "expectGpsSource"; source: "GPS1" | "GPS2" | "NONE" }
  /** The approach annunciated (FMS approachType). */
  | { kind: "expectApproachLevel"; level: "LPV" | "LNAV/VNAV" | "LNAV" | "NO APPR" }
  /** A receiver's own operating mode (its 273). */
  | { kind: "expectReceiverMode"; receiver: 1 | 2; mode: GpsMode };

/** One step. An expectation not yet met waits up to `within` seconds for it before failing. */
export type ScenarioStep = { when: Trigger; action: Action; within?: number };

export type Scenario = {
  id: string;
  title: string;
  objective: string;
  /** The run ends then: any step not finished is not reached. */
  maxSeconds: number;
  /** A named start state (kbtvDemo.ts START_STATES) the fresh simulation is set up in before the first step. */
  start?: StartStateId;
  /**
   * When the simulated clock starts (ISO 8601), for scenarios whose outcome depends on the GPS sky, which moves with the
   * clock. Without it, the bench starts at the wall clock and a headless run at its own default.
   */
  startTime?: string;
  /**
   * The surface the radio altimeter measures against (surface.ts SURFACES, by id). Without it none is declared and the
   * radio height is NCD everywhere.
   */
  surface?: string;
  /** The aircraft profile the run flies (profile.ts PROFILES, by id); the helicopter profile when not named. */
  profile?: string;
  steps: ScenarioStep[];
};

export type StepStatus = "pending" | "done" | "pass" | "fail" | "not reached" | "error";
export type StepResult = { status: StepStatus; at?: number; actual?: string };

/** How a run ended. Only "passed" is a pass: every check held, and there was at least one. */
export type RunOutcome = "running" | "passed" | "failed" | "no checks" | "timed out" | "stopped" | "invalid" | "error";

/**
 * What the run describes, fixed when it starts, so the report cannot change after it finishes. `data` says what the
 * navigation data is (the active cycle's source); a start state can change the cycle, so both are read after it.
 * `profile` names the aircraft profile (profile.ts) the run flew, and `surface` the radio altimeter's declared surface.
 */
export type RunContext = { variant: string; cycle: string; data?: string; profile?: string; surface?: string };

const isExpectation = (action: Action) => action.kind.startsWith("expect");

/** Characters a scenario's text entry can type, as the keys that type them. */
export const keysFor = (text: string): CduFunction[] =>
  [...text].map(ch => (ch === "." ? "DOT" : ch === "/" ? "SLASH" : ch === "-" ? "PLUSMINUS" : ch === " " ? "SP" : `CHAR_${ch}`) as CduFunction);

/** A regular expression matching a screen line as shown, with any run of spaces standing for layout. */
export const linePattern = (text: string) =>
  `^\\s*${text.trim().replace(/[.*+?^${}()|[\]\\]/g, "\\$&").replace(/ +/g, "\\s+")}\\s*$`;

/** The step in words, for the run log, the report and the test procedure. After the first step, "start" means "then". */
export function describeStep(step: ScenarioStep, index = 0): string {
  const w = step.when;
  const when = w.kind === "start" ? (index > 0 ? "Then" : "At the start") : w.kind === "time" ? `At ${formatSeconds(w.seconds)}` : w.kind === "after" ? `${formatSeconds(w.seconds)} later` : w.kind === "below" ? `At or below ${w.feet} ft` : w.kind === "above" ? `When the altimeter reads ${w.feet} ft or more` : w.kind === "distance" ? `Within ${w.nm} NM of ${w.waypoint}` : w.kind === "fafDistance" ? `Within ${w.nm} NM along the predicted path to the FAF` : `When ${w.waypoint} is the active waypoint`;
  const a = step.action;
  const within = step.within ? ` within ${step.within} s` : "";
  const what = (() => {
    switch (a.kind) {
      case "keys": return `press ${a.keys.map(key => key.replace(/^CHAR_/, "")).join(" ")}`;
      case "type": return `type ${a.text} into the scratchpad`;
      case "condition": return `${a.on ? "inject" : "remove"} the condition ${a.condition}`;
      case "alert": return `raise the alert ${a.text}`;
      case "procedure": return `select the ${a.procedure === "APPROACH" ? "approach" : a.procedure} ${a.ident}${a.transition ? ` via ${a.transition}` : ""}`;
      case "armApproach": return a.on === false ? "press APPR off" : "arm the approach";
      case "goAround": return "press TOGA";
      case "proceedPins": return "declare the conditions for the published PinS continuation";
      case "wind": return `set the wind to ${String(a.direction).padStart(3, "0")}°T / ${a.speed} kt`;
      case "autopilot": return [
        a.altitude !== undefined ? `preselect ${a.altitude} ft` : null, a.verticalSpeed !== undefined ? `engage VS ${a.verticalSpeed} fpm` : null, a.hold ? "engage ALT" : null,
        a.speed !== undefined ? `select ${a.speed} kt` : null, a.heading !== undefined ? `select heading ${a.heading}°` : null, a.lnav ? "arm NAV" : null,
        a.hover ? "engage HOV" : null, a.transitionUp ? "engage TU" : null,
      ].filter(Boolean).join(", then ");
      case "expectAfcs": {
        const modes = a.collective === undefined && a.pitch === undefined && a.roll === undefined ? null
          : `the autopilot modes are ${[a.collective ?? "any", a.pitch ?? "any", a.roll ?? "any"].join(" | ")}`;
        const caption = a.lowHeight === undefined ? null : a.lowHeight === "NONE" ? "no low-height caption is shown" : `the low-height caption is ${a.lowHeight}`;
        return `check that ${[modes, caption].filter(Boolean).join(" and ")}${within}`;
      }
      case "expectAircraft": {
        const parts = [
          a.minGroundSpeed !== undefined || a.maxGroundSpeed !== undefined ? `ground speed ${a.minGroundSpeed ?? 0} to ${a.maxGroundSpeed ?? "any"} kt` : null,
          a.track !== undefined ? `track ${a.track} ± ${a.trackTolerance ?? 5}°` : null,
          a.radioHeight !== undefined ? `radio height ${a.radioHeight} ± ${a.heightTolerance ?? 10} ft` : null,
          a.altitude !== undefined ? `altitude ${a.altitude} ± ${a.heightTolerance ?? 10} ft` : null,
          a.minAltitude !== undefined || a.maxAltitude !== undefined ? `altitude ${a.minAltitude ?? "any"} to ${a.maxAltitude ?? "any"} ft` : null,
          a.near ? `within ${a.nearMetres ?? 50} m of ${a.near}${a.minNearMetres !== undefined ? ` and no nearer than ${a.minNearMetres} m` : ""}` : null,
          a.maxCrossTrack !== undefined ? `cross-track at most ${a.maxCrossTrack} NM` : null,
        ].filter(Boolean);
        return `check that the aircraft is at ${parts.join(", ")}${within}`;
      }
      case "expectLine": return `check that screen line ${a.line + 1} matches /${a.pattern}/${within}`;
      case "expectScratchpad": return a.text ? `check that the scratchpad shows ${a.text}${within}` : `check that the scratchpad is blank${within}`;
      case "expectAlert": return `check that the alert ${a.text} has been raised${a.fresh ? " since the last action" : ""}${within}`;
      case "expectHover": return `check that the hover procedure was refused${a.refused ? ` with ${a.refused}` : ""}${a.reason ? ` (${a.reason})` : ""}${within}`;
      case "expectNoAlert": return `check that the alert ${a.text} has not been raised at that moment`;
      case "expectLamp": return `check that the ${a.lamp} annunciator is ${a.lit ? "lit" : "out"}${within}`;
      case "expectActive": return `check that ${a.waypoint} is the active waypoint${within}`;
      case "expectApproach": {
        const parts = [
          a.type ? `the approach is ${a.type}` : null, a.state ? `the approach mode is ${a.state}` : null,
          a.verticalMode ? `the vertical mode is ${a.verticalMode}` : null,
          a.maxVerticalFt !== undefined ? `the vertical deviation is within ${a.maxVerticalFt} ft` : null,
        ].filter(Boolean);
        return `check that ${parts.join(", ")}${within}`;
      }
      case "gps": return `on GPS ${a.receiver}, ${describeGpsOp(a.stimulus)}`;
      case "expectGpsSource": return a.source === "NONE" ? `check that the FMS is not navigating on GPS${within}` : `check that the FMS navigates on ${a.source}${within}`;
      case "expectApproachLevel": return `check that the approach annunciated is ${a.level}${within}`;
      case "expectReceiverMode": return `check that GPS ${a.receiver} is in ${a.mode} mode${within}`;
    }
  })();
  return when === "Then" ? `Then ${what}.` : `${when}, ${what}.`;
}

const formatSeconds = (seconds: number) => {
  const m = Math.floor(seconds / 60);
  const s = Math.round((seconds % 60) * 100) / 100;
  return m ? `${m} min${s ? ` ${s} s` : ""}` : `${s} s`;
};

// ------------------------------------------------------------------ validation

const KEY = /^(LSK[1-6][LR]|MENU|PREV|NEXT|INIT_REF|RTE|DEP_ARR|LEGS|PROG|EXEC|RADIO|FUEL|MARK|HOLD|FIX|BRT|TPDR|MSG|ANS|SQK_IDT|FMC_COMM|VNAV|TACT|ATC|CLR|SP|SLASH|DOT|PLUSMINUS|CHAR_[A-Z0-9])$/;
const LAMPS = new Set(["FAIL", "MSG", "POS", "OFST", "NPA", "GSM", "SMS", "TX1", "TX2", "RNP", "IND", "ATC", "V/UHF", "HF", "MENU", "EXEC"]);
const CONDITION_IDS = new Set(CONDITIONS.map(condition => condition.id as string));
const IDENT = /^[A-Z0-9]{1,7}$/;
const GPS_MODES = new Set(["SELF_TEST", "INITIALIZATION", "ACQUISITION", "NAV", "SBAS_NAV", "SBAS_PA", "ALT_AIDING", "FAULT"]);
const APPROACH_LEVELS = new Set(["LPV", "LNAV/VNAV", "LNAV", "NO APPR"]);
const receiver = (value: unknown) => value === 1 || value === 2;
const MAX_RUN_SECONDS = 24 * 3600;
const APPROACH_TYPES = new Set(["ILS", "LPV", "LNAV/VNAV", "LNAV", "NO APPR"]);

const finite = (value: unknown, min: number, max: number) => typeof value === "number" && Number.isFinite(value) && value >= min && value <= max;
const text = (value: unknown, pattern: RegExp) => typeof value === "string" && pattern.test(value);

/** Why a trigger is not one the runner supports, or null if it is. */
function triggerProblem(when: unknown): string | null {
  const w = when as Record<string, unknown> | null;
  if (!w || typeof w !== "object") return "a trigger (when) is required";
  switch (w.kind) {
    case "start": return null;
    case "time": return finite(w.seconds, 0, MAX_RUN_SECONDS) ? null : "a time trigger needs seconds between 0 and 86400";
    case "distance": return text(w.waypoint, IDENT) && finite(w.nm, 0.01, 1000) ? null : "a distance trigger needs a waypoint ident and nm between 0.01 and 1000";
    case "fafDistance": return finite(w.nm, 0.01, 1000) ? null : "a FAF path-distance trigger needs nm between 0.01 and 1000";
    case "active": return text(w.waypoint, IDENT) ? null : "an active trigger needs a waypoint ident";
    case "below": return finite(w.feet, -1500, 60000) ? null : "a below trigger needs feet between -1500 and 60000";
    case "above": return finite(w.feet, -1500, 60000) ? null : "an above trigger needs feet between -1500 and 60000";
    case "after": return finite(w.seconds, 0, MAX_RUN_SECONDS) ? null : "an after trigger needs seconds between 0 and 86400";
    default: return `unsupported trigger "${String(w.kind)}"`;
  }
}

/** Why an action is not one the runner supports, or null if it is. */
function actionProblem(action: unknown): string | null {
  const a = action as Record<string, unknown> | null;
  if (!a || typeof a !== "object") return "an action is required";
  switch (a.kind) {
    case "keys": return Array.isArray(a.keys) && a.keys.length > 0 && a.keys.every(key => typeof key === "string" && KEY.test(key)) ? null : "keys must be a non-empty list of CDU functions";
    case "type": return text(a.text, /^[A-Z0-9 ./-]{1,24}$/) ? null : "type needs up to 24 scratchpad characters";
    case "condition": {
      const unmodelled = UNMODELLED_CONDITIONS.find(condition => condition.id === a.condition);
      if (unmodelled) return `${unmodelled.label.toLowerCase()} is not modelled in v1, so a scenario that injects it is refused (rev 3 B3.5 F10)`;
      return typeof a.condition === "string" && CONDITION_IDS.has(a.condition) && typeof a.on === "boolean" ? null : "condition needs a known condition and on true or false";
    }
    case "alert":
    case "expectAlert":
    case "expectNoAlert":
      if (a.fresh !== undefined && (a.kind !== "expectAlert" || typeof a.fresh !== "boolean")) return "fresh applies only to expectAlert, true or false";
      return text(a.text, /^.{1,24}$/) ? null : `${a.kind} needs text of 1 to 24 characters`;
    case "expectHover":
      if (a.refused === undefined && a.reason === undefined) return "expectHover needs refused or reason";
      return (a.refused === undefined || text(a.refused, /^.{1,24}$/)) && (a.reason === undefined || text(a.reason, /^.{1,32}$/)) ? null : "expectHover refused and reason must be short texts";
    // A blank scratchpad is a state worth checking: the text may be empty.
    case "expectScratchpad": return text(a.text, /^.{0,24}$/) ? null : "expectScratchpad needs text of at most 24 characters";
    case "procedure": return (a.procedure === "SID" || a.procedure === "STAR" || a.procedure === "APPROACH") && text(a.ident, /^[A-Z0-9]{1,7}$/) && (a.transition === undefined || text(a.transition, /^[A-Z0-9]{1,7}$/)) ? null : "procedure needs SID, STAR or APPROACH, an ident, and a transition ident when given";
    case "armApproach": return a.on === undefined || typeof a.on === "boolean" ? null : "armApproach on must be true or false when given";
    case "goAround": return null;
    case "proceedPins": return [a.basicVfr, a.landingAreaVisible, a.publishedVisibility].every(value => typeof value === "boolean") ? null : "proceedPins declarations must be true or false";
    case "wind": return finite(a.direction, 0, 360) && finite(a.speed, 0, 150) ? null : "wind needs a direction from 0 to 360 and a speed from 0 to 150 kt";
    case "autopilot": {
      const finite = (v: unknown) => v === undefined || (typeof v === "number" && Number.isFinite(v));
      const flag = (v: unknown) => v === undefined || typeof v === "boolean";
      if (!finite(a.altitude) || !finite(a.verticalSpeed) || !finite(a.speed) || !finite(a.heading) || ![a.hold, a.lnav, a.hover, a.transitionUp].every(flag)) return "autopilot altitude, verticalSpeed, speed and heading must be numbers; hold, lnav, hover and transitionUp true or false";
      return a.altitude === undefined && a.verticalSpeed === undefined && !a.hold && a.speed === undefined && a.heading === undefined && !a.lnav && !a.hover && !a.transitionUp
        ? "autopilot needs at least one selection" : null;
    }
    case "expectAfcs": {
      const mode = (v: unknown) => v === undefined || text(v, /^[A-Z/-]{2,8}$/);
      if (a.collective === undefined && a.pitch === undefined && a.roll === undefined && a.lowHeight === undefined) return "expectAfcs needs at least one of collective, pitch, roll and lowHeight";
      if (a.lowHeight !== undefined && !(["LOW HT", "LOW HT OFF", "NONE"] as unknown[]).includes(a.lowHeight)) return "expectAfcs lowHeight must be LOW HT, LOW HT OFF or NONE";
      return mode(a.collective) && mode(a.pitch) && mode(a.roll) ? null : "expectAfcs modes must be mode names";
    }
    case "expectAircraft": {
      const keys = ["minGroundSpeed", "maxGroundSpeed", "track", "radioHeight", "altitude", "minAltitude", "maxAltitude", "near", "maxCrossTrack"] as const;
      if (keys.every(key => a[key] === undefined)) return `expectAircraft needs at least one of ${keys.join(", ")}`;
      const ranges: [string, number, number][] = [
        ["minGroundSpeed", 0, 500], ["maxGroundSpeed", 0, 500], ["track", 0, 360], ["trackTolerance", 0, 180], ["radioHeight", 0, 2500],
        ["altitude", -1500, 60000], ["minAltitude", -1500, 60000], ["maxAltitude", -1500, 60000], ["heightTolerance", 0, 500],
        ["nearMetres", 0, 100000], ["minNearMetres", 0, 100000], ["maxCrossTrack", 0, 100],
      ];
      for (const [key, min, max] of ranges) if (a[key] !== undefined && !finite(a[key], min, max)) return `expectAircraft ${key} must be between ${min} and ${max}`;
      if (a.near !== undefined && !text(a.near, IDENT)) return "expectAircraft near must be a waypoint ident";
      if ((a.nearMetres !== undefined || a.minNearMetres !== undefined) && a.near === undefined) return "expectAircraft nearMetres and minNearMetres need near";
      return null;
    }
    case "expectLine": {
      if (!(Number.isInteger(a.line) && finite(a.line, 0, SCRATCHPAD_LINE))) return `expectLine needs a line from 0 to ${SCRATCHPAD_LINE}`;
      if (typeof a.pattern !== "string") return "expectLine needs a pattern";
      try { new RegExp(a.pattern); } catch { return `expectLine pattern /${a.pattern}/ is not a valid regular expression`; }
      return null;
    }
    case "expectLamp": return typeof a.lamp === "string" && LAMPS.has(a.lamp) && typeof a.lit === "boolean" ? null : "expectLamp needs a known annunciator and lit true or false";
    case "expectActive": return text(a.waypoint, IDENT) ? null : "expectActive needs a waypoint ident";
    case "expectApproach": {
      if (a.type === undefined && a.state === undefined && a.verticalMode === undefined && a.maxVerticalFt === undefined) return "expectApproach needs at least one of type, state, verticalMode and maxVerticalFt";
      if (a.type !== undefined && !(typeof a.type === "string" && APPROACH_TYPES.has(a.type))) return "expectApproach type must be ILS, LPV, LNAV/VNAV, LNAV or NO APPR";
      if (a.state !== undefined && a.state !== "OFF" && a.state !== "ARMED" && a.state !== "CAPTURED") return "expectApproach state must be OFF, ARMED or CAPTURED";
      if (a.verticalMode !== undefined && !text(a.verticalMode, /^[A-Z ]{2,12}$/)) return "expectApproach verticalMode must be a vertical mode name";
      if (a.maxVerticalFt !== undefined && !finite(a.maxVerticalFt, 0, 10000)) return "expectApproach maxVerticalFt must be between 0 and 10000";
      return null;
    }
    case "gps": {
      if (!receiver(a.receiver)) return `gps needs receiver 1 or 2, not ${JSON.stringify(a.receiver)}`;
      const problem = gpsOpProblem(a.stimulus);
      return problem ? `gps: ${problem}` : null;
    }
    case "expectGpsSource": return a.source === "GPS1" || a.source === "GPS2" || a.source === "NONE" ? null : "expectGpsSource needs GPS1, GPS2 or NONE";
    case "expectApproachLevel": return typeof a.level === "string" && APPROACH_LEVELS.has(a.level) ? null : "expectApproachLevel needs LPV, LNAV/VNAV, LNAV or NO APPR";
    case "expectReceiverMode": return receiver(a.receiver) && typeof a.mode === "string" && GPS_MODES.has(a.mode) ? null : `expectReceiverMode needs receiver 1 or 2 and a mode (${[...GPS_MODES].join(", ")})`;
    default: return `unsupported action "${String(a.kind)}"`;
  }
}

/** Every reason the scenario cannot run as written; empty when it can. */
export function scenarioProblems(value: unknown): string[] {
  const s = value as Partial<Scenario> | null;
  const problems: string[] = [];
  if (!s || typeof s !== "object") return ["not a scenario"];
  if (typeof s.title !== "string" || !s.title.trim()) problems.push("it needs a title");
  if (!finite(s.maxSeconds, TICK_SECONDS, MAX_RUN_SECONDS)) problems.push("it needs maxSeconds between 0.25 and 86400");
  if (s.start !== undefined && !(typeof s.start === "string" && Object.hasOwn(START_STATES, s.start))) problems.push(`unknown start state "${String(s.start)}"`);
  if (s.profile !== undefined && !(typeof s.profile === "string" && profileById(s.profile))) problems.push(`profile must be one of ${PROFILES.map(profile => profile.id).join(", ")}`);
  if (s.surface !== undefined && !(typeof s.surface === "string" && surfaceById(s.surface))) problems.push(`surface must be one of ${SURFACES.map(surface => surface.id).join(", ")}`);
  if (s.startTime !== undefined && !(typeof s.startTime === "string" && /^\d{4}-\d\d-\d\dT/.test(s.startTime) && Number.isFinite(Date.parse(s.startTime)))) problems.push("startTime must be an ISO 8601 date and time");
  if (!Array.isArray(s.steps)) return [...problems, "it needs steps"];
  s.steps.forEach((step, i) => {
    const where = `step ${i + 1}`;
    if (!step || typeof step !== "object") { problems.push(`${where}: not a step`); return; }
    const when = triggerProblem(step.when), action = actionProblem(step.action);
    if (when) problems.push(`${where}: ${when}`);
    if (action) problems.push(`${where}: ${action}`);
    if (step.within !== undefined) {
      if (!finite(step.within, 0, MAX_RUN_SECONDS)) problems.push(`${where}: within must be between 0 and 86400 seconds`);
      else if (!action && !isExpectation(step.action)) problems.push(`${where}: within applies only to a check`);
      else if (!action && step.action.kind === "expectNoAlert") problems.push(`${where}: expectNoAlert is checked at one moment; within would read as "stays absent", which it does not check`);
    }
  });
  return problems;
}

// ------------------------------------------------------------------ running

/**
 * Runs a scenario against a simulation. It observes; it does not fly the aircraft or move the clock. Whoever owns
 * them (the bench, or runHeadless) advances them one tick at a time with advanceTicks, which polls after each tick.
 */
export class ScenarioRunner {
  readonly results: StepResult[];
  readonly scenario: Scenario;
  readonly context: RunContext;
  /** Why the scenario could not run, when it is invalid. */
  readonly problems: readonly string[];
  private readonly fms: ScriptedFms;
  /** The flight simulation, for the approach checks; a run without one cannot make them. */
  private readonly sim: FlightSimulator | null;
  private readonly start: number;
  private next = 0;
  /** When the current step's trigger came, for an expectation that is waiting. */
  private eligibleAt: number | null = null;
  /** When the previous step finished (an "after" trigger), and how many alerts there were as the last action began (fresh). */
  private previousAt = 0;
  private previousAlerts = 0;
  private stopped = false;
  private failure: "error" | null = null;
  private endedAt: number | null = null;

  constructor(scenario: Scenario, fms: ScriptedFms, context: RunContext = { variant: "not recorded", cycle: fms.activeCycle.id }, sim: FlightSimulator | null = null) {
    this.scenario = structuredClone(scenario);
    this.fms = fms;
    this.sim = sim;
    const problems = scenarioProblems(scenario);
    // The surface is declared first; the start state sets up the fresh simulation before the first step; the context
    // is read after both.
    if (!problems.length && this.scenario.surface) fms.declareSurface(this.scenario.surface);
    if (!problems.length && this.scenario.start) {
      const set = START_STATES[this.scenario.start].setUp(fms, sim ?? undefined);
      if ("refused" in set) problems.push(`start state ${this.scenario.start}: ${set.refused}`);
    }
    this.problems = problems;
    this.context = { ...context, cycle: fms.activeCycle.id, data: context.data ?? fms.activeCycle.source, profile: profileSummary(fms.aircraftProfile), surface: `${fms.surface.id} (${fms.surface.basis})` };
    this.start = fms.now.getTime();
    this.results = this.scenario.steps.map(() => ({ status: "pending" }));
    if (this.problems.length) { this.next = this.results.length; this.endedAt = 0; return; }
    this.poll();
  }

  get startedAt() { return new Date(this.start); }
  /** The GPS inputs that, with the start time and the steps, fix the GPS timeline. */
  get gpsSeeds() { return { constellation: this.fms.gps[0].constellationSeed, receivers: this.fms.gps.map(receiver => receiver.seed) }; }
  get elapsed() { return (this.fms.now.getTime() - this.start) / 1000; }
  /** Simulated seconds from the start to the end of the run, once it has ended. */
  get endedAfter() { return this.endedAt; }
  get finished() { return this.next >= this.scenario.steps.length; }
  get current() { return this.finished ? null : this.next; }
  get passed() { return this.outcome === "passed"; }

  get outcome(): RunOutcome {
    if (this.problems.length) return "invalid";
    if (!this.finished) return "running";
    if (this.failure === "error" || this.results.some(result => result.status === "error")) return "error";
    if (this.results.some(result => result.status === "fail")) return "failed";
    if (this.stopped) return "stopped";
    if (this.results.some(result => result.status === "not reached")) return "timed out";
    return this.scenario.steps.some(step => isExpectation(step.action)) ? "passed" : "no checks";
  }

  /** Runs every step whose trigger has come, in order; past maxSeconds, ends the run with the rest not reached. */
  poll() {
    if (this.finished) return;
    const now = this.elapsed;
    if (now > this.scenario.maxSeconds + 1e-9) { this.end("not reached"); return; }
    while (!this.finished) {
      const step = this.scenario.steps[this.next];
      try {
        if (this.eligibleAt === null) {
          if (!this.triggered(step.when)) break;
          this.eligibleAt = now;
        }
        if (isExpectation(step.action)) {
          const waited = now - this.eligibleAt;
          const window = step.within ?? 0;
          const check = this.check(step.action);
          // An observation strictly after the window cannot satisfy it, even when the deadline fell between ticks and
          // this is the first tick since: the condition was not seen in time. At the deadline exactly, it counts.
          const late = waited > window + 1e-9;
          if (check.ok && !late) { this.finish({ status: "pass", at: now, actual: check.actual }); continue; }
          // Not met: wait while the window is open (and the run has time left), otherwise it has failed.
          if (!late && waited < window - 1e-9 && now < this.scenario.maxSeconds - 1e-9) break;
          this.finish({ status: "fail", at: now, actual: check.actual });
        } else {
          // A fresh alert check counts what the last action itself raised, so the baseline is taken just before it.
          this.previousAlerts = this.fms.recallList.length;
          this.act(step.action);
          this.finish({ status: "done", at: now });
        }
      } catch (error) {
        this.results[this.next] = { status: "error", at: now, actual: error instanceof Error ? error.message : String(error) };
        this.next += 1;
        this.failure = "error";
        this.end("not reached");
        return;
      }
    }
    if (this.finished) this.endedAt ??= now;
  }

  /** Stops the run where it is, at the operator's request: steps not yet run are not reached. */
  abandon() {
    if (this.finished) return;
    this.stopped = true;
    this.end("not reached");
  }

  private end(status: "not reached") {
    for (let i = this.next; i < this.results.length; i += 1) this.results[i] = { status };
    this.next = this.results.length;
    this.endedAt ??= this.elapsed;
  }

  private finish(result: StepResult) {
    this.results[this.next] = result;
    this.previousAt = result.at ?? this.elapsed;
    this.next += 1;
    this.eligibleAt = null;
  }

  private triggered(when: Trigger) {
    switch (when.kind) {
      case "fafDistance": return this.fms.distanceToFaf !== null && this.fms.distanceToFaf <= when.nm;
      case "start": return true;
      case "time": return this.elapsed >= when.seconds - 1e-9;
      case "after": return this.elapsed >= this.previousAt + when.seconds - 1e-9;
      case "below": return this.fms.altitude <= when.feet + 1e-9;
      case "above": return Math.round(this.fms.altitude) >= when.feet;
      case "distance": {
        const at = this.fms.coordinates(when.waypoint);
        return at !== undefined && distanceNm(this.fms.truePosition, at) <= when.nm;
      }
      case "active": return this.activeWaypoint() === when.waypoint;
      default: throw new Error(`Unsupported trigger "${(when as { kind: string }).kind}".`);
    }
  }

  private activeWaypoint() {
    const leg = this.fms.activeRoute.legs[0];
    return leg?.kind === "wpt" ? leg.ident : null;
  }

  private act(action: Action) {
    const fms = this.fms;
    switch (action.kind) {
      case "keys": for (const key of action.keys) fms.press(key); return;
      case "type": for (const key of keysFor(action.text)) fms.press(key); return;
      case "condition": fms.setCondition(action.condition, action.on); return;
      case "alert": fms.raiseAlert(action.text); return;
      case "procedure": fms.selectProcedure(action.procedure, action.ident, action.transition); return;
      case "armApproach": fms.armApproach(action.on !== false); return;
      // TOGA: the FMS missed-approach request and, under the helicopter profile, the autopilot's GA.
      // A refused TOGA (no missed approach to go around onto, or the FMS failed) is an error in the run, never a silent pass.
      case "goAround": if (!fms.goAround()) throw new Error("TOGA refused by the FMS: no missed approach ahead, or the FMS has failed"); this.sim?.engageGoAround(); return;
      case "proceedPins": if (!(this.sim ? this.sim.proceedFromPins(action) : fms.proceedFromPins(action))) throw new Error("PinS continuation refused: MAP, chart or crew conditions unavailable"); return;
      case "wind": Object.assign(fms.wind, { direction: action.direction, speed: action.speed }); return;
      case "autopilot": {
        const sim = this.sim;
        if (!sim) throw new Error("autopilot selections need the flight simulation");
        if (action.altitude !== undefined) sim.selectAltitude(action.altitude);
        if (action.verticalSpeed !== undefined && !sim.engageVerticalSpeed(action.verticalSpeed)) throw new Error(`VS is not available in the ${fms.aircraftProfile.id} profile`);
        if (action.hold && !sim.engageAltitudeHold()) throw new Error(`ALT is not available in the ${fms.aircraftProfile.id} profile`);
        if (action.speed !== undefined) sim.selectSpeed(action.speed);
        if (action.heading !== undefined) sim.selectHeading(action.heading);
        if (action.lnav) sim.armLnav();
        if (action.hover && !sim.engageHover()) throw new Error("HOV is not available: above the coordinated-flight speed, or no eligible hover feedback");
        if (action.transitionUp && !sim.engageTransitionUp()) throw new Error("TU is not available: not in a hover mode, too fast, or no valid radio height");
        return;
      }
      case "gps": {
        // Through the same stimulus record as the GPS sensors tab, so the tab shows what the scenario applied.
        if (!stimulusFor(fms).apply(action.receiver - 1, action.stimulus)) throw new Error(`GPS ${action.receiver} refused: ${describeGpsOp(action.stimulus)}.`);
        return;
      }
      default: throw new Error(`Unsupported action "${action.kind}".`);
    }
  }

  private check(action: Action): { ok: boolean; actual: string } {
    const fms = this.fms;
    const lines = screenText(fms.screen());
    switch (action.kind) {
      case "expectLine": {
        const line = lines[action.line] ?? "";
        return { ok: new RegExp(action.pattern).test(line), actual: line.trimEnd() };
      }
      case "expectScratchpad": {
        const shown = lines[SCRATCHPAD_LINE].trim();
        return { ok: shown === action.text, actual: shown };
      }
      case "expectAlert": {
        // The recall list is newest first: with fresh, only the alerts raised since the step before this one count.
        const list = action.fresh ? fms.recallList.slice(0, Math.max(0, fms.recallList.length - this.previousAlerts)) : fms.recallList;
        const raised = list.some(message => message.text === action.text);
        return { ok: raised, actual: raised ? action.text : list.map(message => message.text).join(", ") || (action.fresh ? "no new alerts" : "no alerts") };
      }
      case "expectHover": {
        const hover = fms.hover;
        const ok = (action.refused === undefined || hover.refused === action.refused) && (action.reason === undefined || hover.refusedReason === action.reason);
        return { ok, actual: hover.refused ? `${hover.refused} (${hover.refusedReason ?? "no reason"})` : "not refused" };
      }
      case "expectNoAlert": {
        const raised = fms.recallList.some(message => message.text === action.text);
        return { ok: !raised, actual: raised ? action.text : "not raised" };
      }
      case "expectLamp": {
        const lit = fms.lamps().has(action.lamp);
        return { ok: lit === action.lit, actual: lit ? "lit" : "out" };
      }
      case "expectActive": {
        const active = this.activeWaypoint() ?? "none";
        return { ok: active === action.waypoint, actual: active };
      }
      case "expectApproach": {
        const sim = this.sim;
        if (!sim) throw new Error("expectApproach needs the flight simulation, which this run was not given.");
        const type = fms.approachType ?? "none", state = sim.approachMode, vertical = sim.verticalMode;
        const path = sim.verticalPath;
        const deviation = path ? fms.altitude - path.altitude : null;
        const ok = (action.type === undefined || type === action.type) && (action.state === undefined || state === action.state)
          && (action.verticalMode === undefined || vertical === action.verticalMode)
          && (action.maxVerticalFt === undefined || (deviation !== null && Math.abs(deviation) <= action.maxVerticalFt));
        return { ok, actual: `${type} ${state}, ${vertical}, ${deviation === null ? "no path" : `${Math.round(deviation)} ft from the path`}` };
      }
      case "expectAfcs": {
        const sim = this.sim;
        if (!sim) throw new Error("expectAfcs needs the flight simulation, which this run was not given.");
        const modes = sim.axisModes, caption = sim.lowHeightCaption ?? "NONE";
        const ok = (action.collective === undefined || modes.collective === action.collective) && (action.pitch === undefined || modes.pitch === action.pitch)
          && (action.roll === undefined || modes.roll === action.roll) && (action.lowHeight === undefined || caption === action.lowHeight);
        return { ok, actual: `${modes.collective} | ${modes.pitch} | ${modes.roll}${action.lowHeight === undefined ? "" : `, low height ${caption}`}` };
      }
      case "expectAircraft": {
        const ra = fms.radioHeight;
        const at = action.near ? fms.coordinates(action.near) : undefined;
        const metres = at ? distanceNm(fms.truePosition, at) * 1852 : null;
        const trackOff = action.track === undefined ? 0 : Math.abs(((fms.track - action.track + 540) % 360) - 180);
        const ok = (action.minGroundSpeed === undefined || fms.groundSpeed >= action.minGroundSpeed)
          && (action.maxGroundSpeed === undefined || fms.groundSpeed <= action.maxGroundSpeed)
          && (action.track === undefined || trackOff <= (action.trackTolerance ?? 5))
          && (action.radioHeight === undefined || (ra.status === "NORMAL" && Math.abs(ra.value! - action.radioHeight) <= (action.heightTolerance ?? 10)))
          && (action.altitude === undefined || Math.abs(fms.altitude - action.altitude) <= (action.heightTolerance ?? 10))
          && (action.minAltitude === undefined || fms.altitude >= action.minAltitude)
          && (action.maxAltitude === undefined || fms.altitude <= action.maxAltitude)
          && (action.near === undefined || (metres !== null && metres <= (action.nearMetres ?? 50) && metres >= (action.minNearMetres ?? 0)))
          && (action.maxCrossTrack === undefined || Math.abs(fms.crossTrack) <= action.maxCrossTrack);
        const parts = [
          `GS ${fms.groundSpeed.toFixed(1)} kt`, `TRK ${Math.round(fms.track)}°T`, `RA ${ra.status === "NORMAL" ? `${Math.round(ra.value!)} ft` : ra.status}`, `ALT ${Math.round(fms.altitude)} ft`,
          ...(action.near ? [metres === null ? `${action.near} unknown` : `${Math.round(metres)} m from ${action.near}`] : []),
          ...(action.maxCrossTrack !== undefined ? [`XTK ${fms.crossTrack.toFixed(2)} NM`] : []),
        ];
        return { ok, actual: parts.join(", ") };
      }
      case "expectGpsSource": {
        const nav = fms.navState;
        const source = nav.gpsSource !== null ? `GPS${nav.gpsSource}` : "NONE";
        return { ok: source === action.source, actual: nav.gpsSource !== null ? source : `NONE (${nav.mode})` };
      }
      case "expectApproachLevel": {
        const level = fms.approachType;
        return { ok: level === action.level, actual: level ?? "none annunciated" };
      }
      case "expectReceiverMode": {
        const mode = fms.gps[action.receiver - 1].mode;
        return { ok: mode === action.mode, actual: mode };
      }
      default: throw new Error(`Unsupported check "${action.kind}".`);
    }
  }
}

/**
 * Advances a simulation by whole ticks: each moves the clock, integrates the flight and then lets the runner observe.
 * The bench and runHeadless both use it, so a scenario sees the same timeline at any rate.
 */
export function advanceTicks(ticks: number, moveClock: (ms: number) => void, sim: FlightSimulator, runner: ScenarioRunner | null) {
  const running = runner !== null && !runner.finished;
  for (let i = 0; i < ticks; i += 1) {
    moveClock(TICK_SECONDS * 1000);
    sim.step(TICK_SECONDS);
    runner?.poll();
    // A run's timeline ends on the tick where it finishes, however many ticks the caller asked for.
    if (running && runner.finished) return;
  }
}

/** The scenario's planned start (epoch ms), when it names one. */
export const scenarioStart = (scenario: Scenario) => (scenario.startTime && Number.isFinite(Date.parse(scenario.startTime)) ? Date.parse(scenario.startTime) : null);

/** Runs a scenario to its end on a fresh simulation, tick by tick, as the tests do. */
export function runHeadless(scenario: Scenario, start = scenarioStart(scenario) ?? Date.UTC(2026, 8, 27, 14, 0, 0), context?: RunContext) {
  let now = start;
  const fms = new ScriptedFms(() => new Date(now), { profile: profileById(scenario.profile) });
  const sim = new FlightSimulator(fms);
  const runner = new ScenarioRunner(scenario, fms, context, sim);
  // The runner ends itself at maxSeconds; the bound only keeps a broken runner from looping forever.
  const limit = Math.ceil((Number.isFinite(scenario.maxSeconds) ? scenario.maxSeconds : 0) / TICK_SECONDS) + 2;
  for (let t = 0; !runner.finished && t < limit; t += 1) advanceTicks(1, ms => { now += ms; }, sim, runner);
  return { runner, fms, sim };
}

// ------------------------------------------------------------------ outputs

/** The scenario as test procedure text, in the fields of an AeroLink test procedure proposal. */
export function procedureText(scenario: Scenario) {
  const checks = scenario.steps.filter(step => isExpectation(step.action));
  return {
    title: scenario.title,
    objective: scenario.objective,
    preconditions: [
      "The AeroLink FMS Test Bench is open with the scripted CMA-9000 simulation (not a navigation computer).",
      "The simulation is restarted, with the demonstration route and navigation database loaded.",
      ...(scenario.start ? [`It is then set up in the start state ${START_STATES[scenario.start].label}.`] : []),
      `The flight is flown at any rate; the scenario allows ${formatSeconds(scenario.maxSeconds)} of simulated time.`,
      ...(scenario.startTime ? [`The simulated clock starts at ${scenario.startTime}, which fixes the GPS sky the receivers see.`] : []),
      ...(usesGps(scenario) ? [`The GPS model is ${GPS_MODEL_VERSION}, with the bench's constellation seed and receiver seeds (named in the run report).`] : []),
    ].join("\n"),
    steps: scenario.steps.map((step, i) => `${i + 1}. ${describeStep(step, i)}`).join("\n"),
    // Only the checks the scenario actually makes; a scenario without checks verifies nothing and says so.
    expectedResult: checks.length
      ? checks.map(step => `- ${describeStep(step).replace(/^.*?check that /, "").replace(/\.$/, "")}.`).join("\n")
      : "None: this scenario has no checks. It plays back its actions and verifies no outcome.",
  };
}

const usesGps = (scenario: Scenario) => scenario.steps.some(step => step.action.kind === "gps");

const OUTCOME_TEXT: Record<RunOutcome, string> = {
  running: "RUNNING",
  passed: "PASS",
  failed: "FAIL",
  "no checks": "NO CHECKS (actions played back; nothing verified)",
  "timed out": "TIMED OUT (steps not reached by the time limit)",
  stopped: "STOPPED BY THE OPERATOR",
  invalid: "INVALID SCENARIO (not run)",
  error: "EXECUTION ERROR",
};

/** A short, stable fingerprint of the scenario as run (FNV-1a over its JSON), so a report names what it ran. */
export function scenarioDigest(scenario: Scenario) {
  let hash = 0x811c9dc5;
  for (const ch of JSON.stringify(scenario)) {
    hash ^= ch.codePointAt(0)!;
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return `fnv1a-${hash.toString(16).padStart(8, "0")}`;
}

/** A run report in Markdown, from the run as it was: its scenario, context and results are fixed when it starts. */
export function reportMarkdown(runner: ScenarioRunner) {
  const { scenario, results, context } = runner;
  const rows = scenario.steps.map((step, i) => {
    const result = results[i];
    const at = result.at === undefined ? "—" : formatSeconds(result.at);
    const actual = result.actual ? result.actual.replace(/\|/g, "\\|") : "";
    return `| ${i + 1} | ${describeStep(step, i).replace(/\|/g, "\\|")} | ${at} | ${result.status.toUpperCase()} | ${actual} |`;
  });
  return [
    `# FMS Test Bench run: ${scenario.title}`,
    "",
    `**Result: ${OUTCOME_TEXT[runner.outcome]}**`,
    "",
    `- Objective: ${scenario.objective}`,
    `- Scenario: ${scenario.id}, ${scenarioDigest(scenario)}`,
    `- Started: ${runner.startedAt.toISOString()}${runner.endedAfter === null ? "" : `; ended after ${formatSeconds(runner.endedAfter)} of simulated time`}`,
    `- Hardware variation: ${context.variant}`,
    `- Aircraft profile: ${context.profile ?? "not recorded"}`,
    `- Surface for the radio altimeter: ${context.surface ?? "not recorded"}`,
    `- Navigation data: ${context.cycle} (${!context.data || context.data === "demonstration data" ? "invented demonstration data" : context.data})`,
    `- Time: ${TICK_SECONDS} s ticks; a step due between ticks runs at the next one.`,
    "- Driven by the scripted CMA-9000 simulation, not the operational program. This is not flight-qualified evidence.",
    `- GPS: model ${GPS_MODEL_VERSION}; constellation seed ${runner.gpsSeeds.constellation}; receiver seeds ${runner.gpsSeeds.receivers.join(" and ")}; the sky is the one at the start time above. With the steps, these fix the GPS timeline.`,
    ...(runner.problems.length ? ["", "Not run, because:", ...runner.problems.map(problem => `- ${problem}`)] : []),
    "",
    "| # | Step | At | Result | Actual |",
    "|---|---|---|---|---|",
    ...rows,
    ...gpsRows(runner),
    "",
  ].join("\n");
}

/** The GPS stimuli the run applied, one row each (a clear is a row too), with its receiver, time and values. */
function gpsRows(runner: ScenarioRunner) {
  const cell = (value: string) => value.replace(/\|/g, "\\|");
  const rows = runner.scenario.steps.flatMap((step, i) => {
    const action = step.action, result = runner.results[i];
    if (action.kind !== "gps") return [];
    const { op, ...fields } = action.stimulus;
    const values = Object.entries(fields).map(([key, value]) => `${key} ${Array.isArray(value) ? `[${value.join(", ")}]` : typeof value === "object" ? JSON.stringify(value) : String(value)}`).join("; ") || "—";
    const at = result.at === undefined ? "not applied" : formatSeconds(result.at);
    return [`| ${i + 1} | GPS ${action.receiver} | ${at} | ${op} | ${cell(values)} | ${cell(describeGpsOp(action.stimulus))} |`];
  });
  return rows.length ? ["", "GPS stimuli:", "", "| Step | Receiver | At | Operation | Fields | In words |", "|---|---|---|---|---|---|", ...rows] : [];
}

// ------------------------------------------------------------------ recording and import

/**
 * Records what an engineer does on the bench as scenario steps, timed from the start of the recording on the tick
 * grid, so the same sequence can be played back and checked.
 */
export class ScenarioRecorder {
  readonly steps: ScenarioStep[] = [];
  private readonly start: number;
  private readonly clock: () => Date;
  private lastKeyAt: number | null = null;

  constructor(clock: () => Date) {
    this.clock = clock;
    this.start = clock().getTime();
  }

  /** Seconds since the start, rounded up to the next tick: the time at which playback will run the step. */
  private get seconds() { return Math.ceil((this.clock().getTime() - this.start) / (TICK_SECONDS * 1000) - 1e-9) * TICK_SECONDS; }

  private add(action: Action) {
    const seconds = this.seconds;
    const last = this.steps.at(-1);
    // Keys pressed less than a second apart are one step, as a crew member types an entry.
    if (action.kind === "keys" && last?.action.kind === "keys" && this.lastKeyAt !== null && seconds - this.lastKeyAt < 1) {
      last.action.keys.push(...action.keys);
    } else {
      this.steps.push({ when: seconds === 0 ? { kind: "start" } : { kind: "time", seconds }, action });
    }
    this.lastKeyAt = action.kind === "keys" ? seconds : null;
  }

  key(fn: CduFunction) { this.add({ kind: "keys", keys: [fn] }); }
  condition(condition: ConditionId, on: boolean) { this.add({ kind: "condition", condition, on }); }
  alert(text: string) { this.add({ kind: "alert", text }); }
  armApproach(on = true) { this.add(on ? { kind: "armApproach" } : { kind: "armApproach", on: false }); }
  goAround() { this.add({ kind: "goAround" }); }
  proceedPins(declaration: { basicVfr: boolean; landingAreaVisible: boolean; publishedVisibility: boolean }) { this.add({ kind: "proceedPins", ...declaration }); }
  /** An autopilot selection made on the bench (helicopter profile). */
  autopilot(selection: { altitude?: number; verticalSpeed?: number; hold?: boolean; speed?: number }) { this.add({ kind: "autopilot", ...selection }); }
  /** A stimulus applied on the GPS sensors tab. */
  gps(receiver: 1 | 2, stimulus: GpsOp) { this.add({ kind: "gps", receiver, stimulus: structuredClone(stimulus) }); }
  /** Checks a screen line as it is shown now; a few seconds' grace lets playback at another rate catch up. */
  checkLine(line: number, text: string) { this.add({ kind: "expectLine", line, pattern: linePattern(text) }); this.steps.at(-1)!.within = 5; }

  toScenario(title: string): Scenario {
    const last = this.steps.at(-1)?.when;
    const end = last?.kind === "time" ? last.seconds : 0;
    return {
      id: `recorded-${this.start}`,
      title,
      objective: "Replay a sequence recorded on the FMS Test Bench and check the screen lines captured during it.",
      maxSeconds: Math.max(60, Math.ceil(end + 30)),
      // The GPS sky moves with the clock: replay starts where the recording did.
      startTime: new Date(this.start).toISOString(),
      steps: structuredClone(this.steps),
    };
  }
}

/** Reads a scenario from JSON, refusing anything the runner does not support, with every reason. */
export function parseScenario(json: string): Scenario {
  const value = JSON.parse(json) as Partial<Scenario>;
  const problems = scenarioProblems(value);
  if (problems.length) throw new Error(`Not a runnable FMS Test Bench scenario: ${problems.join("; ")}.`);
  return { ...value, id: value.id ?? `imported-${value.title}`, objective: value.objective ?? "" } as Scenario;
}
