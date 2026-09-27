import type { ConditionId } from "./conditions";
import { FlightSimulator } from "./flight";
import { distanceNm } from "./fmsModel";
import { ScriptedFms } from "./scriptedFms";
import { SCRATCHPAD_LINE, screenText, type Lamp } from "./screen";
import type { CduFunction } from "./variants";

// Scripted test scenarios for the FMS Test Bench (product/docs/FMS_TEST_BENCH.md, step 8). A scenario is an ordered
// list of steps; each waits for its trigger, then acts on the simulation or checks what the crew would see. Steps
// run one after another, so a scenario reads as a test procedure does. Scenarios are plain data: they can be
// recorded on the bench, saved as JSON, played back, and written out as test procedure text.

/** When a step runs. Times are simulated seconds from the start of the run. */
export type Trigger =
  | { kind: "start" }
  | { kind: "time"; seconds: number }
  | { kind: "distance"; waypoint: string; nm: number }
  | { kind: "active"; waypoint: string };

export type Action =
  | { kind: "keys"; keys: CduFunction[] }
  | { kind: "type"; text: string }
  | { kind: "condition"; condition: ConditionId; on: boolean }
  | { kind: "alert"; text: string }
  | { kind: "procedure"; procedure: "SID" | "STAR" | "APPROACH"; ident: string }
  | { kind: "armApproach" }
  | { kind: "goAround" }
  | { kind: "expectLine"; line: number; pattern: string }
  | { kind: "expectScratchpad"; text: string }
  | { kind: "expectAlert"; text: string }
  | { kind: "expectNoAlert"; text: string }
  | { kind: "expectLamp"; lamp: Lamp; lit: boolean }
  | { kind: "expectActive"; waypoint: string };

/** One step. An expectation not yet met waits up to `within` seconds for it before failing. */
export type ScenarioStep = { when: Trigger; action: Action; within?: number };

export type Scenario = {
  id: string;
  title: string;
  objective: string;
  /** The run fails any step not reached by then. */
  maxSeconds: number;
  steps: ScenarioStep[];
};

export type StepStatus = "pending" | "done" | "pass" | "fail" | "not reached";
export type StepResult = { status: StepStatus; at?: number; actual?: string };

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
  const when = w.kind === "start" ? (index > 0 ? "Then" : "At the start") : w.kind === "time" ? `At ${formatSeconds(w.seconds)}` : w.kind === "distance" ? `Within ${w.nm} NM of ${w.waypoint}` : `When ${w.waypoint} is the active waypoint`;
  const a = step.action;
  const within = step.within ? ` within ${step.within} s` : "";
  const what = (() => {
    switch (a.kind) {
      case "keys": return `press ${a.keys.map(key => key.replace(/^CHAR_/, "")).join(" ")}`;
      case "type": return `type ${a.text} into the scratchpad`;
      case "condition": return `${a.on ? "inject" : "remove"} the condition ${a.condition}`;
      case "alert": return `raise the alert ${a.text}`;
      case "procedure": return `select the ${a.procedure === "APPROACH" ? "approach" : a.procedure} ${a.ident}`;
      case "armApproach": return "arm the approach";
      case "goAround": return "press TOGA";
      case "expectLine": return `check that screen line ${a.line + 1} matches /${a.pattern}/${within}`;
      case "expectScratchpad": return `check that the scratchpad shows ${a.text}${within}`;
      case "expectAlert": return `check that the alert ${a.text} has been raised${within}`;
      case "expectNoAlert": return `check that the alert ${a.text} has not been raised`;
      case "expectLamp": return `check that the ${a.lamp} annunciator is ${a.lit ? "lit" : "out"}${within}`;
      case "expectActive": return `check that ${a.waypoint} is the active waypoint${within}`;
    }
  })();
  return when === "Then" ? `Then ${what}.` : `${when}, ${what}.`;
}

const formatSeconds = (seconds: number) => {
  const m = Math.floor(seconds / 60);
  const s = Math.round(seconds % 60);
  return m ? `${m} min${s ? ` ${s} s` : ""}` : `${s} s`;
};

/**
 * Runs a scenario against a simulation. It does not fly the aircraft or move the clock: whoever owns them (the bench,
 * or runHeadless) steps the flight and then calls poll().
 */
export class ScenarioRunner {
  readonly results: StepResult[];
  private readonly start: number;
  private next = 0;
  /** When the current step's trigger came, for an expectation that is waiting. */
  private eligibleAt: number | null = null;
  readonly scenario: Scenario;
  private readonly fms: ScriptedFms;

  constructor(scenario: Scenario, fms: ScriptedFms) {
    this.scenario = scenario;
    this.fms = fms;
    this.start = fms.now.getTime();
    this.results = scenario.steps.map(() => ({ status: "pending" }));
    this.poll();
  }

  get startedAt() { return new Date(this.start); }
  get elapsed() { return (this.fms.now.getTime() - this.start) / 1000; }
  get finished() { return this.next >= this.scenario.steps.length; }
  get passed() { return this.finished && this.results.every(result => result.status === "done" || result.status === "pass"); }
  get current() { return this.finished ? null : this.next; }

  /** Runs every step whose trigger has come, in order, and times the run out at maxSeconds. */
  poll() {
    while (!this.finished) {
      const step = this.scenario.steps[this.next];
      const now = this.elapsed;
      if (this.eligibleAt === null) {
        if (!this.triggered(step.when)) break;
        this.eligibleAt = now;
      }
      if (isExpectation(step.action)) {
        const check = this.check(step.action);
        if (!check.ok && now - this.eligibleAt < (step.within ?? 0) && now < this.scenario.maxSeconds) break;
        this.finish({ status: check.ok ? "pass" : "fail", at: now, actual: check.actual });
      } else {
        this.act(step.action);
        this.finish({ status: "done", at: now });
      }
    }
    if (!this.finished && this.elapsed >= this.scenario.maxSeconds) {
      for (let i = this.next; i < this.results.length; i += 1) this.results[i] = { status: "not reached" };
      this.next = this.results.length;
    }
  }

  /** Stops the run where it is: steps not yet run are not reached. */
  abandon() {
    for (let i = this.next; i < this.results.length; i += 1) this.results[i] = { status: "not reached" };
    this.next = this.results.length;
  }

  private finish(result: StepResult) {
    this.results[this.next] = result;
    this.next += 1;
    this.eligibleAt = null;
  }

  private triggered(when: Trigger) {
    switch (when.kind) {
      case "start": return true;
      case "time": return this.elapsed >= when.seconds;
      case "distance": {
        const at = this.fms.coordinates(when.waypoint);
        return at !== undefined && distanceNm(this.fms.truePosition, at) <= when.nm;
      }
      case "active": return this.activeWaypoint() === when.waypoint;
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
      case "procedure": fms.selectProcedure(action.procedure, action.ident); return;
      case "armApproach": fms.armApproach(true); return;
      case "goAround": fms.goAround(); return;
      default: return;
    }
  }

  private check(action: Action): { ok: boolean; actual: string } {
    const fms = this.fms;
    const lines = screenText(fms.screen());
    switch (action.kind) {
      case "expectLine": {
        const text = lines[action.line] ?? "";
        return { ok: new RegExp(action.pattern).test(text), actual: text.trimEnd() };
      }
      case "expectScratchpad": {
        const text = lines[SCRATCHPAD_LINE].trim();
        return { ok: text === action.text, actual: text };
      }
      case "expectAlert": {
        const raised = fms.recallList.some(message => message.text === action.text);
        return { ok: raised, actual: raised ? action.text : fms.recallList.map(message => message.text).join(", ") || "no alerts" };
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
      default: return { ok: true, actual: "" };
    }
  }
}

/** Runs a scenario to its end on a fresh simulation, one simulated second at a time, as the tests do. */
export function runHeadless(scenario: Scenario, start = Date.UTC(2026, 8, 27, 14, 0, 0)) {
  let now = start;
  const fms = new ScriptedFms(() => new Date(now));
  const sim = new FlightSimulator(fms);
  const runner = new ScenarioRunner(scenario, fms);
  // The runner times itself out at maxSeconds; the bound only keeps a broken runner from looping forever.
  for (let t = 0; !runner.finished && t <= scenario.maxSeconds; t += 1) {
    now += 1000;
    sim.step(1);
    runner.poll();
  }
  return { runner, fms, sim };
}

/** The scenario as test procedure text, in the fields of an AeroLink test procedure proposal. */
export function procedureText(scenario: Scenario) {
  const actions = scenario.steps.filter(step => !isExpectation(step.action));
  const checks = scenario.steps.filter(step => isExpectation(step.action));
  return {
    title: scenario.title,
    objective: scenario.objective,
    preconditions: [
      "The AeroLink FMS Test Bench is open with the scripted CMA-9000 simulation (not a navigation computer).",
      "The simulation is restarted, with the demonstration route and navigation database loaded.",
      `The flight is flown at any rate; the scenario allows ${formatSeconds(scenario.maxSeconds)} of simulated time.`,
    ].join("\n"),
    steps: scenario.steps.map((step, i) => `${i + 1}. ${describeStep(step, i)}`).join("\n"),
    expectedResult: checks.length
      ? checks.map(step => `- ${describeStep(step).replace(/^.*?check that /, "").replace(/\.$/, "")}.`).join("\n")
      : `The ${actions.length} actions complete without an alert that the scenario does not expect.`,
  };
}

/** A run report in Markdown: the evidence a tester attaches to a test procedure execution. */
export function reportMarkdown(scenario: Scenario, results: readonly StepResult[], context: { startedAt: Date; cycle: string; variant: string }) {
  const passed = results.every(result => result.status === "done" || result.status === "pass");
  const rows = scenario.steps.map((step, i) => {
    const result = results[i];
    const at = result.at === undefined ? "—" : formatSeconds(result.at);
    const actual = result.actual ? result.actual.replace(/\|/g, "\\|") : "";
    return `| ${i + 1} | ${describeStep(step, i).replace(/\|/g, "\\|")} | ${at} | ${result.status.toUpperCase()} | ${actual} |`;
  });
  return [
    `# FMS Test Bench run: ${scenario.title}`,
    "",
    `**Result: ${passed ? "PASS" : "FAIL"}**`,
    "",
    `- Objective: ${scenario.objective}`,
    `- Started: ${context.startedAt.toISOString()}`,
    `- Hardware variation: ${context.variant}`,
    `- Navigation data: ${context.cycle} (invented demonstration data)`,
    "- Driven by the scripted CMA-9000 simulation, not the operational program. This is not flight-qualified evidence.",
    "",
    "| # | Step | At | Result | Actual |",
    "|---|---|---|---|---|",
    ...rows,
    "",
  ].join("\n");
}

/**
 * Records what an engineer does on the bench as scenario steps, timed from the start of the recording, so the same
 * sequence can be played back and checked.
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

  private get seconds() { return Math.round((this.clock().getTime() - this.start) / 100) / 10; }

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
  armApproach() { this.add({ kind: "armApproach" }); }
  goAround() { this.add({ kind: "goAround" }); }
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
      steps: structuredClone(this.steps),
    };
  }
}

/** Reads a scenario from JSON, refusing anything that is not one. */
export function parseScenario(json: string): Scenario {
  const value = JSON.parse(json) as Partial<Scenario>;
  if (typeof value?.title !== "string" || typeof value.maxSeconds !== "number" || !Array.isArray(value.steps))
    throw new Error("Not an FMS Test Bench scenario: it needs a title, maxSeconds and steps.");
  for (const step of value.steps) {
    if (!step || typeof step !== "object" || !step.when?.kind || !step.action?.kind) throw new Error("Every step needs a trigger (when) and an action.");
  }
  return { id: value.id ?? `imported-${value.title}`, objective: value.objective ?? "", ...value } as Scenario;
}
