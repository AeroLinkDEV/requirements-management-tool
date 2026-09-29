import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import { ALERTS } from "./alerts";
import { CONDITIONS } from "./conditions";
import { FlightSimulator, MAP_RANGES } from "./flight";
import FmsCduPanel from "./FmsCduPanel";
import { aircraftData, fmsOutputs } from "./efis";
import { Nd, Pfd } from "./FmsEfis";
import FmsMap from "./FmsMap";
import FmsGpsTab from "./FmsGpsTab";
import FmsOutTheWindow, { type HudModes } from "./FmsOutTheWindow";
import { relayTerrain } from "./terrainRelay";
import { TerrainTiles, type TerrainSource } from "./terrainTiles";
import type { Layout, View } from "./outTheWindow";
import FmsScenarioCard from "./FmsScenarioCard";
import { conditionalLabel } from "./fmsModel";
import { fmsGpsView } from "./gpsBench";
import { stimulusFor } from "./gpsStimulus";
import { useCduLayout, type CduKeyEvent } from "./layout";
import { LIGHTING_MODES, displayLuminance, type Lighting, type LightingMode } from "./lighting";
import { KBTV_SOURCE, START_STATES, loadKbtvDemonstration, type StartStateId } from "./kbtvDemo";
import { ACTIVE_PROFILE, profileFingerprint } from "./profile";
import { ScenarioRecorder, ScenarioRunner, TICK_SECONDS, advanceTicks, scenarioStart, type Scenario } from "./scenario";
import { ScriptedFms } from "./scriptedFms";
import { screenText } from "./screen";
import { CDU_VARIANTS, DEFAULT_VARIANT_ID, variantById } from "./variants";
import "./FmsCduTestBench.css";

const VARIANT_KEY = "aerolink.fmsCdu.variant";

const storedVariant = () => {
  try { return window.localStorage.getItem(VARIANT_KEY) ?? DEFAULT_VARIANT_ID; } catch { return DEFAULT_VARIANT_ID; }
};

/** The bench's tools under the cockpit, one tab each; the chosen one is remembered. */
const TABS = [
  { id: "scenarios", label: "Scenarios" }, { id: "conditions", label: "Conditions" }, { id: "gps", label: "GPS sensors" },
  { id: "navdata", label: "Nav data" }, { id: "lighting", label: "Lighting and keys" },
] as const;
type TabId = (typeof TABS)[number]["id"];
const TAB_KEY = "aerolink.fmsCdu.tab";
const storedTab = (): TabId => {
  try { const id = window.localStorage.getItem(TAB_KEY); return TABS.find(tab => tab.id === id)?.id ?? "scenarios"; } catch { return "scenarios"; }
};

// The out-the-window view: whether it is shown, and how, is remembered. It starts hidden because showing it loads a
// 3D engine and the terrain around the aircraft.
const WINDOW_KEY = "aerolink.fmsCdu.window";
// Synthetic vision on the PFD, remembered; off until chosen, for the same reason.
const SVS_KEY = "aerolink.fmsCdu.svs";
const storedSvs = () => { try { return window.localStorage.getItem(SVS_KEY) === "on"; } catch { return false; } };
type WindowChoice = { shown: boolean; layout: Layout; view: View };
const WINDOW_LAYOUTS = [["hud", "HUD"], ["panel", "Panel"]] as const;
const WINDOW_VIEWS = [["cockpit", "Cockpit"], ["chase", "Chase"], ["map", "Map"]] as const;
const storedWindow = (): WindowChoice => {
  const fallback: WindowChoice = { shown: false, layout: "hud", view: "cockpit" };
  try {
    const stored = JSON.parse(window.localStorage.getItem(WINDOW_KEY) ?? "null") as Partial<WindowChoice> | null;
    return {
      shown: stored?.shown === true,
      layout: WINDOW_LAYOUTS.find(([id]) => id === stored?.layout)?.[0] ?? fallback.layout,
      view: WINDOW_VIEWS.find(([id]) => id === stored?.view)?.[0] ?? fallback.view,
    };
  } catch { return fallback; }
};

type LogEntry = CduKeyEvent & { title: string };

const formatLuminance = (fl: number) => (fl < 10 ? fl.toFixed(1) : String(Math.round(fl)));

/**
 * An interactive CMA-9000 control display unit for engineers to exercise before, and later with, the real
 * operational program. Today it runs the scripted simulation. The bench injects conditions and alerts, moves the
 * aircraft along its route, and sets the cockpit lighting. Scenarios run scripted steps against a restarted
 * simulation and check the screen, can be recorded from the bench, and are written out as test procedure text.
 */
export default function FmsCduTestBench({ terrain }: { terrain?: TerrainSource } = {}) {
  const { layout, failed } = useCduLayout();
  const [variantId, setVariantId] = useState(storedVariant);
  const [session, setSession] = useState(0);
  // Simulated time: it starts at the wall clock (or a scenario's planned start, which fixes the GPS sky) and runs at the chosen rate while the flight is playing.
  const simTime = useRef(Date.now());
  // A scenario run or a recording starts on the next session, so it always begins from a restarted simulation.
  const pendingScenario = useRef<Scenario | null>(null);
  const pendingRecording = useRef(false);
  // A demonstration start state (kbtvDemo.ts) also starts on the next session: a restarted simulation, then set up.
  const pendingStart = useRef<StartStateId | null>(null);
  const { backend, sim, runner, recorder, started } = useMemo(() => {
    simTime.current = (pendingScenario.current && scenarioStart(pendingScenario.current)) ?? Date.now();
    const fms = new ScriptedFms(() => new Date(simTime.current));
    const flight = new FlightSimulator(fms);
    const start = pendingStart.current;
    const started = start ? START_STATES[start].setUp(fms) : null;
    // The run's context is fixed as it starts, so its report describes the run and not the controls afterwards.
    const chosen = variantById(variantId);
    const runner = pendingScenario.current
      ? new ScenarioRunner(pendingScenario.current, fms, { variant: `${chosen.id} (${chosen.label})`, cycle: fms.activeCycle.id }, flight)
      : null;
    const recorder = pendingRecording.current ? new ScenarioRecorder(() => new Date(simTime.current)) : null;
    pendingScenario.current = null;
    pendingRecording.current = false;
    pendingStart.current = null;
    return { backend: fms, sim: flight, runner, recorder, started: start && started ? { id: start, outcome: started } : null };
  }, [session]); // eslint-disable-line react-hooks/exhaustive-deps
  const [recording, setRecording] = useState(false);
  const recordTo = recording ? recorder : null;
  // While recording, what the GPS sensors tab applies is recorded as scenario steps, when it is applied.
  useEffect(() => {
    const stimulus = stimulusFor(backend);
    stimulus.listener = recordTo ? (index, op) => recordTo.gps((index + 1) as 1 | 2, op) : null;
    return () => { stimulus.listener = null; };
  }, [backend, recordTo]);
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
  // The lower display beside the CDU: the cockpit ND, or the engineering map with the true position.
  const [lowerDisplay, setLowerDisplay] = useState<"nd" | "map">("nd");
  const [navLoad, setNavLoad] = useState<string | null>(null);
  const [navAirports, setNavAirports] = useState("");
  const [headingInput, setHeadingInput] = useState("090");
  const [jumpNote, setJumpNote] = useState<string | null>(null);
  const [tab, setTab] = useState<TabId>(storedTab);
  // One set of height tiles for the out-the-window view and the PFD's synthetic vision.
  const tiles = useMemo(() => new TerrainTiles(terrain ?? relayTerrain), [terrain]);
  const [svs, setSvs] = useState(storedSvs);
  const chooseSvs = (on: boolean) => {
    setSvs(on);
    try { window.localStorage.setItem(SVS_KEY, on ? "on" : "off"); } catch { /* a remembered choice is a convenience only */ }
  };
  const [outside, setOutside] = useState<WindowChoice>(storedWindow);
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

  const chooseTab = (id: TabId) => {
    setTab(id);
    try { window.localStorage.setItem(TAB_KEY, id); } catch { /* a remembered choice is a convenience only */ }
  };

  const chooseWindow = (change: Partial<WindowChoice>) => {
    const chosen = { ...outside, ...change };
    setOutside(chosen);
    try { window.localStorage.setItem(WINDOW_KEY, JSON.stringify(chosen)); } catch { /* a remembered choice is a convenience only */ }
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
  const startDemonstration = (id: StartStateId) => {
    pendingStart.current = id;
    setSession(value => value + 1);
    setLog([]);
    setRecording(false);
    setPlaying(false);
    setNavLoad(null);
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
  const bus = fmsOutputs(backend, sim);
  const air = aircraftData(backend, sim);
  const signed = (value: number, digits = 0) => `${value >= 0 ? "+" : "−"}${Math.abs(value).toFixed(digits)}`;

  const next = backend.activeRoute.legs[0];
  // The approach as the controller has it: the capability (ILS, or the GPS level: LPV, LNAV/VNAV, LNAV) is annunciated
  // armed until captured, engaged after.
  const approachLabel = backend.approachType && backend.approachType !== "NO APPR" ? backend.approachType : "APPR";
  // The flight mode annunciator shows the modes the controller is in (flight.ts), not a reading of the motion: engaged
  // modes, then armed ones. The Flight card and the head-up display both show it.
  const modes: HudModes = {
    lateral: sim.lateralMode === "LNAV" ? (sim.approachMode === "CAPTURED" ? approachLabel : guidance.mode) : sim.headingHeld ? "HDG HOLD" : "HDG SEL",
    vertical: sim.verticalMode,
    armed: [...(sim.lnavIsArmed ? ["LNAV"] : []), ...(sim.approachMode === "ARMED" ? [approachLabel] : [])],
  };
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
          <p className="fmsBenchProfile" data-testid="fms-bench-profile">
            Aircraft profile: <strong>{ACTIVE_PROFILE.title}</strong> ({ACTIVE_PROFILE.id} v{ACTIVE_PROFILE.version}, {profileFingerprint(ACTIVE_PROFILE)}).
            Declared as data; parameters not yet flown by the simulation are marked for later stages.
          </p>
        </div>
        <label className="fmsBenchVariant">
          <span>Hardware variation</span>
          <select value={variant.id} onChange={event => chooseVariant(event.target.value)}>
            {CDU_VARIANTS.map(option => <option key={option.id} value={option.id}>{option.id} — {option.label}</option>)}
          </select>
        </label>
      </header>

      <section className="fmsBenchCard fmsBenchWindow" aria-label="Out-the-window view">
        <div className="fmsBenchMapHead">
          <h2>Out the window</h2>
          <div className="fmsBenchDisplayControls">
            {outside.shown ? (
              <>
                <div className="fmsBenchModes" role="radiogroup" aria-label="Window layout">
                  {WINDOW_LAYOUTS.map(([id, label]) => (
                    <label key={id} className={outside.layout === id ? "selected" : undefined}>
                      <input type="radio" name="fmsBenchWindowLayout" value={id} checked={outside.layout === id} onChange={() => chooseWindow({ layout: id })} />
                      {label}
                    </label>
                  ))}
                </div>
                <div className="fmsBenchModes" role="radiogroup" aria-label="Window view">
                  {WINDOW_VIEWS.map(([id, label]) => (
                    <label key={id} className={outside.view === id ? "selected" : undefined}>
                      <input type="radio" name="fmsBenchWindowView" value={id} checked={outside.view === id} onChange={() => chooseWindow({ view: id })} />
                      {label}
                    </label>
                  ))}
                </div>
              </>
            ) : null}
            <button type="button" aria-expanded={outside.shown} onClick={() => chooseWindow({ shown: !outside.shown })}>
              {outside.shown ? "Hide the view" : "Show the view"}
            </button>
          </div>
        </div>
        {outside.shown
          ? <FmsOutTheWindow air={air} route={bus.activeRoute} modes={modes} layout={outside.layout} view={outside.view} tiles={tiles} />
          : <p className="fmsBenchHint">A 3D view from the simulated aircraft over open elevation data, with the active route in magenta: head-up or over a glareshield, from the cockpit, behind the aircraft, or above it.</p>}
      </section>

      <div className="fmsBenchCockpit">
        <div className={`fmsBenchPanel mode-${lighting.mode}`}>
          {layout
            ? <FmsCduPanel backend={backend} variant={variant} layout={layout} onKey={onKey} lighting={lighting} />
            : <p className="fmsBenchLoading" role="status">{failed ? "The CDU model could not be loaded." : "Loading the CDU model…"}</p>}
        </div>

        <section className={`fmsBenchCard fmsBenchDisplays mode-${lighting.mode}`} aria-label="EFIS">
          <div className="fmsBenchMapHead">
            <h2>EFIS</h2>
            <div className="fmsBenchDisplayControls">
              <div className="fmsBenchModes" role="radiogroup" aria-label="Lower display">
                {([["nd", "ND"], ["map", "Engineering map"]] as const).map(([id, label]) => (
                  <label key={id} className={lowerDisplay === id ? "selected" : undefined}>
                    <input type="radio" name="fmsBenchLowerDisplay" value={id} checked={lowerDisplay === id} onChange={() => setLowerDisplay(id)} />
                    {label}
                  </label>
                ))}
              </div>
              <label>
                <span>Range</span>
                <select value={range} onChange={event => setRange(Number(event.target.value))} aria-label="Map range">
                  {MAP_RANGES.map(value => <option key={value} value={value}>{value} NM</option>)}
                </select>
              </label>
              <label>
                <input type="checkbox" checked={svs} onChange={event => chooseSvs(event.target.checked)} />
                <span>Synthetic vision</span>
              </label>
            </div>
          </div>
          <Pfd bus={bus} air={air} now={backend.now.getTime()} svs={svs ? tiles : null} />
          {lowerDisplay === "nd"
            ? <Nd bus={bus} air={air} range={range} />
            : <div className="fmsBenchMapScreen"><FmsMap fms={backend} sim={sim} range={range} /></div>}
          <p className="fmsBenchHint">
            {lowerDisplay === "nd"
              ? "A generic EFIS drawn only from what the FMS publishes and the aircraft's own attitude and air data. Magenta is what the FMS commands, green an engaged mode, white armed, cyan selected, amber a flag."
              : "Engineering map: north-up, with the aircraft's true position as well as the FMS position. Not a cockpit display."}
          </p>
        </section>

        <section className="fmsBenchCard fmsBenchFlight" aria-label="Flight">
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
          <div className="fmsBenchFma" role="status" aria-label="Flight modes">
            <span className="engaged">{modes.lateral}</span>
            {modes.armed.map(mode => <span key={mode} className="armed">{mode}</span>)}
            <span className="engaged">{modes.vertical}</span>
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
            {/* APPR arms the approach; pressed off it disarms, or after capture cancels the approach to an altitude hold. */}
            <button type="button" disabled={failedFms || !backend.approachType} aria-pressed={backend.approachArmed || sim.approachMode === "CAPTURED"}
              title={sim.approachMode === "CAPTURED" ? "Approach captured: press to cancel it (the aircraft levels), or TOGA to go around" : backend.approachArmed ? "Approach armed: press to disarm" : "Arm the approach"}
              onClick={() => { const on = !backend.approachArmed; recordTo?.armApproach(on); backend.armApproach(on); }}>APPR</button>
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
      </div>

      <div className="fmsBenchTools">
        <div className="fmsBenchTabs" role="tablist" aria-label="Bench tools">
          {TABS.map(item => (
            <button key={item.id} type="button" role="tab" id={`fms-bench-tabbutton-${item.id}`} aria-controls={`fms-bench-tab-${item.id}`}
              aria-selected={tab === item.id} tabIndex={tab === item.id ? 0 : -1} onClick={() => chooseTab(item.id)}
              onKeyDown={event => {
                const at = TABS.findIndex(entry => entry.id === tab);
                const step = event.key === "ArrowRight" ? 1 : event.key === "ArrowLeft" ? -1 : 0;
                if (step) { event.preventDefault(); chooseTab(TABS[(at + step + TABS.length) % TABS.length].id); }
              }}>
              {item.label}
            </button>
          ))}
        </div>
        <div className="fmsBenchTabPanel fmsBenchCards" role="tabpanel" id="fms-bench-tab-scenarios" aria-labelledby="fms-bench-tabbutton-scenarios" hidden={tab !== "scenarios"}>
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
        </div>
        <div className="fmsBenchTabPanel fmsBenchCards" role="tabpanel" id="fms-bench-tab-conditions" aria-labelledby="fms-bench-tabbutton-conditions" hidden={tab !== "conditions"}>
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
        </div>
        <div className="fmsBenchTabPanel" role="tabpanel" id="fms-bench-tab-gps" aria-labelledby="fms-bench-tabbutton-gps" hidden={tab !== "gps"}>
          {tab === "gps" ? <FmsGpsTab view={fmsGpsView(backend)} fms={backend} /> : null}
        </div>
        <div className="fmsBenchTabPanel fmsBenchCards" role="tabpanel" id="fms-bench-tab-navdata" aria-labelledby="fms-bench-tabbutton-navdata" hidden={tab !== "navdata"}>
          <section className="fmsBenchCard">
            <h2>Navigation data</h2>
            <p className="fmsBenchReadout">
              Active <strong>{backend.activeCycle.id}</strong> ({backend.activeCycle.source}): {backend.navdb.counts.airports} airports, {backend.navdb.counts.navaids} navaids,{" "}
              {backend.navdb.counts.fixes} fixes, {backend.navdb.counts.airways} airways, {backend.navdb.counts.procedures} procedures.{" "}
              {backend.activeCycle.source === "demonstration data" && backend.inactiveCycle?.source === "demonstration data"
                ? "The built-in set is invented demonstration data; its two cycles hold the same data."
                : "The built-in set is invented demonstration data; a loaded cycle adds to it."}
            </p>
            <div className="fmsBenchDemo" role="group" aria-label="Real-data demonstration">
              <p className="fmsBenchHint">
                Real data: the FAA CIFP cycle 2609 extract for Burlington, Vermont (KBTV), bundled with the bench. It is a
                US Government work in the public domain, for demonstration only, not for navigation: the cycle is not kept
                current. The invented CYUL demonstration stays the default start.
              </p>
              <div className="fmsBenchActions">
                <button type="button" disabled={failedFms || backend.activeCycle.source === KBTV_SOURCE}
                  onClick={() => {
                    const outcome = loadKbtvDemonstration(backend);
                    setNavLoad("refused" in outcome ? `Refused, nothing changed. ${outcome.refused}.`
                      : `KBTV demonstration loaded and active: cycle ${outcome.loaded}, FAA CIFP 2609 (public domain, not for navigation).`);
                  }}>Load the KBTV demonstration (FAA CIFP 2609)</button>
                <button type="button" onClick={() => startDemonstration("kbtv-rnav15")}
                  title="Restarts the simulation, loads the KBTV data and places the aircraft 8 NM before STAEV at 3200 ft, cleared direct STAEV, approach armed">
                  Set up KBTV RNAV RWY 15
                </button>
              </div>
              {started ? (
                <p className="fmsBenchHint" role="status">
                  {"refused" in started.outcome ? `Set-up refused: ${started.outcome.refused}.` : `Set up: ${START_STATES[started.id].label}. Press Fly to fly the approach.`}
                </p>
              ) : null}
            </div>
            {backend.inactiveCycle ? (
              <p className="fmsBenchReadout">
                Inactive <strong>{backend.inactiveCycle.id}</strong> ({backend.inactiveCycle.source}).{" "}
                <button type="button" disabled={failedFms} onClick={() => backend.swapCycles()}>Activate {backend.inactiveCycle.id}</button>
              </p>
            ) : null}
            <label className="fmsBenchFile">
              <span>Airports to load from a full FAA CIFP (e.g. KBTV), or blank for a small file</span>
              <input value={navAirports} aria-label="Airports to load" placeholder="KBTV"
                onChange={event => setNavAirports(event.target.value.toUpperCase().replace(/[^A-Z0-9 ,]/g, ""))} />
            </label>
            <label className="fmsBenchFile">
              <span>Load ARINC 424 data (waypoints, navaids, airports, runways, airways, RNAV approaches with their published FAS) as the inactive cycle</span>
              <input type="file" accept=".pc,.dat,.txt,.424,text/plain,*" aria-label="ARINC 424 navigation data file"
                onChange={async event => {
                  const file = event.target.files?.[0];
                  if (!file) return;
                  const airports = navAirports.split(/[\s,]+/).filter(Boolean);
                  // A full CIFP is some 50 MB: without airports named, it is not loaded whole into the bench.
                  if (!airports.length && file.size > 5_000_000) {
                    setNavLoad(`${file.name} is large (${Math.round(file.size / 1_000_000)} MB): name the airports to load, then choose it again.`);
                    event.target.value = "";
                    return;
                  }
                  const outcome = backend.loadArinc424(await file.text(), file.name, airports);
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
        </div>
        <div className="fmsBenchTabPanel fmsBenchCards" role="tabpanel" id="fms-bench-tab-lighting" aria-labelledby="fms-bench-tabbutton-lighting" hidden={tab !== "lighting"}>
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
        </div>
      </div>
    </main>
  );
}
