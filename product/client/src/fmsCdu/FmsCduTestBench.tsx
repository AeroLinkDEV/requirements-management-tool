import { memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, useSyncExternalStore, type HTMLAttributes, type ReactNode } from "react";
import { ALERTS } from "./alerts";
import { CONDITIONS, UNMODELLED_CONDITIONS, type ConditionId } from "./conditions";
import { MAP_RANGES } from "./flight";
import FmsCduPanel from "./FmsCduPanel";
import { aircraftData, fmsOutputs } from "./efis";
import { Nd, Pfd } from "./FmsEfis";
import FmsMap from "./FmsMap";
import FmsGpsTab from "./FmsGpsTab";
import FmsOutTheWindow, { FmsOutTheWindowHeader, groundImagery, type Ground, type HudModes } from "./FmsOutTheWindow";
import type { ImagerySource } from "./groundImagery";
import { TERRAIN_COLOURINGS, type TerrainColouring } from "./terrainAwareness";
import { relayImagery, relayTerrain } from "./terrainRelay";
import { TerrainTiles, type TerrainSource } from "./terrainTiles";
import type { Layout, View } from "./outTheWindow";
import FmsScenarioCard from "./FmsScenarioCard";
import FmsSensorFaultCard from "./FmsSensorFaultCard";
import { conditionalLabel } from "./fmsModel";
import { PROCEDURE_CHARTS } from "./procedureCharts";
import { findProcedure } from "./procedures";
import { fmsGpsView } from "./gpsBench";
import { stimulusFor } from "./gpsStimulus";
import { useCduLayout, type CduKeyEvent } from "./layout";
import { LIGHTING_MODES, displayLuminance, type Lighting, type LightingMode } from "./lighting";
import { KBTV_SOURCE, START_STATES, loadKbtvDemonstration, type StartStateId } from "./kbtvDemo";
import { ACTIVE_PROFILE, PROFILES, profileById, profileFingerprint } from "./profile";
import { ScenarioRecorder, ScenarioRunner, TICK_SECONDS, advanceTicks, scenarioStart, type Scenario } from "./scenario";
import { DualFmsSystem } from "./dualFms";
import type { ScriptedFms } from "./scriptedFms";
import type { FmsSide } from "./crossTalk";
import { WMM2025_DATABASE } from "./wmm2025";
import { MAX_BARO_ERROR_FT, SETTING_RANGE_HPA, formatSetting } from "./baro";
import { browserUserDatabaseStore } from "./userDatabase";
import { screenText } from "./screen";
import { CDU_VARIANTS, DEFAULT_VARIANT_ID, variantById } from "./variants";
import { useFmsStationWindows, type StationSurfaceId } from "./useFmsStationWindows";
import { FmsStationSurface } from "./FmsStationSurface";
import { FmsStationDock, FmsStationPlaceholder, type StationArrangement } from "./FmsStationDock";
import "./FmsCduTestBench.css";
import "./FmsCockpitLayout.css";
import "./FmsStation.css";

const VARIANT_KEY = "aerolink.fmsCdu.variant";

const storedVariant = () => {
  try { return window.localStorage.getItem(VARIANT_KEY) ?? DEFAULT_VARIANT_ID; } catch { return DEFAULT_VARIANT_ID; }
};

const storedStationArrangement = (key: string | null): StationArrangement => {
  try {
    const value = key ? JSON.parse(window.localStorage.getItem(key) ?? "null") as Partial<StationArrangement> | null : null;
    return { preset: value?.preset === "two" || value?.preset === "three" ? value.preset : "single", instructorApart: value?.instructorApart === true };
  } catch { return { preset: "single", instructorApart: false }; }
};

/** A duration in seconds as h:mm:ss. */
const clockText = (seconds: number) => {
  const s = Math.floor(seconds);
  return `${Math.floor(s / 3600)}:${String(Math.floor((s % 3600) / 60)).padStart(2, "0")}:${String(s % 60).padStart(2, "0")}`;
};

/** The bench's tools under the cockpit, one tab each; the chosen one is remembered. */
const TABS = [
  { id: "flight", label: "Flight and setup" },
  { id: "scenarios", label: "Scenarios" }, { id: "conditions", label: "Conditions" }, { id: "gps", label: "GPS sensors" },
  { id: "dual", label: "Dual FMS and radios" }, { id: "navdata", label: "Nav data" }, { id: "lighting", label: "Lighting and keys" },
] as const;
type TabId = (typeof TABS)[number]["id"];
const TAB_KEY = "aerolink.fmsCdu.tab";
const storedTab = (): TabId => {
  try { const id = window.localStorage.getItem(TAB_KEY); return TABS.find(tab => tab.id === id)?.id ?? "scenarios"; } catch { return "scenarios"; }
};

type KeptPanelProps = { shown: boolean; render: () => ReactNode } & Omit<HTMLAttributes<HTMLDivElement>, "hidden" | "children">;
/**
 * A tool tab's panel. The simulation tick re-renders the bench four times a second; rebuilding every hidden panel
 * each time (the key log alone grows to 200 entries) held the page's main thread long enough to stall pointer input
 * for seconds on a loaded host (#1349). A panel not shown is not rebuilt: it keeps what it last drew, and its
 * components their state, and is brought up to date when it is shown again.
 */
const KeptPanel = memo(function KeptPanel({ shown, render, ...attributes }: KeptPanelProps) {
  return <div {...attributes} hidden={!shown}>{render()}</div>;
}, (before, after) => !before.shown && !after.shown);

// The out-the-window view: whether it is shown, and how, is remembered. It starts hidden because showing it loads a
// 3D engine and the terrain around the aircraft.
const WINDOW_KEY = "aerolink.fmsCdu.window";
// Synthetic vision on the PFD, remembered; off until chosen, for the same reason.
const SVS_KEY = "aerolink.fmsCdu.svs";
const storedSvs = () => { try { return window.localStorage.getItem(SVS_KEY) === "on"; } catch { return false; } };
type WindowChoice = { shown: boolean; layout: Layout; view: View; ground: Ground; colouring: TerrainColouring };
const WINDOW_LAYOUTS = [["hud", "HUD"], ["panel", "Panel"]] as const;
const WINDOW_VIEWS = [["cockpit", "Cockpit"], ["chase", "Chase"], ["map", "Map"]] as const;
const WINDOW_GROUNDS = [["imagery", "Imagery"], ["relief", "Relief"]] as const;
const COLOURING_LABELS: Record<TerrainColouring, string> = { off: "Off", relative: "Relative", absolute: "Absolute" };
const storedWindow = (): WindowChoice => {
  const fallback: WindowChoice = { shown: false, layout: "hud", view: "cockpit", ground: "imagery", colouring: "off" };
  try {
    const stored = JSON.parse(window.localStorage.getItem(WINDOW_KEY) ?? "null") as Partial<WindowChoice> | null;
    return {
      shown: stored?.shown === true,
      layout: WINDOW_LAYOUTS.find(([id]) => id === stored?.layout)?.[0] ?? fallback.layout,
      view: WINDOW_VIEWS.find(([id]) => id === stored?.view)?.[0] ?? fallback.view,
      ground: WINDOW_GROUNDS.find(([id]) => id === stored?.ground)?.[0] ?? fallback.ground,
      colouring: TERRAIN_COLOURINGS.find(id => id === stored?.colouring) ?? fallback.colouring,
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
export default function FmsCduTestBench({ terrain, imagery, userName }: { terrain?: TerrainSource; imagery?: ImagerySource; userName?: string } = {}) {
  const { layout, failed } = useCduLayout();
  const [variantId, setVariantId] = useState(storedVariant);
  const [session, setSession] = useState(0);
  const [cduSide, setCduSide] = useState<FmsSide>(1);
  const [showPeer, setShowPeer] = useState(false);
  // First increment is opt-in; the established engineering view remains the demo fallback.
  const viewKey = userName ? `aerolink.fmsCdu.cockpit.${encodeURIComponent(userName)}` : null;
  const [cockpitView, setCockpitView] = useState(() => {
    try { return viewKey !== null && window.localStorage.getItem(viewKey) === "on"; } catch { return false; }
  });
  const [focused, setFocused] = useState(false);
  const [iosOpen, setIosOpen] = useState(false);
  const [iosExpanded, setIosExpanded] = useState(false);
  const stationKey = userName ? `aerolink.fmsCdu.station.${encodeURIComponent(userName)}` : null;
  const [arrangement, setArrangement] = useState(() => storedStationArrangement(stationKey));
  const station = useFmsStationWindows({ scopeKey: stationKey ?? "anonymous" });
  const outsideAway = Boolean(station.windows.outside);
  const cockpitAway = Boolean(station.windows.cockpit);
  const instructorAway = Boolean(station.windows.instructor);
  const iosVisible = !cockpitView || iosOpen || cockpitAway || instructorAway;
  const iosReservesOutside = cockpitView && iosOpen && !outsideAway && !cockpitAway && !instructorAway;
  const iosButton = useRef<HTMLButtonElement>(null);
  const iosPanel = useRef<HTMLDivElement>(null);
  const closeIos = () => { station.returnSurface("instructor"); setIosOpen(false); iosButton.current?.focus(); };
  const chooseCockpitView = (on: boolean) => {
    if (!on) station.returnAll();
    setCockpitView(on);
    setIosOpen(false);
    setFocused(false);
    if (!on && tab === "flight") chooseTab("scenarios");
    try { if (viewKey) window.localStorage.setItem(viewKey, on ? "on" : "off"); } catch { /* optional preference */ }
  };
  useEffect(() => {
    if (cockpitView && iosOpen) iosPanel.current?.focus();
  }, [cockpitView, iosOpen, station.windows.instructor]);
  const chooseArrangement = (value: StationArrangement) => {
    if (value.preset !== arrangement.preset) station.returnAll();
    else if (!value.instructorApart) station.returnSurface("instructor");
    setArrangement(value);
    try { if (stationKey) window.localStorage.setItem(stationKey, JSON.stringify(value)); } catch { /* optional preference */ }
  };
  const openStationSurface = (id: StationSurfaceId) => {
    if (id === "instructor") setIosOpen(true);
    if (station.openSurface(id) && id === "outside") chooseWindow({ shown: true });
  };
  // The aircraft profile the next session flies (profile.ts); a scenario that names one flies that one.
  const profileChoice = useRef(ACTIVE_PROFILE.id);
  const secondaryProfileChoice = useRef("");
  // Simulated time: it starts at the wall clock (or a scenario's planned start, which fixes the GPS sky) and runs at the chosen rate while the flight is playing.
  const simTime = useRef(Date.now());
  // A scenario run or a recording starts on the next session, so it always begins from a restarted simulation.
  const pendingScenario = useRef<Scenario | null>(null);
  const pendingRecording = useRef(false);
  // A demonstration start state (kbtvDemo.ts) also starts on the next session: a restarted simulation, then set up.
  const pendingStart = useRef<StartStateId | null>(null);
  const { system, runner, recorder, started } = useMemo(() => {
    simTime.current = (pendingScenario.current && scenarioStart(pendingScenario.current)) ?? Date.now();
    const profile = profileById(pendingScenario.current?.profile) ?? profileById(profileChoice.current) ?? ACTIVE_PROFILE;
    // The user database (E5) is kept per signed-in user and profile; a scenario run starts from an empty one in memory,
    // so its outcome does not depend on what a user has stored.
    const userDatabase = pendingScenario.current || !userName ? undefined
      : { store: browserUserDatabaseStore(window.localStorage), scope: { userId: userName, profileId: profile.id } };
    const system = new DualFmsSystem(() => new Date(simTime.current), { profile, secondaryProfile: profileById(secondaryProfileChoice.current) ?? profile, ...(userDatabase ? { userDatabase } : {}) });
    const fms = system.computers[0], flight = system.flights[0];
    const start = pendingStart.current;
    const started = start ? START_STATES[start].setUp(fms, flight) : null;
    if (start) { fms.dualOperation?.settingsChanged(); fms.dualOperation?.finishEdit(true); system.computers[1].observeAircraft(fms); }
    // The run's context is fixed as it starts, so its report describes the run and not the controls afterwards.
    const chosen = variantById(variantId);
    const runner = pendingScenario.current
      ? new ScenarioRunner(pendingScenario.current, fms, { variant: `${chosen.id} (${chosen.label})`, cycle: fms.activeCycle.id }, flight)
      : null;
    const recorder = pendingRecording.current ? new ScenarioRecorder(() => new Date(simTime.current)) : null;
    pendingScenario.current = null;
    pendingRecording.current = false;
    pendingStart.current = null;
    return { system, runner, recorder, started: start && started ? { id: start, outcome: started } : null };
  }, [session, userName]); // eslint-disable-line react-hooks/exhaustive-deps
  const backend = system.computers[cduSide - 1];
  const peerBackend = system.computers[2 - cduSide];
  const sharedSensorCondition = (id: ConditionId) => ["gpsLost", "gpsIntegrity", "dmeOutage", "apirsFail", "dvsFail", "raFail"].includes(id);
  const conditionBackend = (id: ConditionId) => sharedSensorCondition(id) ? system.computers[0] : backend;
  const sim = system.simulator, guidanceBackend = system.computers[system.guidanceSide - 1];
  const [recording, setRecording] = useState(false);
  const recordTo = recording ? recorder : null;
  // While recording, what the GPS sensors tab applies is recorded as scenario steps, when it is applied.
  useEffect(() => {
    const stimulus = stimulusFor(system.computers[0]);
    stimulus.listener = recordTo ? (index, op) => recordTo.gps((index + 1) as 1 | 2, op) : null;
    return () => { stimulus.listener = null; };
  }, [system, recordTo]);
  const pausedFor = useRef<ScenarioRunner | null>(null);
  const subscribe = useCallback((listener: () => void) => { const off = system.computers.map(unit => unit.subscribe(listener)); return () => off.forEach(remove => remove()); }, [system]);
  useSyncExternalStore(subscribe, () => system.computers[0].revision() + system.computers[1].revision());
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
  const [userDbStatus, setUserDbStatus] = useState<string | null>(null);
  const [headingInput, setHeadingInput] = useState("090");
  const [restartOnGround, setRestartOnGround] = useState(false);
  const [magvarLoad, setMagvarLoad] = useState<string | null>(null);
  // The crew's autopilot selections under the helicopter profile: preselected altitude, vertical speed and speed.
  const [altInput, setAltInput] = useState("");
  const [vsInput, setVsInput] = useState("-500");
  const [spdInput, setSpdInput] = useState("");
  const pinsContext = `${session}:${guidanceBackend.pinsContinuation?.revision ?? 0}`;
  const [pinsDeclaration, setPinsDeclaration] = useState({ context: "", basicVfr: false, landingAreaVisible: false, publishedVisibility: false });
  const crewConditions = pinsDeclaration.context === pinsContext ? pinsDeclaration : { context: pinsContext, basicVfr: false, landingAreaVisible: false, publishedVisibility: false };
  const [gsInput, setGsInput] = useState("");
  const [jumpNote, setJumpNote] = useState<string | null>(null);
  const [tab, setTab] = useState<TabId>(() => { const saved = storedTab(); return saved === "flight" && !cockpitView ? "scenarios" : saved; });
  const visibleTabs = TABS.filter(item => cockpitView || item.id !== "flight");
  // One set of height tiles for the out-the-window view and the PFD's synthetic vision.
  const tiles = useMemo(() => new TerrainTiles(terrain ?? relayTerrain), [terrain]);
  const photos = useMemo(() => groundImagery(imagery ?? relayImagery), [imagery]);
  const [svs, setSvs] = useState(storedSvs);
  const chooseSvs = (on: boolean) => {
    setSvs(on);
    try { window.localStorage.setItem(SVS_KEY, on ? "on" : "off"); } catch { /* a remembered choice is a convenience only */ }
  };
  const [outside, setOutside] = useState<WindowChoice>(storedWindow);
  const outsideToggle = useRef<HTMLButtonElement>(null);
  const outsideToggleFocus = useRef<Document | null>(null);
  useLayoutEffect(() => {
    const destination = outsideToggleFocus.current;
    outsideToggleFocus.current = null;
    const button = outsideToggle.current;
    // Showing/hiding remounts the toolbar. Only hand back the focused user's toggle; an external
    // preset change or a document transfer must not focus an unrelated or stale destination.
    if (destination && button?.isConnected && button.ownerDocument === destination) button.focus();
  }, [outside.shown]);
  const variant = variantById(variantId);

  // Time moves in ticks (scenario.ts): while flying, each callback runs `rate` ticks, each advancing the clock, the
  // flight and then the scenario, so a run sees the same timeline at any rate or callback pacing. Paused with no run
  // is an aircraft freeze: the aircraft stands still but the clock runs, so timers and a self test complete. Paused
  // during a run pauses the run: its clock stops, so no deadline or delayed step is consumed.
  useEffect(() => {
    const interval = TICK_SECONDS * 1000;
    const timer = window.setInterval(() => {
      const running = runner !== null && !runner.finished;
      if (playing) advanceTicks(rate, ms => { simTime.current += ms; }, system, runner);
      else if (!running) { simTime.current += interval; system.tick(); }
      // A finished scenario pauses the flight once; flying on afterwards is the engineer's choice.
      if (runner?.finished && pausedFor.current !== runner) { pausedFor.current = runner; setPlaying(false); }
    }, interval);
    return () => window.clearInterval(timer);
  }, [system, runner, playing, rate]);

  /** A different aircraft profile restarts the simulation in it. */
  const chooseProfile = (id: string) => {
    profileChoice.current = id;
    setSession(s => s + 1);
  };

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
    recordTo?.key(event.fn, event.held);
  }, [backend, recordTo]);

  // Stable physical-side callbacks let memoized faceplate keys skip the four-Hz bench ticks (#1349).
  const onCockpitKeys = useMemo(() => ([1, 2] as const).map(side => (event: CduKeyEvent) => {
    const title = screenText(system.computers[side - 1].screen())[0].trim();
    setLog(entries => [{ ...event, title: `CDU ${side}: ${title}` }, ...entries].slice(0, 200));
    if (side === 1) recordTo?.key(event.fn, event.held);
  }), [system, recordTo]);

  const reset = () => { setSession(value => value + 1); setLog([]); setPlaying(false); setRecording(false); };
  const runScenario = (scenario: Scenario) => {
    pendingScenario.current = scenario;
    setCduSide(1);
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
    setCduSide(1);
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
  const bus = fmsOutputs(guidanceBackend, sim);
  const air = aircraftData(guidanceBackend, sim);
  const signed = (value: number, digits = 0) => `${value >= 0 ? "+" : "−"}${Math.abs(value).toFixed(digits)}`;

  const next = guidanceBackend.activeRoute.legs[0];
  // The executed approach's chart notes (C.10): restrictions, speed notes and minima the coded data does not carry,
  // shown for reference and never enforced.
  const approach = findProcedure(backend.navdb, backend.activeRoute, "APPROACH");
  const approachChart = approach ? PROCEDURE_CHARTS[`${backend.activeRoute.dest} ${approach.ident}`] : undefined;
  // The approach as the controller has it: the capability (ILS, or the GPS level: LPV, LNAV/VNAV, LNAV) is annunciated
  // armed until captured, engaged after.
  const approachLabel = guidanceBackend.approachType && guidanceBackend.approachType !== "NO APPR" ? guidanceBackend.approachType : "APPR";
  // The flight mode annunciator shows the modes the controller is in (flight.ts), not a reading of the motion: engaged
  // modes, then armed ones. The Flight card and the head-up display both show it.
  const modes: HudModes = {
    angleReference: guidanceBackend.hasCondition("fmsFail") ? "TRUE" : guidanceBackend.angleReference,
    magneticVariation: guidanceBackend.magneticField?.declination,
    lateral: sim.lateralMode === "LNAV" ? (sim.approachMode === "CAPTURED" ? approachLabel : guidance.mode) : sim.headingHeld ? "HDG HOLD" : "HDG SEL",
    vertical: sim.verticalMode,
    armed: [...(sim.lnavIsArmed ? ["LNAV"] : []), ...(sim.approachMode === "ARMED" ? [approachLabel] : [])],
  };
  const failedFms = backend.hasCondition("fmsFail");
  const lampNote = (lamp: string | undefined) =>
    lamp === undefined ? "sensor" : lamp === "MENU" ? "MENU light" : variant.annunciators.some(code => code === lamp) ? `${lamp} lamp` : "no lamp on this variation";
  const meaning = ALERTS.find(entry => entry.text === libraryAlert)?.meaning;

  const setupControls = (<>
        <label className="fmsBenchVariant">
          <span>Aircraft profile</span>
          <select value={backend.aircraftProfile.id} onChange={event => chooseProfile(event.target.value)}>
            {PROFILES.map(option => <option key={option.id} value={option.id}>{option.title}</option>)}
          </select>
        </label>
        <label className="fmsBenchVariant">
          <span>Hardware variation</span>
          <select value={variant.id} onChange={event => chooseVariant(event.target.value)}>
            {CDU_VARIANTS.map(option => <option key={option.id} value={option.id}>{option.id} — {option.label}</option>)}
          </select>
        </label>
  </>);

  const windowControls = (
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
                <div className="fmsBenchModes" role="radiogroup" aria-label="Window ground" title="Aerial imagery where there is some (the United States), relief elsewhere; or relief only">
                  {WINDOW_GROUNDS.map(([id, label]) => (
                    <label key={id} className={outside.ground === id ? "selected" : undefined}>
                      <input type="radio" name="fmsBenchWindowGround" value={id} checked={outside.ground === id} onChange={() => chooseWindow({ ground: id })} />
                      {label}
                    </label>
                  ))}
                </div>
                <div className="fmsBenchModes" role="radiogroup" aria-label="Terrain colouring"
                  title="Relative: red at or above 100 ft below the aircraft, amber within 500 ft. Absolute: height bands. Both with a contour every 500 ft">
                  {TERRAIN_COLOURINGS.map(id => (
                    <label key={id} className={outside.colouring === id ? "selected" : undefined}>
                      <input type="radio" name="fmsBenchTerrainColouring" value={id} checked={outside.colouring === id} onChange={() => chooseWindow({ colouring: id })} />
                      {COLOURING_LABELS[id]}
                    </label>
                  ))}
                </div>
              </>
            ) : null}
            <button type="button" ref={outsideToggle} aria-expanded={outside.shown} onClick={event => {
              outsideToggleFocus.current = event.currentTarget.ownerDocument.activeElement === event.currentTarget ? event.currentTarget.ownerDocument : null;
              chooseWindow({ shown: !outside.shown });
            }}>
              {outside.shown ? "Hide the view" : "Show the view"}
            </button>
          </div>
  );

  return (
    // A <main>, as every workspace page is: the shell frames and densifies pages by that element.
    <main className={`fmsBench${cockpitView ? " fmsBenchCockpitView" : ""}${focused ? " fmsBenchFocused" : ""}`} aria-label="FMS Test Bench" onKeyDown={event => {
      if (cockpitView && iosOpen && !cockpitAway && !instructorAway && event.key === "Escape"
        && (event.target as HTMLElement).ownerDocument === event.currentTarget.ownerDocument) { event.preventDefault(); closeIos(); }
    }}>
      <div className="fmsBenchViewBar fmsBenchActions">
        <button type="button" onClick={() => chooseCockpitView(!cockpitView)}>
          {cockpitView ? "Engineering view" : "Cockpit view"}
        </button>
        {!cockpitView ? <span className="fmsBenchHint">For separate browser windows, switch to Cockpit view.</span> : null}
        {cockpitView ? <>
          <button type="button" ref={iosButton} aria-expanded={iosVisible} aria-controls={instructorAway ? undefined : "fms-instructor"}
            onClick={() => instructorAway ? station.openSurface("instructor") : cockpitAway ? iosPanel.current?.focus() : iosOpen ? closeIos() : setIosOpen(true)}>Instructor station</button>
          <button type="button" onClick={() => setFocused(value => !value)}>{focused ? "Show navigation" : "Focus bench"}</button>
          <FmsStationDock arrangement={arrangement} onArrangement={chooseArrangement} windows={station.windows} opening={station.opening}
            errors={station.errors} onOpen={openStationSurface} onReturn={station.returnSurface} onReturnAll={station.returnAll} />
          <span className="fmsBenchHint">Scripted simulation · EFIS / AFCS: FMS {system.guidanceSide} · Inspected: CDU {cduSide}</span>
          {recording ? <strong className="fmsBenchHint" role="status">Recording CDU 1 only; CDU 2 input is not recorded.</strong> : null}
        </> : null}
      </div>
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
            Aircraft profile: <strong>{backend.aircraftProfile.title}</strong> ({backend.aircraftProfile.id} v{backend.aircraftProfile.version}, {profileFingerprint(backend.aircraftProfile)}).
            Declared as data; parameters not yet flown by the simulation are marked for later stages.
          </p>
        </div>
        {!cockpitView ? setupControls : null}
      </header>

      <div className={`fmsBenchUpper${cockpitView && iosOpen && iosExpanded ? " fmsBenchIosExpanded" : ""}${iosReservesOutside ? " fmsBenchIosReservesOutside" : ""}${outsideAway ? " fmsBenchOutsideAway" : ""}${cockpitAway ? " fmsBenchCockpitAway" : ""}`}>
      {outsideAway ? <FmsStationPlaceholder id="outside" onReturn={() => station.returnSurface("outside")} /> : null}
      <FmsStationSurface targetWindow={station.windows.outside} className={`${cockpitView ? "fmsBenchCockpitView " : ""}fmsStationSurfaceOutside`}>
      <section className="fmsBenchCard fmsBenchWindow" aria-label="Out-the-window view">
        {outside.shown
          ? <FmsOutTheWindow air={air} route={bus.activeRoute} modes={modes} layout={outside.layout} view={outside.view} tiles={tiles}
            ground={outside.ground} colouring={outside.colouring} imagery={photos} controls={windowControls} />
          : <><FmsOutTheWindowHeader controls={windowControls} /><p className="fmsBenchHint">A 3D view from the simulated aircraft over open elevation data, with the active route in magenta: head-up or over a glareshield, from the cockpit, behind the aircraft, or above it.</p></>}
      </section>
      </FmsStationSurface>
      {cockpitAway ? <FmsStationPlaceholder id="cockpit" onReturn={() => station.returnSurface("cockpit")} /> : null}
      <FmsStationSurface targetWindow={station.windows.cockpit} className={`${cockpitView ? "fmsBenchCockpitView " : ""}fmsStationSurfaceCockpit`}>
      <div className="fmsBenchCockpit">
        {cockpitView ? (<>
          {([1, 2] as const).map(side => <div key={side} className={`fmsBenchCduStation mode-${lighting.mode}`} data-side={side}
            data-active={cduSide === side} onFocusCapture={() => { if (!recording && (!runner || runner.finished)) setCduSide(side); }}>
            <div className="fmsBenchCduLabel"><strong>FMS {side} / CDU {side}</strong><span>{cduSide === side ? "Inspected" : ""}</span></div>
            {layout ? <FmsCduPanel backend={system.computers[side - 1]} variant={variant} layout={layout} lighting={lighting}
              onKey={onCockpitKeys[side - 1]} /> : <p role="status">{failed ? "The CDU model could not be loaded." : "Loading the CDU model…"}</p>}
          </div>)}
        </>) : (
        <div className={`fmsBenchPanel mode-${lighting.mode}`}>
          <div className="fmsBenchActions">
            <label>CDU inspected <select aria-label="CDU inspected" value={cduSide} disabled={recording || !!runner && !runner.finished}
              onChange={event => setCduSide(Number(event.target.value) as FmsSide)}>
              <option value={1}>FMS 1 / CDU 1</option><option value={2}>FMS 2 / CDU 2</option>
            </select></label>
            <button type="button" aria-pressed={showPeer} onClick={() => setShowPeer(value => !value)}>{showPeer ? "Hide peer CDU" : "Show peer CDU"}</button>
          </div>
          <div data-testid="fms-cdu-inspected" aria-label={`CDU ${cduSide}`}>

          {layout
            ? <FmsCduPanel backend={backend} variant={variant} layout={layout} onKey={onKey} lighting={lighting} />
            : <p className="fmsBenchLoading" role="status">{failed ? "The CDU model could not be loaded." : "Loading the CDU model…"}</p>}
          </div>
          {showPeer && layout ? <div data-testid="fms-cdu-peer" aria-label={`CDU ${3 - cduSide}`}>
            <h2>FMS {3 - cduSide} / CDU {3 - cduSide}</h2>
            <FmsCduPanel backend={peerBackend} variant={variant} layout={layout} lighting={lighting} />
          </div> : null}
        </div>
        )}

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
          <label>FMS guidance source <select aria-label="FMS guidance source" value={system.guidanceSide} disabled={recording || !!runner && !runner.finished}
            onChange={event => system.selectGuidance(Number(event.target.value) as FmsSide)}>
            <option value={1}>FMS 1</option><option value={2}>FMS 2</option>
          </select></label>
          <p className="fmsBenchHint">One physical aircraft. EFIS and AFCS use FMS {system.guidanceSide}; computer entries address CDU {cduSide}. Sensor faults affect the shared aircraft inputs.</p>
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
            {!cockpitView ? (<>
            <button type="button" disabled={guidanceBackend.hasCondition("fmsFail")}
              onClick={() => setJumpNote(guidanceBackend.sequence() === "discontinuity" ? "Jump stops at a route discontinuity. Close it on LEGS, or override it (engineering)." : null)}>
              Jump to next waypoint
            </button>
            {next?.kind === "disco" && !guidanceBackend.hasCondition("fmsFail")
              ? <button type="button" onClick={() => { guidanceBackend.overrideDiscontinuity(); setJumpNote("Discontinuity overridden (engineering action, logged)."); }}>Override discontinuity</button>
              : null}
            <button type="button" onClick={reset}>Restart the simulation</button>
            </>) : null}
          </div>
          {/* The flight mode annunciator: engaged modes in green, armed ones in white, as on the PFD. */}
          {jumpNote ? <p className="fmsBenchHint" role="status">{jumpNote}</p> : null}
          <div className="fmsBenchFma" role="status" aria-label="Flight modes">
            <span className="engaged">{modes.lateral}</span>
            {modes.armed.map(mode => <span key={mode} className="armed">{mode}</span>)}
            <span className="engaged">{modes.vertical}</span>
          </div>
          {sim.modeEvents.length ? <p className="fmsBenchHint">Last mode change: {sim.modeEvents.at(-1)!.event}, {sim.modeEvents.at(-1)!.detail}</p> : null}
          <form className="fmsBenchAutopilot" onSubmit={event => {
            event.preventDefault();
            const entry = Number(headingInput);
            if (!headingInput || entry < 0 || entry > 360) return;
            const heading = guidanceBackend.hasCondition("fmsFail") ? entry : guidanceBackend.angleFromEntry(entry);
            if (heading !== null) sim.selectHeading(heading);
          }}>
            <label>
              <span>Heading {guidanceBackend.hasCondition("fmsFail") ? "TRUE" : guidanceBackend.angleReference}</span>
              <input inputMode="numeric" value={headingInput} maxLength={3} aria-label="Selected heading"
                onChange={event => setHeadingInput(event.target.value.replace(/\D/g, ""))} />
            </label>
            {/* HDG SEL is the autopilot's basic mode, so it stays available when the FMS has failed. */}
            <button type="submit" aria-pressed={sim.lateralMode === "HDG"}>HDG SEL</button>
            <button type="button" disabled={guidanceBackend.hasCondition("fmsFail") || sim.lateralMode === "LNAV"} aria-pressed={sim.lnavIsArmed} onClick={() => sim.armLnav()}>LNAV</button>
            {/* APPR arms the approach; pressed off it disarms, or after capture cancels the approach to an altitude hold. */}
            <button type="button" disabled={guidanceBackend.hasCondition("fmsFail") || !guidanceBackend.approachType} aria-pressed={guidanceBackend.approachArmed || sim.approachMode === "CAPTURED"}
              title={sim.approachMode === "CAPTURED" ? "Approach captured: press to cancel it (the aircraft levels), or TOGA to go around" : guidanceBackend.approachArmed ? "Approach armed: press to disarm" : "Arm the approach"}
              onClick={() => { const on = !guidanceBackend.approachArmed; recordTo?.armApproach(on); guidanceBackend.armApproach(on); }}>APPR</button>
            <button type="button" disabled={guidanceBackend.hasCondition("fmsFail") && !sim.advisory} onClick={() => { recordTo?.goAround(); guidanceBackend.goAround(); sim.engageGoAround(); }}>TOGA</button>
            {sim.advisory ? null : <button type="button" disabled={guidanceBackend.hasCondition("fmsFail") || sim.altitudeHoldReference === null} onClick={() => sim.engageVnav()}>VNAV</button>}
          </form>
          {sim.advisory ? (
            // The helicopter profile: the crew flies the vertical axis and the speed; the FMS constraints are advisories.
            <form className="fmsBenchAutopilot fmsBenchVerticalSelections" aria-label="Vertical and speed selections" onSubmit={event => event.preventDefault()}>
              <label>
                <span>ALT SEL</span>
                <input inputMode="numeric" value={altInput} placeholder={String(sim.selectedAltitude)} maxLength={5} aria-label="Preselected altitude"
                  onChange={event => setAltInput(event.target.value.replace(/\D/g, ""))} />
              </label>
              <button type="button" disabled={!altInput} onClick={() => { const altitude = Number(altInput); recordTo?.autopilot({ altitude }); sim.selectAltitude(altitude); setAltInput(""); }}>SET</button>
              <label>
                <span>VS</span>
                <input inputMode="numeric" value={vsInput} maxLength={5} aria-label="Vertical speed"
                  onChange={event => setVsInput(event.target.value.replace(/[^\d-]/g, ""))} />
              </label>
              <button type="button" aria-pressed={sim.verticalSpeedTarget !== null} onClick={() => { const verticalSpeed = Number(vsInput) || 0; recordTo?.autopilot({ verticalSpeed }); sim.engageVerticalSpeed(verticalSpeed); }}>VS</button>
              <button type="button" aria-pressed={sim.verticalMode === "ALT HOLD"} onClick={() => { recordTo?.autopilot({ hold: true }); sim.engageAltitudeHold(); }}>ALT</button>
              <label>
                <span>SPD</span>
                <input inputMode="numeric" value={spdInput} placeholder={String(sim.selectedSpeed)} maxLength={3} aria-label="Selected speed"
                  onChange={event => setSpdInput(event.target.value.replace(/\D/g, ""))} />
              </label>
              <button type="button" disabled={!spdInput} onClick={() => { const speed = Number(spdInput); recordTo?.autopilot({ speed }); sim.selectSpeed(speed); setSpdInput(""); }}>SET SPD</button>
              {/* GSPD: a ground speed held along the heading in the low-speed regime, on the hover feedback (plan B3.1). */}
              <label>
                <span>GS</span>
                <input inputMode="numeric" value={gsInput} maxLength={2} aria-label="Selected ground speed"
                  onChange={event => setGsInput(event.target.value.replace(/\D/g, ""))} />
              </label>
              <button type="button" aria-pressed={sim.axisModes.pitch === "GSPD"} disabled={!gsInput}
                onClick={() => { const groundSpeed = Number(gsInput); if (sim.engageGroundSpeed(groundSpeed)) { recordTo?.autopilot({ groundSpeed }); setGsInput(""); } }}>GSPD</button>
              {/* The cyclic force-trim release, pressed and let go: the hover references re-datum where the aircraft is. */}
              <button type="button" title="Cyclic force-trim release" onClick={() => { recordTo?.autopilot({ forceTrimRelease: true }); sim.releaseForceTrim(); }}>FTR</button>
            </form>
          ) : null}
          <dl className="fmsBenchGuidance" aria-label="Guidance">
            {guidanceBackend.pinsContinuation ? <><dt>PinS continuation</dt><dd>
              {guidanceBackend.pinsContinuation.available ? guidanceBackend.pinsContinuation.endpoint.visualSegment.kind === "PROCEED VFR" ? "Proceed VFR" : "Proceed visually" : "Chart continuation not verified"}
              {guidanceBackend.pinsContinuation.active ? " — crew flying the visual segment" : ""}
            </dd></> : null}
            <dt>Mode</dt><dd>{guidance.mode}</dd>
            <dt>DTK</dt><dd>{guidance.desiredTrack === null ? "---" : guidanceBackend.angleText(guidance.desiredTrack)}</dd>
            <dt>TRK</dt><dd>{guidanceBackend.angleText(guidanceBackend.track)}</dd>
            <dt>XTK</dt><dd>{guidance.crossTrack >= 0 ? "R" : "L"}{Math.abs(guidance.crossTrack).toFixed(2)} NM</dd>
            <dt>Bank</dt><dd>{guidance.mode === "HDG" ? "—" : `${sim.bankAngle >= 0 ? "R" : "L"}${Math.abs(sim.bankAngle).toFixed(0)}°`}</dd>
            <dt>GS</dt><dd>{Math.round(guidanceBackend.groundSpeed)} kt</dd>
            <dt>ALT</dt><dd>{Math.round(guidanceBackend.altitude)} ft → {Math.round(guidance.targetAltitude)}</dd>
            <dt>VS</dt><dd>{signed(Math.round(guidanceBackend.verticalSpeed / 10) * 10)} fpm</dd>
          </dl>
          {guidanceBackend.pinsContinuation?.available && !guidanceBackend.pinsContinuation.active ? <fieldset>
            <legend>{guidanceBackend.pinsContinuation.endpoint.visualSegment.kind === "PROCEED VFR" ? "Proceed VFR" : "Proceed visually"} from the MAP</legend>
            {guidanceBackend.pinsContinuation.endpoint.visualSegment.kind === "PROCEED VFR" ? <label><input type="checkbox" checked={crewConditions.basicVfr}
              onChange={event => setPinsDeclaration({ ...crewConditions, basicVfr: event.target.checked })} />Basic VFR conditions met</label> : <>
              <label><input type="checkbox" checked={crewConditions.landingAreaVisible} onChange={event => setPinsDeclaration({ ...crewConditions, landingAreaVisible: event.target.checked })} />Landing area in sight</label>
              <label><input type="checkbox" checked={crewConditions.publishedVisibility} onChange={event => setPinsDeclaration({ ...crewConditions, publishedVisibility: event.target.checked })} />Published visibility met throughout the visual segment</label>
            </>}
            <p className="fmsBenchHint">Crew declaration required. Follow the published chart and fly the visual segment using heading and altitude controls.</p>
            <button type="button" disabled={guidanceBackend.hasCondition("fmsFail") || !guidanceBackend.pinsContinuation.mapPassed || (guidanceBackend.pinsContinuation.endpoint.visualSegment.kind === "PROCEED VFR" ? !crewConditions.basicVfr : !crewConditions.landingAreaVisible || !crewConditions.publishedVisibility)}
              onClick={() => { if (sim.proceedFromPins(crewConditions)) recordTo?.proceedPins(crewConditions); }}>Continue from MAP</button>
          </fieldset> : null}
        </section>
      </div>

      </FmsStationSurface>
      {instructorAway ? <FmsStationPlaceholder id="instructor" onReturn={() => station.returnSurface("instructor")} /> : null}
      <FmsStationSurface targetWindow={station.windows.instructor} className={`${cockpitView ? "fmsBenchCockpitView " : ""}fmsStationSurfaceInstructor`}>
      <div className="fmsBenchTools" id="fms-instructor" ref={iosPanel} tabIndex={-1}
        role={cockpitView ? "region" : undefined} aria-label={cockpitView ? "Instructor station" : undefined}
        hidden={cockpitView && !iosVisible} onKeyDown={event => {
          if (cockpitView && (!cockpitAway || instructorAway) && event.key === "Escape") { event.preventDefault(); event.stopPropagation(); closeIos(); }
        }}>
        {cockpitView ? <div className="fmsBenchIosHead"><strong>Instructor station · CDU {cduSide}</strong>
          {!cockpitAway && !instructorAway ? <button type="button" aria-pressed={iosExpanded} onClick={() => setIosExpanded(value => !value)}>{iosExpanded ? "Compact instructor" : "More instructor room"}</button> : null}
          {!cockpitAway || instructorAway ? <button type="button" onClick={closeIos}>Close instructor station</button> : null}
          {iosReservesOutside && outside.shown ? <span className="fmsBenchHint fmsBenchOtwCoveredHint">At this width the instructor drawer covers part or all of the outside view. Close the drawer to see the whole view.</span> : null}
          {outsideAway && !cockpitAway && !instructorAway ? <span className="fmsBenchHint">The instructor drawer covers part of the cockpit while open.</span> : null}</div> : null}
        <div className="fmsBenchTabs" role="tablist" aria-label="Bench tools">
          {visibleTabs.map(item => (
            <button key={item.id} type="button" role="tab" id={`fms-bench-tabbutton-${item.id}`} aria-controls={`fms-bench-tab-${item.id}`}
              aria-selected={tab === item.id} tabIndex={tab === item.id ? 0 : -1} onClick={() => chooseTab(item.id)}
              onKeyDown={event => {
                const at = visibleTabs.findIndex(entry => entry.id === tab);
                const step = event.key === "ArrowRight" ? 1 : event.key === "ArrowLeft" ? -1 : 0;
                if (step) { event.preventDefault(); chooseTab(visibleTabs[(at + step + visibleTabs.length) % visibleTabs.length].id); }
              }}>
              {item.label}
            </button>
          ))}
        </div>
        <KeptPanel className="fmsBenchTabPanel fmsBenchCards" role="tabpanel" id="fms-bench-tab-flight" aria-labelledby="fms-bench-tabbutton-flight" shown={tab === "flight" && iosVisible} render={() => (<>
          <section className="fmsBenchCard" aria-label="Flight and setup">
            <h2>Flight and setup</h2>
            <p className="fmsBenchHint">Scripted simulation, not a navigation computer. Restart and jump are engineering actions.</p>
            <p className="fmsBenchProfile">Aircraft profile: <strong>{backend.aircraftProfile.title}</strong> ({backend.aircraftProfile.id} v{backend.aircraftProfile.version}, {profileFingerprint(backend.aircraftProfile)}).</p>
            {cockpitView ? <div className="fmsBenchActions">
            <button type="button" disabled={guidanceBackend.hasCondition("fmsFail")}
              onClick={() => setJumpNote(guidanceBackend.sequence() === "discontinuity" ? "Jump stops at a route discontinuity. Close it on LEGS, or override it (engineering)." : null)}>
              Jump to next waypoint
            </button>
            {next?.kind === "disco" && !guidanceBackend.hasCondition("fmsFail")
              ? <button type="button" onClick={() => { guidanceBackend.overrideDiscontinuity(); setJumpNote("Discontinuity overridden (engineering action, logged)."); }}>Override discontinuity</button>
              : null}
            <button type="button" onClick={reset}>Restart the simulation</button>
            </div> : <p className="fmsBenchHint">The flight controls are beside the displays.</p>}
            {jumpNote ? <p role="status">{jumpNote}</p> : null}
            {cockpitView ? setupControls : null}
            {cockpitView ? <label>CDU inspected <select aria-label="CDU inspected" value={cduSide} disabled={recording || !!runner && !runner.finished}
              onChange={event => setCduSide(Number(event.target.value) as FmsSide)}>
              <option value={1}>FMS 1 / CDU 1</option><option value={2}>FMS 2 / CDU 2</option>
            </select></label> : null}
            <p className="fmsBenchHint">Scenario playback and recording target CDU 1. CDU 2 input is not recorded. EFIS and AFCS follow the separately selected guidance source.</p>
          </section>
        </>)} />
        <KeptPanel className="fmsBenchTabPanel fmsBenchCards" role="tabpanel" id="fms-bench-tab-scenarios" aria-labelledby="fms-bench-tabbutton-scenarios" shown={tab === "scenarios" && iosVisible} render={() => (
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
        )} />
        <KeptPanel className="fmsBenchTabPanel fmsBenchCards" role="tabpanel" id="fms-bench-tab-conditions" aria-labelledby="fms-bench-tabbutton-conditions" shown={tab === "conditions" && iosVisible} render={() => (<>
          <section className="fmsBenchCard">
            <h2>Conditions</h2>
            <ul className="fmsBenchConditions">
              {CONDITIONS.map(condition => (
                <li key={condition.id}>
                  <label>
                    <input type="checkbox" checked={condition.id === "independent" ? !system.linked : conditionBackend(condition.id).hasCondition(condition.id)}
                      disabled={failedFms && condition.id !== "fmsFail" && !sharedSensorCondition(condition.id)}
                      onChange={event => { recordTo?.condition(condition.id, event.target.checked); conditionBackend(condition.id).setCondition(condition.id, event.target.checked); }} />
                    <span>
                      <b>{condition.label}</b> <small className={lampNote(condition.lamp).startsWith("no ") ? "absent" : undefined}>{lampNote(condition.lamp)}</small>
                      {condition.id === "independent" ? <span className="fmsBenchHint">Injects a cross-talk fault. Clearing restores the link; confirm SYNC on SETUP to leave independent operation.</span> : null}
                      <span className="fmsBenchHint">{condition.description}</span>
                    </span>
                  </label>
                </li>
              ))}
            </ul>
            {/* Rev 3 B3.5 F10: not modelled in v1, so not offered; a scenario that injects one is refused at admission. */}
            <p className="fmsBenchHint" data-testid="fms-unmodelled-conditions">
              Not modelled in v1 for aircraft/AFCS: {UNMODELLED_CONDITIONS.map(condition => condition.label.toLowerCase()).join(", ")}. A scenario that injects one is refused. Navigation input validity is controlled in the sensor fault laboratory.
            </p>
          </section>

          <BaroCard backend={backend} sensorOwner={system.computers[0]} recordTo={recordTo} />
          <FmsSensorFaultCard backend={backend} sensorOwner={system.computers[0]} recordTo={recordTo} />

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
        </>)} />
        <KeptPanel className="fmsBenchTabPanel" role="tabpanel" id="fms-bench-tab-gps" aria-labelledby="fms-bench-tabbutton-gps"
          shown={tab === "gps" && iosVisible} render={() => tab === "gps" ? <FmsGpsTab view={fmsGpsView(backend, system.computers[0])} fms={backend} /> : null} />
        <KeptPanel className="fmsBenchTabPanel" role="tabpanel" id="fms-bench-tab-dual" aria-labelledby="fms-bench-tabbutton-dual" shown={tab === "dual" && iosVisible} render={() => (
          <section className="fmsBenchCard" aria-label="Dual computers and radio devices">
            <h2>Dual FMS and civil RMS</h2>
            <label>FMS 2 software profile (restarts the bench) <select aria-label="FMS 2 software profile" value={secondaryProfileChoice.current}
              onChange={event => { secondaryProfileChoice.current = event.target.value; setSession(value => value + 1); }}>
              <option value="">Same as FMS 1</option>{PROFILES.map(profile => <option key={profile.id} value={profile.id}>{profile.title}</option>)}
            </select></label>
            <p className="fmsBenchReadout">{system.mode}, cross-talk {system.linked ? "available" : "lost"}; navigation source {system.navigationSide ? `FMS ${system.navigationSide}` : "each computer independently"}.</p>
            <p className="fmsBenchHint">SETUP 5L requests a mode change; 6R confirms, 6L cancels. Independent RTE 4L sends ACT (empty entry) or SEC to the other computer as MOD; its crew must EXEC. A link restoration leaves both routes independent.</p>
            <button type="button" onClick={() => system.setLinkAvailable(!system.linked)}>{system.linked ? "Fail cross-talk link" : "Restore cross-talk link"}</button>
            <h3>Radio devices</h3>
            <p className="fmsBenchHint">Active tuning follows shared device feedback in both modes. Standby entries cross-talk while the link is available. Laboratory feedback delay {backend.aircraftProfile.parameters.rmsFeedbackDelay.value}s; timeout {backend.aircraftProfile.parameters.rmsFeedbackTimeout.value}s.</p>
            <label>COM1 device feedback <select aria-label="COM1 device feedback" defaultValue="normal" onChange={event => system.rms.injectFailure("com1", event.target.value === "failed")}>
              <option value="normal">Normal</option><option value="failed">No feedback</option>
            </select></label>
            <ul aria-label="RMS tuning feedback">{system.rms.requests.slice(0, 6).map(request => <li key={request.id}>FMS {request.side}: {request.device.toUpperCase()} {request.value} — {request.status}</li>)}</ul>
          </section>
        )} />
        <KeptPanel className="fmsBenchTabPanel fmsBenchCards" role="tabpanel" id="fms-bench-tab-navdata" aria-labelledby="fms-bench-tabbutton-navdata" shown={tab === "navdata" && iosVisible} render={() => (<>
          <section className="fmsBenchCard" aria-label="FMS initialization and preflight">
            <h2>FMS initialization and preflight</h2>
            <p className="fmsBenchReadout">FMS power: {backend.powerState}. Receiver power is controlled on GPS sensors.</p>
            <label><input type="checkbox" checked={restartOnGround} onChange={event => setRestartOnGround(event.target.checked)} /> On ground at power-up (bench input)</label>
            <div className="fmsBenchActions">
              <button type="button" onClick={() => backend.powerOff()}>FMS power off</button>
              <button type="button" onClick={() => backend.powerOn("COLD", restartOnGround)}>Cold start FMS</button>
              <button type="button" onClick={() => backend.powerOn("WARM", restartOnGround)}>Warm start FMS</button>
            </div>
            <p>Review the active data and aircraft configuration, frequencies, position and UTC, route and leg geometry, angle reference, fuel, and satellite deselection before using the demonstration.</p>
            <div className="fmsBenchActions" aria-label="Preflight pages">
              {([['IDENT', 'IDENT'], ['RADIO', 'RADIO'], ['POS', 'POS INIT'], ['RTE', 'ROUTE'], ['LEGS', 'LEGS'], ['SETUP', 'SETUP'], ['FUEL', 'FUEL'], ['SAT_DESELECT', 'SAT DESELECT']] as const).map(([page, label]) => <button type="button" key={page} disabled={failedFms} onClick={() => backend.open(page)}>{label}</button>)}
            </div>
            <p className="fmsBenchReadout">{backend.magvar.database.name}, epoch {backend.magvar.database.epoch}, released {backend.magvar.database.released}, CRC {backend.magvar.database.crc}: {backend.magvar.valid ? backend.magvar.outOfDate(backend.utcTime) ? 'OUT OF DATE' : 'valid checksum' : 'CRC FAILED'}.</p>
            <label>Load magnetic model package <input type="file" accept=".json" aria-label="Load magnetic model package" onChange={async event => {
              const file = event.target.files?.[0]; event.target.value = '';
              if (!file) return;
              try {
                const accepted = backend.loadMagvar(JSON.parse(await file.text()));
                setMagvarLoad(!accepted ? 'Refused: unsupported model package.' : backend.magvar.valid ? `Loaded ${backend.magvar.database.name}.` : 'MAG VAR CRC FAILED: FMS navigation withdrawn.');
              } catch { setMagvarLoad('Refused: invalid JSON.'); }
            }} /></label>
            <button type="button" onClick={() => {
              const url = URL.createObjectURL(new Blob([JSON.stringify(backend.magvar.database, null, 2)], { type: "application/json" }));
              const link = document.createElement("a"); link.href = url; link.download = `fms-magvar-${backend.magvar.database.name}.json`; link.click(); URL.revokeObjectURL(url);
            }}>Export MAGVAR package</button>
            <button type="button" onClick={() => { backend.loadMagvar(WMM2025_DATABASE); setMagvarLoad('Built-in WMM2025 restored.'); }}>Restore WMM2025</button>
            {magvarLoad ? <p role="status">{magvarLoad}</p> : null}
          </section>
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
                current. This start flies LNAV with advisory VNAV under S300, or coupled LPV under the separate later
                CMA/SBAS profile. The invented CYUL demonstration stays the default start.
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
                <button type="button" onClick={() => startDemonstration("87n-rnav190-final")}
                  title="Restarts the simulation under the helicopter profile, loads the FAA CIFP 2609 Copter point-in-space data and places the aircraft 3 NM before STAYS at 1700 ft, NAV and the approach armed">
                  Set up 87N COPTER RNAV 190 final
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
              <span>Load ARINC 424 data (waypoints, navaids, airports, runways, airways, procedures and published RNAV FAS) as the inactive cycle</span>
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
          {approach?.notes?.length ? (
            <section className="fmsBenchCard" aria-label="Procedure notes">
              <h2>Procedure notes</h2>
              <p className="fmsBenchHint">
                {backend.activeRoute.dest} {approach.ident}, from the chart{approachChart ? ` (${approachChart.source})` : ""}: shown for
                reference, never enforced by the bench.
              </p>
              <ul className="fmsBenchReadout" data-testid="fms-procedure-notes">
                {approach.notes.map(note => <li key={note}>{note}</li>)}
              </ul>
            </section>
          ) : null}
          {Object.keys(backend.movingWaypoints).length ? (
            <section className="fmsBenchCard" aria-label="Moving waypoints">
              <h2>Moving waypoints</h2>
              <p className="fmsBenchHint">
                Bench aid: each moving waypoint's age, the simulation time since its position was entered. A moving waypoint
                never expires (plan D-R). In the active route, the FMS's rendezvous with it (M300 11-37).
              </p>
              <ul className="fmsBenchReadout" data-testid="fms-moving-waypoints">
                {Object.entries(backend.movingWaypoints).map(([ident, motion]) => {
                  const age = backend.movingAge(ident);
                  const index = backend.activeRoute.legs.findIndex(leg => leg.kind === "wpt" && leg.ident === ident);
                  const rendezvous = index >= 0 ? backend.rendezvousFor(backend.activeRoute, index) : null;
                  const toGo = rendezvous?.ttg == null ? null : rendezvous.ttg - (backend.now.getTime() - rendezvous.computedAt) / 1000;
                  return (
                    <li key={ident}>
                      <strong>{ident}</strong> {backend.angleText(motion.track)}/{motion.speed} kt, age {age === null ? "unknown" : clockText(age)}
                      {rendezvous ? (rendezvous.achievable ? `; rendezvous in ${toGo === null ? "--" : clockText(Math.max(0, toGo))}, ${rendezvous.distanceNm!.toFixed(1)} NM` : "; rendezvous unachievable") : ""}
                    </li>
                  );
                })}
              </ul>
            </section>
          ) : null}
          <section className="fmsBenchCard" aria-label="User database">
            <h2>User database</h2>
            <p className="fmsBenchReadout">
              {userName ? <>Kept in this browser for <strong>{userName}</strong>, profile {backend.aircraftProfile.id}: </> : <>Not signed in: kept for this session only: </>}
              <strong data-testid="fms-user-db-count">{backend.userWaypoints.length} user waypoints, {backend.userRoutes.length} user routes</strong>.
              Store a waypoint on the CDU (REF NAV DATA or PREDEF WPT 2/2, NEW USER WPT) or save a route on RTE.
            </p>
            {backend.userDatabaseProblem ? <p className="fmsBenchHint" role="alert">{backend.userDatabaseProblem}; it is left as it was and nothing is saved over it.</p> : null}
            <p className="fmsBenchReadout">
              <button type="button" onClick={() => {
                const url = URL.createObjectURL(new Blob([backend.exportUserDatabase()], { type: "application/json" }));
                const link = document.createElement("a");
                link.href = url;
                link.download = `fms-user-database-${backend.aircraftProfile.id}.json`;
                link.click();
                URL.revokeObjectURL(url);
              }}>Export user database</button>
            </p>
            <label className="fmsBenchFile">
              <span>Import a user database (all of it or nothing: a malformed file or a clash with a stored waypoint or route changes nothing)</span>
              <input type="file" accept=".json,application/json" aria-label="User database file"
                onChange={async event => {
                  const file = event.target.files?.[0];
                  if (!file) return;
                  const outcome = backend.importUserDatabase(await file.text());
                  setUserDbStatus("refused" in outcome
                    ? `Refused, nothing changed: ${outcome.refused.join("; ")}.`
                    : `${file.name}: ${outcome.imported.waypoints} user waypoints and ${outcome.imported.routes} user routes added.`);
                  event.target.value = "";
                }} />
            </label>
            {userDbStatus ? <p className="fmsBenchHint" role="status">{userDbStatus}</p> : null}
          </section>
        </>)} />
        <KeptPanel className="fmsBenchTabPanel fmsBenchCards" role="tabpanel" id="fms-bench-tab-lighting" aria-labelledby="fms-bench-tabbutton-lighting" shown={tab === "lighting" && iosVisible} render={() => (<>
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
        </>)} />
      </div>
      </FmsStationSurface>
      </div>
    </main>
  );
}

/**
 * The barometric altitude system (B1.1, baro.ts): the crew's altimeter setting, and the laboratory's declared QNH and
 * injected baro error. The readout shows the physical height beside what the altimeter reads, so an error or a mis-set
 * altimeter is visible for what it is. None of these moves the aircraft directly or changes the radio height.
 */
function BaroCard({ backend, sensorOwner, recordTo }: { backend: ScriptedFms; sensorOwner: ScriptedFms; recordTo: ScenarioRecorder | null }) {
  const [setting, setSetting] = useState("");
  const [qnh, setQnh] = useState("");
  const [error, setError] = useState("");
  const baro = backend.baro;
  const environment = sensorOwner.baro;
  const hpa = (text: string) => Number(text);
  const settingValid = /^\d{3,4}$/.test(setting) && hpa(setting) >= SETTING_RANGE_HPA.min && hpa(setting) <= SETTING_RANGE_HPA.max;
  const qnhValid = /^\d{3,4}$/.test(qnh) && hpa(qnh) >= SETTING_RANGE_HPA.min && hpa(qnh) <= SETTING_RANGE_HPA.max;
  const errorValid = /^-?\d{1,4}$/.test(error) && Math.abs(Number(error)) <= MAX_BARO_ERROR_FT;
  return (
    <section className="fmsBenchCard" aria-label="Barometric altitude">
      <h2>Barometric altitude</h2>
      <p className="fmsBenchReadout" data-testid="baro-readout">
        Physical height <strong>{Math.round(backend.physicalAltitude)} ft</strong>, barometric <strong>{Math.round(backend.altitude)} ft</strong>,
        indicated <strong>{Math.round(backend.indicatedAltitude)} ft</strong> ({formatSetting(baro.setting)}; declared QNH {environment.declaredQnhHpa} hPa;
        baro error {environment.errorFt >= 0 ? "+" : ""}{environment.errorFt} ft).
      </p>
      <form className="fmsBenchAlert" onSubmit={event => { event.preventDefault(); if (!settingValid) return; recordTo?.baro({ setting: hpa(setting) }); backend.setBaroSetting({ kind: "QNH", hPa: hpa(setting) }); setSetting(""); }}>
        <input inputMode="numeric" value={setting} maxLength={4} placeholder="QNH hPa" aria-label="Altimeter setting (QNH, hPa)"
          onChange={event => setSetting(event.target.value.replace(/\D/g, ""))} />
        <button type="submit" disabled={!settingValid}>Set QNH</button>
        <button type="button" aria-pressed={baro.setting.kind === "STD"} onClick={() => { recordTo?.baro({ setting: "STD" }); backend.setBaroSetting({ kind: "STD" }); }}>STD</button>
      </form>
      <p className="fmsBenchHint">The crew's setting changes what the altimeter indicates; the autopilot and the FMS work on the barometric altitude referenced to the declared QNH.</p>
      <form className="fmsBenchAlert" onSubmit={event => { event.preventDefault(); if (!qnhValid) return; recordTo?.baro({ declaredQnh: hpa(qnh) }); sensorOwner.declareQnh(hpa(qnh), "bench"); setQnh(""); }}>
        <input inputMode="numeric" value={qnh} maxLength={4} placeholder="QNH hPa" aria-label="Declared QNH (hPa)"
          onChange={event => setQnh(event.target.value.replace(/\D/g, ""))} />
        <button type="submit" disabled={!qnhValid}>Declare the QNH</button>
      </form>
      <form className="fmsBenchAlert" onSubmit={event => { event.preventDefault(); if (!errorValid) return; recordTo?.baro({ errorFt: Number(error) }); sensorOwner.setBaroError(Number(error), "bench"); setError(""); }}>
        <input inputMode="numeric" value={error} maxLength={5} placeholder="Error ft" aria-label="Baro error (ft)"
          onChange={event => setError(event.target.value.replace(/[^\d-]/g, ""))} />
        <button type="submit" disabled={!errorValid}>Inject the error</button>
      </form>
      <p className="fmsBenchHint">An engineering stimulus, logged: the altimeter reads the physical height plus the error, and the autopilot holding an altitude flies with it, as it would in an aircraft.</p>
    </section>
  );
}
