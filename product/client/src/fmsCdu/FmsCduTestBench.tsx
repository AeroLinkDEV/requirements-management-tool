import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import { ALERTS } from "./alerts";
import { CONDITIONS } from "./conditions";
import { FlightSimulator, MAP_RANGES } from "./flight";
import FmsCduPanel from "./FmsCduPanel";
import FmsEfis from "./FmsEfis";
import FmsMap from "./FmsMap";
import FmsScenarioCard from "./FmsScenarioCard";
import { conditionalLabel } from "./fmsModel";
import { useCduLayout, type CduKeyEvent } from "./layout";
import { LIGHTING_MODES, displayLuminance, type Lighting, type LightingMode } from "./lighting";
import { ScenarioRecorder, ScenarioRunner, TICK_SECONDS, advanceTicks, type Scenario } from "./scenario";
import { ScriptedFms } from "./scriptedFms";
import { screenText } from "./screen";
import { CDU_VARIANTS, DEFAULT_VARIANT_ID, variantById } from "./variants";
import "./FmsCduTestBench.css";

const VARIANT_KEY = "aerolink.fmsCdu.variant";

const storedVariant = () => {
  try { return window.localStorage.getItem(VARIANT_KEY) ?? DEFAULT_VARIANT_ID; } catch { return DEFAULT_VARIANT_ID; }
};

type LogEntry = CduKeyEvent & { title: string };

const formatLuminance = (fl: number) => (fl < 10 ? fl.toFixed(1) : String(Math.round(fl)));

/**
 * An interactive CMA-9000 control display unit for engineers to exercise before, and later with, the real
 * operational program. Today it runs the scripted simulation. The bench injects conditions and alerts, moves the
 * aircraft along its route, and sets the cockpit lighting. Scenarios run scripted steps against a restarted
 * simulation and check the screen, can be recorded from the bench, and are written out as test procedure text.
 */
export default function FmsCduTestBench() {
  const { layout, failed } = useCduLayout();
  const [variantId, setVariantId] = useState(storedVariant);
  const [session, setSession] = useState(0);
  // Simulated time: it starts at the wall clock and runs at the chosen rate while the flight is playing.
  const simTime = useRef(Date.now());
  // A scenario run or a recording starts on the next session, so it always begins from a restarted simulation.
  const pendingScenario = useRef<Scenario | null>(null);
  const pendingRecording = useRef(false);
  const { backend, sim, runner, recorder } = useMemo(() => {
    simTime.current = Date.now();
    const fms = new ScriptedFms(() => new Date(simTime.current));
    // The run's context is fixed as it starts, so its report describes the run and not the controls afterwards.
    const chosen = variantById(variantId);
    const runner = pendingScenario.current
      ? new ScenarioRunner(pendingScenario.current, fms, { variant: `${chosen.id} (${chosen.label})`, cycle: fms.activeCycle.id })
      : null;
    const recorder = pendingRecording.current ? new ScenarioRecorder(() => new Date(simTime.current)) : null;
    pendingScenario.current = null;
    pendingRecording.current = false;
    return { backend: fms, sim: new FlightSimulator(fms), runner, recorder };
  }, [session]); // eslint-disable-line react-hooks/exhaustive-deps
  const [recording, setRecording] = useState(false);
  const recordTo = recording ? recorder : null;
  const pausedFor = useRef<ScenarioRunner | null>(null);
  const subscribe = useCallback((listener: () => void) => backend.subscribe(listener), [backend]);
  useSyncExternalStore(subscribe, () => backend.revision());
  const [log, setLog] = useState<LogEntry[]>([]);
  const [alert, setAlert] = useState("");
  const [libraryAlert, setLibraryAlert] = useState(ALERTS[0].text);
  const [lighting, setLighting] = useState<Lighting>({ mode: "day", ambient: LIGHTING_MODES[0].ambient });
  const [playing, setPlaying] = useState(false);
  const [rate, setRate] = useState(1);
  const [range, setRange] = useState(20);
  const [navLoad, setNavLoad] = useState<string | null>(null);
  const [headingInput, setHeadingInput] = useState("090");
  const [jumpNote, setJumpNote] = useState<string | null>(null);
  const variant = variantById(variantId);

  // Time moves in ticks (scenario.ts): while flying, each callback runs `rate` ticks, each advancing the clock, the
  // flight and then the scenario, so a run sees the same timeline at any rate or callback pacing. Paused with no run
  // is an aircraft freeze: the aircraft stands still but the clock runs, so timers and a self test complete. Paused
  // during a run pauses the run: its clock stops, so no deadline or delayed step is consumed.
  useEffect(() => {
    const interval = TICK_SECONDS * 1000;
    const timer = window.setInterval(() => {
      const running = runner !== null && !runner.finished;
      if (playing) advanceTicks(rate, ms => { simTime.current += ms; }, sim, runner);
      else if (!running) { simTime.current += interval; backend.tick(); }
      // A finished scenario pauses the flight once; flying on afterwards is the engineer's choice.
      if (runner?.finished && pausedFor.current !== runner) { pausedFor.current = runner; setPlaying(false); }
    }, interval);
    return () => window.clearInterval(timer);
  }, [backend, sim, runner, playing, rate]);

  const chooseVariant = (id: string) => {
    setVariantId(id);
    try { window.localStorage.setItem(VARIANT_KEY, id); } catch { /* a remembered choice is a convenience only */ }
  };

  const chooseLighting = (mode: LightingMode) => {
    setLighting({ mode, ambient: LIGHTING_MODES.find(option => option.id === mode)!.ambient });
  };

  const onKey = useCallback((event: CduKeyEvent) => {
    const title = screenText(backend.screen())[0].trim();
    setLog(entries => [{ ...event, title }, ...entries].slice(0, 200));
    recordTo?.key(event.fn);
  }, [backend, recordTo]);

  const reset = () => { setSession(value => value + 1); setLog([]); setPlaying(false); setRecording(false); };
  const runScenario = (scenario: Scenario) => {
    pendingScenario.current = scenario;
    setSession(value => value + 1);
    setLog([]);
    setRecording(false);
    setPlaying(true);
  };
  const startRecording = () => {
    pendingRecording.current = true;
    setSession(value => value + 1);
    setLog([]);
    setPlaying(false);
    setRecording(true);
  };
  const finishRecording = (title: string) => {
    setRecording(false);
    return recorder ? recorder.toScenario(title) : null;
  };
  const guidance = sim.guidance;
  const signed = (value: number, digits = 0) => `${value >= 0 ? "+" : "−"}${Math.abs(value).toFixed(digits)}`;

  const next = backend.activeRoute.legs[0];
  // On the final approach: armed approach and the aircraft past the FAF (the runway is the active waypoint).
  const onFinal = backend.approachArmed && next?.kind === "wpt" && /^RW\d{2}/.test(next.ident);
  const failedFms = backend.hasCondition("fmsFail");
  const lampNote = (lamp: string | undefined) =>
    lamp === undefined ? "sensor" : lamp === "MENU" ? "MENU light" : variant.annunciators.some(code => code === lamp) ? `${lamp} lamp` : "no lamp on this variation";
  const meaning = ALERTS.find(entry => entry.text === libraryAlert)?.meaning;

  return (
    // A <main>, as every workspace page is: the shell frames and densifies pages by that element.
    <main className="fmsBench" aria-label="FMS Test Bench">
      <header className="fmsBenchHeader">
        <div>
          <span className="fmsBenchEyebrow">TEST BENCH</span>
          <h1>CMA-9000 FMS control display unit</h1>
          <p>
            A photorealistic, touchable CDU running a <strong>scripted simulation</strong>: key behaviour follows the
            CMA-9000 Operator's Manual, and courses and distances come from a small demonstration navigation
            database. It is not a navigation computer, and it is built so the real operational program can drive it
            later.
          </p>
        </div>
        <label className="fmsBenchVariant">
          <span>Hardware variation</span>
          <select value={variant.id} onChange={event => chooseVariant(event.target.value)}>
            {CDU_VARIANTS.map(option => <option key={option.id} value={option.id}>{option.id} — {option.label}</option>)}
          </select>
        </label>
      </header>

      <div className="fmsBenchBody">
        <div className="fmsBenchMain">
          <div className={`fmsBenchPanel mode-${lighting.mode}`}>
            {layout
              ? <FmsCduPanel backend={backend} variant={variant} layout={layout} onKey={onKey} lighting={lighting} />
              : <p className="fmsBenchLoading" role="status">{failed ? "The CDU model could not be loaded." : "Loading the CDU model…"}</p>}
          </div>

          <section className="fmsBenchCard" aria-label="EFIS">
            <div className="fmsBenchMapHead">
              <h2>EFIS: primary flight and navigation displays</h2>
            </div>
            <p className="fmsBenchHint">
              A generic EFIS drawn only from what the FMS publishes (desired track, cross-track, vertical deviation, roll
              command, distance to go, targets and modes, each with a validity status) and the aircraft's own attitude and
              air data. Magenta is what the FMS commands, green an engaged mode, white armed, cyan selected, amber a flag.
            </p>
            <FmsEfis fms={backend} sim={sim} range={range} />
          </section>

          <section className="fmsBenchCard fmsBenchMapCard">
            <div className="fmsBenchMapHead">
              <h2>Engineering map (north-up, with the true position)</h2>
              <label>
                <span>Range</span>
                <select value={range} onChange={event => setRange(Number(event.target.value))} aria-label="Map range">
                  {MAP_RANGES.map(value => <option key={value} value={value}>{value} NM</option>)}
                </select>
              </label>
            </div>
            <div className={`fmsBenchMapScreen mode-${lighting.mode}`}>
              <FmsMap fms={backend} sim={sim} range={range} />
            </div>
          </section>

          <section className="fmsBenchCard">
            <h2>Cockpit lighting</h2>
            <div className="fmsBenchModes" role="radiogroup" aria-label="Cockpit lighting">
              {LIGHTING_MODES.map(option => (
                <label key={option.id} className={lighting.mode === option.id ? "selected" : undefined}>
                  <input type="radio" name="fmsBenchLighting" value={option.id} checked={lighting.mode === option.id}
                    onChange={() => chooseLighting(option.id)} />
                  {option.label}
                </label>
              ))}
            </div>
            <label className="fmsBenchAmbient">
              <span>Ambient light at the light sensor</span>
              <input type="range" min={0} max={100} value={Math.round(lighting.ambient * 100)} aria-label="Ambient light"
                onChange={event => setLighting(current => ({ ...current, ambient: Number(event.target.value) / 100 }))} />
            </label>
            <p className="fmsBenchReadout">
              Display <strong data-testid="fms-luminance">{formatLuminance(displayLuminance(backend.brightness(), lighting))} fL</strong>
              {lighting.mode === "nvg" ? " (NVG range 0.1–3 fL)" : ""}. BRT on the panel adjusts it within the range.
            </p>
          </section>
        </div>

        <aside className="fmsBenchSide">
          <section className="fmsBenchCard">
            <h2>Flight</h2>
            <p className="fmsBenchReadout">
              {next?.kind === "wpt"
                ? <>Active waypoint <strong>{next.ident}</strong>{guidance.distanceToGo !== null && guidance.mode === "LNAV" ? `, ${guidance.distanceToGo.toFixed(1)} NM` : ""}</>
                : next?.kind === "cond" ? <>Active leg <strong>{conditionalLabel(next)}</strong></> : next ? "Route discontinuity ahead" : "End of route"}
            </p>
            <div className="fmsBenchActions">
              {/* Pause is a bench control: it stays usable whatever has failed in the simulated aircraft. */}
              <button type="button" onClick={() => setPlaying(value => !value)} aria-pressed={playing}>
                {playing ? "Pause" : "Fly"}
              </button>
              {!playing ? <span className="fmsBenchHint">{runner && !runner.finished ? "Run paused: its clock is stopped." : "Aircraft frozen: the clock runs."}</span> : null}
              <label className="fmsBenchRate">
                <span>Rate</span>
                <select value={rate} onChange={event => setRate(Number(event.target.value))} aria-label="Simulation rate">
                  {[1, 4, 16, 64].map(value => <option key={value} value={value}>{value}×</option>)}
                </select>
              </label>
              <button type="button" disabled={failedFms}
                onClick={() => setJumpNote(backend.sequence() === "discontinuity" ? "Jump stops at a route discontinuity. Close it on LEGS, or override it (engineering)." : null)}>
                Jump to next waypoint
              </button>
              {next?.kind === "disco" && !failedFms
                ? <button type="button" onClick={() => { backend.overrideDiscontinuity(); setJumpNote("Discontinuity overridden (engineering action, logged)."); }}>Override discontinuity</button>
                : null}
              <button type="button" onClick={reset}>Restart the simulation</button>
            </div>
            {/* The flight mode annunciator: engaged modes in green, armed ones in white, as on the PFD. */}
            {jumpNote ? <p className="fmsBenchHint" role="status">{jumpNote}</p> : null}
            {/* The flight mode annunciator shows the modes the controller is in (flight.ts), not a reading of the motion. */}
            <div className="fmsBenchFma" role="status" aria-label="Flight modes">
              <span className="engaged">{sim.lateralMode === "LNAV" ? (onFinal && backend.approachType ? backend.approachType : guidance.mode) : sim.headingHeld ? "HDG HOLD" : "HDG SEL"}</span>
              {sim.lnavIsArmed ? <span className="armed">LNAV</span> : null}
              {backend.approachArmed && !onFinal ? <span className="armed">APPR</span> : null}
              <span className="engaged">{sim.verticalMode}</span>
            </div>
            {sim.modeEvents.length ? <p className="fmsBenchHint">Last mode change: {sim.modeEvents.at(-1)!.event}, {sim.modeEvents.at(-1)!.detail}</p> : null}
            <form className="fmsBenchAutopilot" onSubmit={event => { event.preventDefault(); sim.selectHeading(Number(headingInput) || 0); }}>
              <label>
                <span>Heading</span>
                <input inputMode="numeric" value={headingInput} maxLength={3} aria-label="Selected heading"
                  onChange={event => setHeadingInput(event.target.value.replace(/\D/g, ""))} />
              </label>
              {/* HDG SEL is the autopilot's basic mode, so it stays available when the FMS has failed. */}
              <button type="submit" aria-pressed={sim.lateralMode === "HDG"}>HDG SEL</button>
              <button type="button" disabled={failedFms || sim.lateralMode === "LNAV"} aria-pressed={sim.lnavIsArmed} onClick={() => sim.armLnav()}>LNAV</button>
              <button type="button" disabled={failedFms || !backend.approachType} aria-pressed={backend.approachArmed}
                onClick={() => { if (!backend.approachArmed) recordTo?.armApproach(); backend.armApproach(!backend.approachArmed); }}>APPR</button>
              <button type="button" disabled={failedFms} onClick={() => { recordTo?.goAround(); backend.goAround(); }}>TOGA</button>
              <button type="button" disabled={failedFms || sim.altitudeHoldReference === null} onClick={() => sim.engageVnav()}>VNAV</button>
            </form>
            <dl className="fmsBenchGuidance" aria-label="Guidance">
              <dt>Mode</dt><dd>{guidance.mode}</dd>
              <dt>DTK</dt><dd>{guidance.desiredTrack === null ? "---" : `${String(Math.round(guidance.desiredTrack) || 360).padStart(3, "0")}°`}</dd>
              <dt>TRK</dt><dd>{String(Math.round(backend.track) || 360).padStart(3, "0")}°</dd>
              <dt>XTK</dt><dd>{guidance.crossTrack >= 0 ? "R" : "L"}{Math.abs(guidance.crossTrack).toFixed(2)} NM</dd>
              <dt>Bank</dt><dd>{guidance.mode === "HDG" ? "—" : `${sim.bankAngle >= 0 ? "R" : "L"}${Math.abs(sim.bankAngle).toFixed(0)}°`}</dd>
              <dt>GS</dt><dd>{Math.round(backend.groundSpeed)} kt</dd>
              <dt>ALT</dt><dd>{Math.round(backend.altitude)} ft → {Math.round(guidance.targetAltitude)}</dd>
              <dt>VS</dt><dd>{signed(Math.round(backend.verticalSpeed / 10) * 10)} fpm</dd>
            </dl>
          </section>

          <FmsScenarioCard
            runner={runner}
            recording={recording}
            screenLines={screenText(backend.screen())}
            onRun={runScenario}
            onStop={() => runner?.abandon()}
            onRecord={startRecording}
            onFinishRecording={finishRecording}
            onCheckLine={line => recorder?.checkLine(line, screenText(backend.screen())[line])}
          />

          <section className="fmsBenchCard">
            <h2>Conditions</h2>
            <ul className="fmsBenchConditions">
              {CONDITIONS.map(condition => (
                <li key={condition.id}>
                  <label>
                    <input type="checkbox" checked={backend.hasCondition(condition.id)}
                      disabled={failedFms && condition.id !== "fmsFail"}
                      onChange={event => { recordTo?.condition(condition.id, event.target.checked); backend.setCondition(condition.id, event.target.checked); }} />
                    <span>
                      <b>{condition.label}</b> <small className={lampNote(condition.lamp).startsWith("no ") ? "absent" : undefined}>{lampNote(condition.lamp)}</small>
                      <span className="fmsBenchHint">{condition.description}</span>
                    </span>
                  </label>
                </li>
              ))}
            </ul>
          </section>

          <section className="fmsBenchCard">
            <h2>Navigation data</h2>
            <p className="fmsBenchReadout">
              Active <strong>{backend.activeCycle.id}</strong> ({backend.activeCycle.source}): {backend.navdb.counts.airports} airports, {backend.navdb.counts.navaids} navaids,{" "}
              {backend.navdb.counts.fixes} fixes, {backend.navdb.counts.airways} airways, {backend.navdb.counts.procedures} procedures.
              The built-in set is invented demonstration data; its two cycles hold the same data.
            </p>
            {backend.inactiveCycle ? (
              <p className="fmsBenchReadout">
                Inactive <strong>{backend.inactiveCycle.id}</strong> ({backend.inactiveCycle.source}).{" "}
                <button type="button" disabled={failedFms} onClick={() => backend.swapCycles()}>Activate {backend.inactiveCycle.id}</button>
              </p>
            ) : null}
            <label className="fmsBenchFile">
              <span>Load ARINC 424 data (waypoints, navaids, airports, runways, airways) as the inactive cycle</span>
              <input type="file" accept=".pc,.dat,.txt,.424,text/plain" aria-label="ARINC 424 navigation data file"
                onChange={async event => {
                  const file = event.target.files?.[0];
                  if (!file) return;
                  const outcome = backend.loadArinc424(await file.text(), file.name);
                  setNavLoad("refused" in outcome
                    ? `Refused, nothing changed. ${outcome.refused}.`
                    : `${file.name}: ${outcome.read} records read, ${outcome.skipped} skipped${outcome.errors.length ? `; ${outcome.errors[0]}` : ""}. Loaded as inactive cycle ${outcome.loaded}: activate it on IDENT or here.`);
                  event.target.value = "";
                }} />
            </label>
            {navLoad ? <p className="fmsBenchHint" role="status">{navLoad}</p> : null}
            {backend.datasetLog.length ? (
              <ul className="fmsBenchHint" aria-label="Navigation data record">
                {backend.datasetLog.map((entry, index) => <li key={index}><b>{entry.action}</b> {entry.detail}</li>)}
              </ul>
            ) : null}
          </section>

          <section className="fmsBenchCard">
            <h2>Alerts</h2>
            <form className="fmsBenchAlert" onSubmit={event => { event.preventDefault(); recordTo?.alert(libraryAlert); backend.raiseAlert(libraryAlert); }}>
              <select value={libraryAlert} aria-label="Alert from the manual" onChange={event => setLibraryAlert(event.target.value)}>
                {ALERTS.map(entry => <option key={entry.text} value={entry.text}>{entry.text}</option>)}
              </select>
              <button type="submit" disabled={failedFms}>Raise</button>
            </form>
            {meaning ? <p className="fmsBenchHint">{meaning}</p> : null}
            <form className="fmsBenchAlert" onSubmit={event => { event.preventDefault(); if (alert.trim()) { recordTo?.alert(alert.trim()); backend.raiseAlert(alert.trim()); setAlert(""); } }}>
              <input value={alert} maxLength={24} placeholder="Other text, e.g. UNABLE RNP" aria-label="Alert message to raise"
                onChange={event => setAlert(event.target.value)} />
              <button type="submit" disabled={!alert.trim() || failedFms}>Raise alert</button>
            </form>
          </section>

          <section className="fmsBenchCard">
            <h2>Keyboard</h2>
            <dl className="fmsBenchKeys">
              <dt>A–Z, 0–9</dt><dd>Type into the scratchpad</dd>
              <dt>F1–F6</dt><dd>Left line select keys (Shift for right)</dd>
              <dt>Backspace</dt><dd>CLR (hold for one second to clear all)</dd>
              <dt>Enter</dt><dd>EXEC</dd>
              <dt>PgUp / PgDn</dt><dd>PREV / NEXT</dd>
              <dt>Space . / -</dt><dd>SP, decimal, slash, +/-</dd>
            </dl>
          </section>

          <section className="fmsBenchCard fmsBenchLog">
            <h2>Key events <small>{log.length}</small></h2>
            {log.length === 0
              ? <p className="fmsBenchEmpty">Press a key on the panel.</p>
              : (
                <ol>
                  {log.map((entry, index) => (
                    <li key={`${entry.at.getTime()}-${index}`}>
                      <time>{entry.at.toLocaleTimeString(undefined, { hour12: false })}</time>
                      <b>{entry.fn.replace(/^CHAR_/, "")}{entry.held ? " (held)" : ""}</b>
                      <span>{entry.title}</span>
                    </li>
                  ))}
                </ol>
              )}
          </section>
        </aside>
      </div>
    </main>
  );
}
