import { useState } from "react";
import { describeStep, parseScenario, procedureText, reportMarkdown, type RunOutcome, type Scenario, type ScenarioRunner } from "./scenario";
import { SCENARIO_LIBRARY } from "./scenarioLibrary";

const STATUS_LABEL = { pending: "Pending", done: "Done", pass: "Pass", fail: "Fail", "not reached": "Not reached", error: "Error" } as const;

/** The run's outcome in words: only a run whose checks all held is a pass. */
const OUTCOME_LABEL: Record<RunOutcome, string> = {
  running: "RUNNING",
  passed: "PASS",
  failed: "FAIL",
  "no checks": "NO CHECKS: actions played back, nothing verified",
  "timed out": "TIMED OUT: steps not reached by the time limit",
  stopped: "STOPPED by the operator",
  invalid: "INVALID SCENARIO: not run",
  error: "EXECUTION ERROR",
};

/** Offers text as a file the user saves; nothing leaves the browser. */
const download = (name: string, text: string, type: string) => {
  const url = URL.createObjectURL(new Blob([text], { type }));
  const link = document.createElement("a");
  link.href = url;
  link.download = name;
  link.click();
  URL.revokeObjectURL(url);
};

const fileName = (title: string) => title.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "") || "scenario";

/**
 * The bench's scenario card: runs a built-in, recorded or loaded scenario on a restarted simulation with its steps
 * checked live, records one from what the engineer does, and writes a scenario out as test procedure text and a run
 * as a report. The bench owns the simulation; this card asks it to run or record.
 */
export default function FmsScenarioCard({
  runner, recording, screenLines, onRun, onStop, onRecord, onFinishRecording, onCheckLine,
}: {
  runner: ScenarioRunner | null;
  recording: boolean;
  screenLines: readonly string[];
  onRun: (scenario: Scenario) => void;
  onStop: () => void;
  onRecord: () => void;
  onFinishRecording: (title: string) => Scenario | null;
  onCheckLine: (line: number) => void;
}) {
  const [own, setOwn] = useState<Scenario[]>([]);
  const [selectedId, setSelectedId] = useState(SCENARIO_LIBRARY[0].id);
  const [checkLine, setCheckLine] = useState(0);
  const [title, setTitle] = useState("Recorded scenario");
  const [notice, setNotice] = useState<string | null>(null);
  const [showProcedure, setShowProcedure] = useState(false);
  const scenarios = [...SCENARIO_LIBRARY, ...own];
  const selected = scenarios.find(scenario => scenario.id === selectedId) ?? SCENARIO_LIBRARY[0];
  const running = runner !== null && !runner.finished;
  const shown = runner?.scenario ?? selected;
  const procedure = procedureText(selected);
  const procedureBlock = `Title: ${procedure.title}\n\nObjective: ${procedure.objective}\n\nPreconditions:\n${procedure.preconditions}\n\nSteps:\n${procedure.steps}\n\nExpected result:\n${procedure.expectedResult}\n`;

  const adopt = (scenario: Scenario) => {
    setOwn(list => [...list.filter(entry => entry.id !== scenario.id), scenario]);
    setSelectedId(scenario.id);
  };

  return (
    <section className="fmsBenchCard fmsScenario" aria-label="Scenarios">
      <h2>Scenarios</h2>
      <label className="fmsScenarioPick">
        <span>Scenario</span>
        <select value={selected.id} disabled={running || recording} aria-label="Scenario" onChange={event => { setSelectedId(event.target.value); setShowProcedure(false); }}>
          {scenarios.map(scenario => <option key={scenario.id} value={scenario.id}>{scenario.title}</option>)}
        </select>
      </label>
      <p className="fmsBenchHint">{selected.objective}</p>
      <div className="fmsBenchActions">
        {running
          ? <button type="button" onClick={onStop}>Stop the run</button>
          : <button type="button" disabled={recording} onClick={() => onRun(selected)}>Run the scenario</button>}
        <button type="button" disabled={running} aria-pressed={recording}
          onClick={() => {
            if (!recording) { onRecord(); setNotice("Recording from a restarted simulation: keys, conditions, alerts, APPR and TOGA."); return; }
            const scenario = onFinishRecording(title.trim() || "Recorded scenario");
            if (scenario) { adopt(scenario); setNotice(`Recorded ${scenario.steps.length} steps as “${scenario.title}”.`); }
          }}>
          {recording ? "Stop recording" : "Record"}
        </button>
      </div>

      {recording ? (
        <div className="fmsScenarioRecord">
          <label>
            <span>Name</span>
            <input value={title} maxLength={80} aria-label="Recording name" onChange={event => setTitle(event.target.value)} />
          </label>
          <label>
            <span>Screen line</span>
            <select value={checkLine} aria-label="Screen line to check" onChange={event => setCheckLine(Number(event.target.value))}>
              {screenLines.map((text, line) => <option key={line} value={line}>{`${line + 1}: ${text.trim() || "(blank)"}`}</option>)}
            </select>
          </label>
          <button type="button" onClick={() => { onCheckLine(checkLine); setNotice(`Check added: line ${checkLine + 1} as shown now.`); }}>Add screen check</button>
        </div>
      ) : null}
      {notice ? <p className="fmsBenchHint" role="status">{notice}</p> : null}

      <ol className="fmsScenarioSteps" aria-label="Scenario steps">
        {shown.steps.map((step, i) => {
          // While there is a run, the steps shown are its own copy of the scenario, with its results.
          const result = runner?.results[i];
          const status = result?.status ?? "pending";
          return (
            <li key={i} data-status={status} aria-current={runner?.current === i ? "step" : undefined}>
              <span>{describeStep(step, i)}</span>
              <b>{STATUS_LABEL[status]}{result?.at !== undefined ? ` · ${Math.round(result.at)} s` : ""}</b>
              {status === "fail" && result?.actual !== undefined ? <small>Shown: {result.actual || "(blank)"}</small> : null}
            </li>
          );
        })}
      </ol>
      {runner?.finished ? (
        <p className={`fmsScenarioResult ${runner.passed ? "pass" : "fail"}`} role="status">
          {OUTCOME_LABEL[runner.outcome]}: {runner.results.filter(result => result.status === "pass").length} checks passed,{" "}
          {runner.results.filter(result => result.status === "fail" || result.status === "not reached" || result.status === "error").length} failed, not reached or in error.
        </p>
      ) : null}

      <div className="fmsBenchActions">
        <button type="button" disabled={!runner?.finished}
          onClick={() => runner && download(`${fileName(runner.scenario.title)}-run.md`, reportMarkdown(runner), "text/markdown")}>
          Download run report
        </button>
        <button type="button" aria-expanded={showProcedure} onClick={() => setShowProcedure(value => !value)}>Test procedure text</button>
        <button type="button" onClick={() => download(`${fileName(selected.title)}.json`, JSON.stringify(selected, null, 2), "application/json")}>Save as JSON</button>
      </div>
      <label className="fmsBenchFile">
        <span>Load a scenario (JSON)</span>
        <input type="file" accept=".json,application/json" aria-label="Scenario file" disabled={running || recording}
          onChange={async event => {
            const file = event.target.files?.[0];
            if (!file) return;
            try {
              adopt(parseScenario(await file.text()));
              setNotice(`Loaded “${file.name}”.`);
            } catch (error) {
              setNotice(`${file.name} was not loaded: ${error instanceof Error ? error.message : String(error)}`);
            }
            event.target.value = "";
          }} />
      </label>
      {showProcedure ? (
        <div className="fmsScenarioProcedure">
          <p className="fmsBenchHint">
            The fields of an AeroLink test procedure proposal. Copy them into a procedure change; the bench does not
            change controlled procedures itself.
          </p>
          <textarea readOnly value={procedureBlock} aria-label="Test procedure text" rows={12} />
          <button type="button"
            onClick={async () => {
              try { await navigator.clipboard.writeText(procedureBlock); setNotice("Test procedure text copied."); }
              catch { setNotice("Copying was blocked; select the text and copy it."); }
            }}>
            Copy
          </button>
        </div>
      ) : null}
    </section>
  );
}
