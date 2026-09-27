import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import { ALERTS } from "./alerts";
import { parseArinc424 } from "./arinc424";
import { CONDITIONS } from "./conditions";
import { FlightSimulator, MAP_RANGES } from "./flight";
import FmsCduPanel from "./FmsCduPanel";
import FmsMap from "./FmsMap";
import { conditionalLabel } from "./fmsModel";
import { useCduLayout, type CduKeyEvent } from "./layout";
import { LIGHTING_MODES, displayLuminance, type Lighting, type LightingMode } from "./lighting";
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
 * aircraft along its route, and sets the cockpit lighting; the key event log is the seam a future test procedure
 * integration records from.
 */
export default function FmsCduTestBench() {
  const { layout, failed } = useCduLayout();
  const [variantId, setVariantId] = useState(storedVariant);
  const [session, setSession] = useState(0);
  // Simulated time: it starts at the wall clock and runs at the chosen rate while the flight is playing.
  const simTime = useRef(Date.now());
  const { backend, sim } = useMemo(() => {
    simTime.current = Date.now();
    const fms = new ScriptedFms(() => new Date(simTime.current));
    return { backend: fms, sim: new FlightSimulator(fms) };
  }, [session]); // eslint-disable-line react-hooks/exhaustive-deps
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
  const variant = variantById(variantId);

  // A quarter-second loop flies the aircraft while playing; paused, time stands still but timers are checked.
  useEffect(() => {
    const interval = 250;
    const timer = window.setInterval(() => {
      if (!playing) { backend.tick(); return; }
      const dt = (interval / 1000) * rate;
      simTime.current += dt * 1000;
      sim.step(dt);
    }, interval);
    return () => window.clearInterval(timer);
  }, [backend, sim, playing, rate]);

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
  }, [backend]);

  const reset = () => { setSession(value => value + 1); setLog([]); setPlaying(false); };
  const guidance = sim.guidance;
  const signed = (value: number, digits = 0) => `${value >= 0 ? "+" : "−"}${Math.abs(value).toFixed(digits)}`;

  const next = backend.activeRoute.legs[0];
  const failedFms = backend.hasCondition("fmsFail");
  const lampNote = (lamp: string) =>
    lamp === "MENU" ? "MENU light" : variant.annunciators.some(code => code === lamp) ? `${lamp} lamp` : "no lamp on this variation";
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

          <section className="fmsBenchCard fmsBenchMapCard">
            <div className="fmsBenchMapHead">
              <h2>Navigation map</h2>
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
              <button type="button" onClick={() => setPlaying(value => !value)} disabled={failedFms} aria-pressed={playing}>
                {playing ? "Pause" : "Fly"}
              </button>
              <label className="fmsBenchRate">
                <span>Rate</span>
                <select value={rate} onChange={event => setRate(Number(event.target.value))} aria-label="Simulation rate">
                  {[1, 4, 16, 64].map(value => <option key={value} value={value}>{value}×</option>)}
                </select>
              </label>
              <button type="button" onClick={() => backend.sequence()} disabled={failedFms}>Jump to next waypoint</button>
              <button type="button" onClick={reset}>Restart the simulation</button>
            </div>
            {/* The flight mode annunciator: engaged modes in green, armed ones in white, as on the PFD. */}
            <div className="fmsBenchFma" role="status" aria-label="Flight modes">
              <span className="engaged">{sim.lateralMode === "LNAV" ? (guidance.mode === "HDG" ? "LNAV" : guidance.mode) : "HDG SEL"}</span>
              {sim.lnavIsArmed ? <span className="armed">LNAV</span> : null}
              <span className="engaged">{Math.abs(backend.verticalSpeed) > 100 ? "VNAV PTH" : "VNAV ALT"}</span>
            </div>
            <form className="fmsBenchAutopilot" onSubmit={event => { event.preventDefault(); sim.selectHeading(Number(headingInput) || 0); }}>
              <label>
                <span>Heading</span>
                <input inputMode="numeric" value={headingInput} maxLength={3} aria-label="Selected heading"
                  onChange={event => setHeadingInput(event.target.value.replace(/\D/g, ""))} />
              </label>
              <button type="submit" disabled={failedFms} aria-pressed={sim.lateralMode === "HDG"}>HDG SEL</button>
              <button type="button" disabled={failedFms || sim.lateralMode === "LNAV"} aria-pressed={sim.lnavIsArmed} onClick={() => sim.armLnav()}>LNAV</button>
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

          <section className="fmsBenchCard">
            <h2>Conditions</h2>
            <ul className="fmsBenchConditions">
              {CONDITIONS.map(condition => (
                <li key={condition.id}>
                  <label>
                    <input type="checkbox" checked={backend.hasCondition(condition.id)}
                      disabled={failedFms && condition.id !== "fmsFail"}
                      onChange={event => backend.setCondition(condition.id, event.target.checked)} />
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
              <strong>{backend.navdb.cycle.id}</strong>: {backend.navdb.counts.airports} airports, {backend.navdb.counts.navaids} navaids,{" "}
              {backend.navdb.counts.fixes} fixes, {backend.navdb.counts.airways} airways, {backend.navdb.counts.procedures} procedures.
              The built-in set is invented demonstration data.
            </p>
            <label className="fmsBenchFile">
              <span>Load ARINC 424 data (waypoints, navaids, airports, runways, airways)</span>
              <input type="file" accept=".pc,.dat,.txt,.424,text/plain" aria-label="ARINC 424 navigation data file"
                onChange={async event => {
                  const file = event.target.files?.[0];
                  if (!file) return;
                  const result = parseArinc424(await file.text());
                  backend.loadNavData(result.data);
                  setNavLoad(`${file.name}: ${result.read} records read, ${result.skipped} skipped${result.errors.length ? `; ${result.errors[0]}` : ""}.`);
                  event.target.value = "";
                }} />
            </label>
            {navLoad ? <p className="fmsBenchHint" role="status">{navLoad}</p> : null}
          </section>

          <section className="fmsBenchCard">
            <h2>Alerts</h2>
            <form className="fmsBenchAlert" onSubmit={event => { event.preventDefault(); backend.raiseAlert(libraryAlert); }}>
              <select value={libraryAlert} aria-label="Alert from the manual" onChange={event => setLibraryAlert(event.target.value)}>
                {ALERTS.map(entry => <option key={entry.text} value={entry.text}>{entry.text}</option>)}
              </select>
              <button type="submit" disabled={failedFms}>Raise</button>
            </form>
            {meaning ? <p className="fmsBenchHint">{meaning}</p> : null}
            <form className="fmsBenchAlert" onSubmit={event => { event.preventDefault(); if (alert.trim()) { backend.raiseAlert(alert.trim()); setAlert(""); } }}>
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
