import { useEffect, useState } from "react";
import { STATUS_FIELDS, type FieldType, type GpsBus, type GpsLabel, type GpsReceiver, type Override, type SatelliteStatus, type Ssm, type StatusLabel, type StatusPatch, type Word } from "./gps";
import { MONITOR_LABELS, alertLimits, lowSatellites, modeLabel, overrideFor, type GpsView } from "./gpsBench";
import type { ScriptedFms } from "./scriptedFms";
import "./FmsGpsTab.css";

/**
 * The GPS sensors tab: the FMS's two simulated CMA-5024 receivers (gps.ts, owned and fed by scriptedFms.ts), which one
 * the FMS navigates on, what each sees and reports, the faults the bench can inject into each, and a live monitor of each
 * output bus with per-word overrides. Every change here is followed by the FMS re-reading the receivers (view.updated).
 */
export default function FmsGpsTab({ view, fms }: { view: GpsView; fms: ScriptedFms }) {
  const [one, two] = view.receivers;
  return (
    <div className="fmsGps">
      <RoutingStrip view={view} fms={fms} />
      <div className="fmsGpsReceivers">
        {[one, two].map((rx, index) => (
          <ReceiverColumn key={index} name={`GPS ${index + 1}`} rx={rx} view={view} index={index as 0 | 1} fms={fms} />
        ))}
      </div>
    </div>
  );
}

const NAV_MODES = new Set(["NAV", "SBAS_NAV", "SBAS_PA", "ALT_AIDING"]);
const feeding = (bus: GpsBus | null) => bus !== null && NAV_MODES.has(bus["273"].value!.mode);
const modeText = (rx: GpsReceiver) => { const bus = rx.bus(); return bus ? modeLabel(bus["273"].value!.mode) : "NO DATA"; };

/**
 * GPS 1 and GPS 2 into the FMS navigation solution, and on to the EFIS: the link of the receiver the FMS navigates on is
 * solid, the other dashed (standby, or not usable). The selector is the FMS's GPS selection (NAV OPTIONS).
 */
function RoutingStrip({ view, fms }: { view: GpsView; fms: ScriptedFms }) {
  const [one, two] = view.receivers;
  const apart = view.difference();
  const nav = fms.navState, chosen = fms.gpsStatus.chosen;
  const source = chosen === null ? nav.mode : `GPS${chosen + 1}`;
  const lastChange = fms.navSourceLog[0];
  const box = (x: number, y: number, w: number, title: string, detail: string, tone: string, id: string) => (
    <g data-testid={id}>
      <rect x={x} y={y} width={w} height={44} rx={8} className={`fmsGpsBox ${tone}`} />
      <text x={x + w / 2} y={y + 18} textAnchor="middle" className="fmsGpsBoxTitle">{title}</text>
      <text x={x + w / 2} y={y + 34} textAnchor="middle" className="fmsGpsBoxDetail">{detail}</text>
    </g>
  );
  const tone = (index: 0 | 1, bus: GpsBus | null) => (chosen === index ? "ok" : feeding(bus) ? "standby" : bus ? "warn" : "fail");
  const role = (index: 0 | 1, bus: GpsBus | null) => (chosen === index ? "in use" : feeding(bus) ? "standby" : "not usable");
  return (
    <section className="fmsBenchCard fmsGpsRouting" aria-label="Sensor routing">
      <h2>Sensor routing</h2>
      <svg viewBox="0 0 640 128" role="img"
        aria-label={`Sensor routing: GPS 1 ${modeText(one)} ${role(0, one.bus())}, GPS 2 ${modeText(two)} ${role(1, two.bus())}, FMS on ${source}`}>
        {box(8, 8, 150, "GPS 1", `${modeText(one)} · ${role(0, one.bus())}`, tone(0, one.bus()), "route-gps1")}
        {box(8, 72, 150, "GPS 2", `${modeText(two)} · ${role(1, two.bus())}`, tone(1, two.bus()), "route-gps2")}
        {box(250, 40, 170, "FMS nav solution", `${source} · ANP ${nav.anp.toFixed(2)}/RNP ${fms.requiredRnp.toFixed(2)}`, chosen === null && nav.mode === "DR" ? "warn" : "ok", "route-fms")}
        {box(512, 40, 120, "EFIS", "PFD · ND", "ok", "route-efis")}
        <path d="M158 30 C 205 30, 205 62, 250 62" className={`fmsGpsLink ${chosen === 0 ? "" : "dashed"}`} data-testid="route-link-gps1" />
        <path d="M158 94 C 205 94, 205 62, 250 62" className={`fmsGpsLink ${chosen === 1 ? "" : "dashed"}`} data-testid="route-link-gps2" />
        <path d="M420 62 L 512 62" className="fmsGpsLink" />
        <text x={172} y={120} className="fmsGpsBoxDetail" data-testid="route-difference">{apart === null ? "GPS 1–GPS 2 Δ —" : `GPS 1–GPS 2 Δ ${apart.toFixed(1)} m`}</text>
      </svg>
      <label className="fmsGpsSelector">
        <span>FMS GPS selection</span>
        <select aria-label="FMS GPS selection" value={view.choice} onChange={event => view.select(event.target.value as GpsView["choice"])}>
          {(["AUTO", "GPS1", "GPS2", "OFF"] as const).map(option => <option key={option}>{option}</option>)}
        </select>
        <small data-testid="route-source-note">
          AUTO navigates on GPS1, then GPS2 when GPS1 is not usable; GPS1 or GPS2 uses only that receiver; OFF deselects GPS
          (the FMS then uses DME/DME, VOR/DME or dead reckoning). {lastChange ? `Last source change: ${lastChange.source} at ${lastChange.at.toISOString().slice(11, 19)}Z.` : ""}
        </small>
      </label>
    </section>
  );
}

/** What the bench has done to one receiver, so its controls and fault chips show it. */
type BenchFaults = {
  /** The satellites below 15° when "mask low satellites" was pressed (a terrain mask snapshot), and ones picked by PRN. */
  lowPrns: number[]; masked: number[]; jamDb: number; satFault: { prn: number; kind: "RAMP" | "STEP"; amount: number } | null;
  doNotUse: boolean; outage: number[]; ionoStorm: number; receiver: boolean; rfInput: boolean; baroLost: boolean; stopped: boolean;
  spoof: { northM: number; driftEastMps: number } | null;
};
const NO_FAULTS: BenchFaults = {
  lowPrns: [], masked: [], jamDb: 0, satFault: null, doNotUse: false, outage: [], ionoStorm: 1, receiver: false, rfInput: false, baroLost: false, stopped: false, spoof: null,
};

function ReceiverColumn({ name, rx, view, index, fms }: { name: string; rx: GpsReceiver; view: GpsView; index: 0 | 1; fms: ScriptedFms }) {
  const [faults, setFaults] = useState<BenchFaults>(NO_FAULTS);
  const bus = rx.bus();
  // The receiver as last computed, even when it has stopped transmitting: the card shows what the unit knows.
  const raw = rx.rawBus();
  // While the GPS integrity condition is on it owns the satellite selection, and clears it when it ends: the bench's
  // masking is replaced, so it is shown as cleared rather than left looking applied.
  const held = view.integrityHeld;
  useEffect(() => {
    if (held) setFaults(current => (current.lowPrns.length || current.masked.length ? { ...current, lowPrns: [], masked: [] } : current));
  }, [held]);

  const apply = (next: BenchFaults) => {
    setFaults(next);
    if (!held) rx.deselect([...new Set([...next.lowPrns, ...next.masked])]);
    rx.setJamming(next.jamDb);
    rx.setSbas({ doNotUse: next.doNotUse, outage: next.outage, ionoStorm: next.ionoStorm });
    rx.injectFault("RECEIVER", next.receiver);
    rx.injectFault("RF_INPUT", next.rfInput);
    rx.injectFault("STOP_TRANSMITTING", next.stopped);
    rx.setSpoof(next.spoof ? { northM: next.spoof.northM, eastM: 0, driftNorthMps: 0, driftEastMps: next.spoof.driftEastMps } : null);
    // setBaroLost re-reads the receivers too.
    view.setBaroLost(index, next.baroLost);
  };
  const setSatFault = (fault: BenchFaults["satFault"]) => {
    if (faults.satFault && faults.satFault.prn !== fault?.prn) rx.satelliteFault(faults.satFault.prn, null);
    if (fault) rx.satelliteFault(fault.prn, fault.kind === "RAMP" ? { kind: "RAMP", metresPerSecond: fault.amount } : { kind: "STEP", metres: fault.amount });
    else if (faults.satFault) rx.satelliteFault(faults.satFault.prn, null);
    setFaults({ ...faults, satFault: fault });
    view.updated();
  };

  return (
    <div className="fmsGpsColumn">
      <ReceiverCard name={name} raw={raw} transmitting={bus !== null} faults={faults} phase={fms.flightPhase} inUse={fms.gpsStatus.chosen === index} />
      <FaultControls name={name} raw={raw} faults={faults} apply={apply} setSatFault={setSatFault} held={held} />
      <BusMonitor name={name} rx={rx} updated={view.updated} />
    </div>
  );
}

const MODE_TONE: Record<string, string> = {
  SBAS_PA: "ok", SBAS_NAV: "ok", NAV: "ok", ALT_AIDING: "warn", ACQUISITION: "warn", INITIALIZATION: "idle", SELF_TEST: "idle", FAULT: "fail",
};

function ReceiverCard({ name, raw, transmitting, faults, phase, inUse }: { name: string; raw: GpsBus; transmitting: boolean; faults: BenchFaults; phase: ScriptedFms["flightPhase"]; inUse: boolean }) {
  const status = raw["273"].value!;
  const sats = raw["060"].map(word => word.value!);
  const sbas = raw["305"].value!;
  const hpl = raw["130"].ssm === "NORMAL" ? raw["130"].value! * 1852 : null;
  const vpl = raw["133"].ssm === "NORMAL" ? raw["133"].value! * 0.3048 : null;
  const limits = alertLimits(phase, sbas.level);
  const chips = faultChips(faults);
  return (
    <section className="fmsBenchCard fmsGpsCard" aria-label={name}>
      <div className="fmsGpsCardHead">
        <h2>{name} <small>CMA-5024 simulation</small></h2>
        <span className={`fmsGpsChip ${MODE_TONE[status.mode] ?? "idle"}`} data-testid="gps-mode">{modeLabel(status.mode)}</span>
        {!transmitting ? <span className="fmsGpsChip fail">NOT TRANSMITTING</span> : null}
        {inUse ? <span className="fmsGpsChip ok" data-testid="gps-in-use">FMS IN USE</span> : null}
      </div>
      <div className="fmsGpsSky">
        <SkyPlot name={name} sats={sats} />
        <Cn0Bars sats={sats.filter(s => s.tracked)} />
      </div>
      <dl className="fmsGpsFigures">
        <dt>Satellites used/visible</dt><dd data-testid="gps-used">{status.used}/{status.visible}</dd>
        <dt>HDOP / VDOP</dt><dd>{number(raw["101"], 2)} / {number(raw["102"], 2)}</dd>
        <dt>HFOM</dt><dd>{raw["247"].ssm === "NORMAL" ? `${(raw["247"].value! * 1852).toFixed(1)} m` : ssmText(raw["247"].ssm)}</dd>
        <dt>HIL / VIL</dt><dd>{hpl === null ? ssmText(raw["130"].ssm) : `${hpl.toFixed(1)} m`} / {vpl === null ? ssmText(raw["133"].ssm) : `${vpl.toFixed(1)} m`}</dd>
        <dt>Approach level</dt><dd data-testid="gps-level">{sbas.level}</dd>
        <dt>SBAS</dt><dd>{sbas.provider ? `${sbas.provider}${sbas.paActive ? " · PA" : ""}` : "not in use"}</dd>
        <dt>RAIM</dt><dd>{status.integrity}</dd>
      </dl>
      <div className="fmsGpsIntegrity" aria-label={`${name} integrity`}>
        <IntegrityBar label="HPL" value={hpl} limit={limits.halM} limitLabel={`HAL ${formatLimit(limits.halM)}`} />
        <IntegrityBar label="VPL" value={vpl} limit={limits.valM} limitLabel={limits.valM === null ? "no VAL" : `VAL ${limits.valM} m`} />
        <small>Limits for the {phase.toLowerCase()} phase{phase === "APPROACH" ? ` at ${sbas.level}` : ""}. Logarithmic scale from 1 m.</small>
      </div>
      <ul className="fmsGpsChips" aria-label={`${name} active faults`}>
        {chips.length ? chips.map(chip => <li key={chip} className="fmsGpsChip fail">{chip}</li>) : <li className="fmsGpsChip idle">No faults injected</li>}
      </ul>
    </section>
  );
}

const formatLimit = (m: number) => (m >= 1852 ? `${(m / 1852).toFixed(0)} NM` : m >= 500 ? `${(m / 1852).toFixed(1)} NM` : `${m} m`);
const ssmText = (ssm: Ssm) => ({ NORMAL: "", NCD: "no data", FT: "test", FW: "fail" }[ssm]);
const number = (word: Word<number>, digits: number) => (word.ssm === "NORMAL" ? word.value!.toFixed(digits) : ssmText(word.ssm));

function faultChips(faults: BenchFaults) {
  const chips: string[] = [];
  if (faults.receiver) chips.push("RECEIVER FAULT");
  if (faults.rfInput) chips.push("RF INPUT");
  if (faults.stopped) chips.push("STOPPED TRANSMITTING");
  if (faults.baroLost) chips.push("BARO LOST");
  if (faults.jamDb > 0) chips.push(`JAM −${faults.jamDb} dB`);
  const masked = new Set([...faults.lowPrns, ...faults.masked]).size;
  if (masked) chips.push(`${masked} MASKED`);
  if (faults.satFault) chips.push(`PRN ${faults.satFault.prn} ${faults.satFault.kind}`);
  if (faults.doNotUse) chips.push("SBAS DO NOT USE");
  if (faults.outage.length) chips.push(`GEO OUT ${faults.outage.join(", ")}`);
  if (faults.ionoStorm > 1) chips.push(`IONO ×${faults.ionoStorm}`);
  if (faults.spoof) chips.push("SPOOFED");
  return chips;
}

/** A polar sky plot: north up, the horizon outside, rings at 30° and 60°, the zenith at the centre. */
function SkyPlot({ name, sats }: { name: string; sats: SatelliteStatus[] }) {
  const c = 100, r = 88;
  const at = (s: SatelliteStatus) => {
    const radius = r * (1 - Math.max(0, s.elevation) / 90), az = (s.azimuth * Math.PI) / 180;
    return { x: c + radius * Math.sin(az), y: c - radius * Math.cos(az) };
  };
  const tone = (s: SatelliteStatus) => (s.excluded ? "excluded" : s.used ? "used" : s.tracked ? "tracked" : "untracked");
  return (
    <svg className="fmsGpsSkyPlot" viewBox="0 0 200 200" role="img" aria-label={`${name} sky plot: ${sats.filter(s => s.used).length} satellites used of ${sats.length} in view`}>
      {[0, 30, 60].map(el => <circle key={el} cx={c} cy={c} r={r * (1 - el / 90)} className="fmsGpsRing" />)}
      <line x1={c} y1={c - r} x2={c} y2={c + r} className="fmsGpsRing" />
      <line x1={c - r} y1={c} x2={c + r} y2={c} className="fmsGpsRing" />
      <text x={c} y={9} textAnchor="middle" className="fmsGpsCompass">N</text>
      {sats.map(s => {
        const p = at(s);
        return (
          <g key={s.prn} className={`fmsGpsSat ${tone(s)}`}>
            {s.sbas ? <rect x={p.x - 5} y={p.y - 5} width={10} height={10} /> : <circle cx={p.x} cy={p.y} r={5} />}
            <text x={p.x + 7} y={p.y + 3}>{s.prn}</text>
          </g>
        );
      })}
    </svg>
  );
}

function Cn0Bars({ sats }: { sats: SatelliteStatus[] }) {
  return (
    <ul className="fmsGpsCn0" aria-label="Signal strength, dB-Hz">
      {sats.length === 0 ? <li className="fmsBenchHint">No satellites tracked.</li> : sats.map(s => (
        <li key={s.prn} title={`PRN ${s.prn}: ${s.cn0.toFixed(1)} dB-Hz`}>
          <span className={`bar ${s.excluded ? "excluded" : s.used ? "used" : "tracked"}`} style={{ height: `${Math.max(4, (s.cn0 - 20) * 2.5)}px` }} />
          <small>{s.prn}</small>
        </li>
      ))}
    </ul>
  );
}

/**
 * A protection level against its alert limit, on a logarithmic scale from 1 m (levels of metres and limits of
 * nautical miles both stay readable): the bar grows to the level, the line marks the limit.
 */
function IntegrityBar({ label, value, limit, limitLabel }: { label: string; value: number | null; limit: number | null; limitLabel: string }) {
  const top = Math.max(limit ?? 100, value ?? 0) * 2;
  const at = (m: number) => Math.min(100, (Math.log10(Math.max(1, m)) / Math.log10(top)) * 100);
  const over = value !== null && limit !== null && value > limit;
  return (
    <div className="fmsGpsBar" role="meter" aria-label={label} aria-valuenow={value ?? undefined} aria-valuemin={1} aria-valuemax={top}
      aria-valuetext={value === null ? `${label} unavailable` : `${label} ${value.toFixed(1)} m, ${limitLabel}${over ? ", exceeded" : ""}`}>
      <span className="fmsGpsBarLabel">{label}</span>
      <span className="fmsGpsBarTrack">
        {value !== null ? <span className={`fmsGpsBarFill ${over ? "over" : ""}`} style={{ width: `${at(value)}%` }} /> : null}
        {limit !== null ? <span className="fmsGpsBarLimit" style={{ left: `${at(limit)}%` }} /> : null}
      </span>
      <span className="fmsGpsBarValue">{value === null ? "—" : `${value.toFixed(1)} m`} <small>{limitLabel}</small></span>
    </div>
  );
}

function FaultControls({ name, raw, faults, apply, setSatFault, held }: {
  name: string; raw: GpsBus; faults: BenchFaults; apply: (next: BenchFaults) => void; setSatFault: (fault: BenchFaults["satFault"]) => void; held: boolean;
}) {
  const gps = raw["060"].map(word => word.value!).filter(s => !s.sbas);
  const [prn, setPrn] = useState<number | "">("");
  const [kind, setKind] = useState<"RAMP" | "STEP">("RAMP");
  const [amount, setAmount] = useState(2);
  const [spoofNorth, setSpoofNorth] = useState(300);
  const [spoofDrift, setSpoofDrift] = useState(0);
  const toggle = (key: "doNotUse" | "receiver" | "rfInput" | "baroLost" | "stopped") => apply({ ...faults, [key]: !faults[key] });
  const check = (key: "doNotUse" | "receiver" | "rfInput" | "baroLost" | "stopped", label: string) => (
    <label className="fmsGpsCheck"><input type="checkbox" checked={faults[key]} onChange={() => toggle(key)} aria-label={`${name} ${label}`} /> {label}</label>
  );
  return (
    <section className="fmsBenchCard fmsGpsFaults" aria-label={`${name} faults`}>
      <h2>{name} faults</h2>
      {held ? (
        <p className="fmsGpsHeld" role="note">
          The GPS integrity lost condition holds this receiver's satellites (five kept, a 200 m step on one): masking is off
          until it ends, and it clears any masking when it does.
        </p>
      ) : null}
      <div className="fmsGpsFaultRow">
        <button type="button" disabled={held} aria-pressed={faults.lowPrns.length > 0} onClick={() => apply({ ...faults, lowPrns: faults.lowPrns.length ? [] : lowSatellites(raw) })}>Mask low satellites (below 15°)</button>
      </div>
      <div className="fmsGpsPrns" role="group" aria-label={`${name} mask satellites`}>
        {gps.map(s => {
          const masked = faults.masked.includes(s.prn);
          return (
            <button key={s.prn} type="button" disabled={held} aria-pressed={masked || faults.lowPrns.includes(s.prn)} title={`Mask PRN ${s.prn} (${s.elevation.toFixed(0)}°)`}
              onClick={() => apply({ ...faults, masked: masked ? faults.masked.filter(p => p !== s.prn) : [...faults.masked, s.prn] })}>{s.prn}</button>
          );
        })}
      </div>
      <label className="fmsGpsSlider">
        <span>Jamming {faults.jamDb} dB</span>
        <input type="range" min={0} max={25} value={faults.jamDb} aria-label={`${name} jamming`} onChange={event => apply({ ...faults, jamDb: Number(event.target.value) })} />
      </label>
      <form className="fmsGpsFaultRow" onSubmit={event => { event.preventDefault(); if (prn !== "") setSatFault({ prn, kind, amount }); }}>
        <select value={prn} aria-label={`${name} faulty satellite`} onChange={event => setPrn(event.target.value === "" ? "" : Number(event.target.value))}>
          <option value="">Satellite…</option>
          {gps.filter(s => s.tracked).map(s => <option key={s.prn} value={s.prn}>PRN {s.prn}</option>)}
        </select>
        <select value={kind} aria-label={`${name} range error kind`} onChange={event => setKind(event.target.value as "RAMP" | "STEP")}>
          <option value="RAMP">Ramp (m/s)</option>
          <option value="STEP">Step (m)</option>
        </select>
        <input type="number" value={amount} step={0.5} aria-label={`${name} range error amount`} onChange={event => setAmount(Number(event.target.value))} />
        <button type="submit" disabled={prn === ""}>Inject</button>
        <button type="button" disabled={!faults.satFault} onClick={() => setSatFault(null)}>Clear</button>
      </form>
      <div className="fmsGpsFaultRow">
        {check("doNotUse", "SBAS do not use")}
        {[131, 133].map(geo => (
          <label key={geo} className="fmsGpsCheck">
            <input type="checkbox" checked={faults.outage.includes(geo)} aria-label={`${name} GEO ${geo} outage`}
              onChange={() => apply({ ...faults, outage: faults.outage.includes(geo) ? faults.outage.filter(p => p !== geo) : [...faults.outage, geo] })} /> GEO {geo} out
          </label>
        ))}
      </div>
      <label className="fmsGpsSlider">
        <span>Ionospheric storm ×{faults.ionoStorm}</span>
        <input type="range" min={1} max={30} value={faults.ionoStorm} aria-label={`${name} ionospheric storm`} onChange={event => apply({ ...faults, ionoStorm: Number(event.target.value) })} />
      </label>
      <div className="fmsGpsFaultRow">
        {check("receiver", "Receiver fault")}
        {check("rfInput", "RF input fault")}
        {check("baroLost", "Baro lost")}
        {check("stopped", "Stop transmitting")}
      </div>
      <form className="fmsGpsFaultRow" onSubmit={event => { event.preventDefault(); apply({ ...faults, spoof: { northM: spoofNorth, driftEastMps: spoofDrift } }); }}>
        <span className="fmsGpsInline">Spoof</span>
        <label>North <input type="number" value={spoofNorth} aria-label={`${name} spoof north offset`} onChange={event => setSpoofNorth(Number(event.target.value))} /> m</label>
        <label>drift east <input type="number" value={spoofDrift} aria-label={`${name} spoof east drift`} onChange={event => setSpoofDrift(Number(event.target.value))} /> m/s</label>
        <button type="submit">Spoof</button>
        <button type="button" disabled={!faults.spoof} onClick={() => apply({ ...faults, spoof: null })}>End</button>
      </form>
      <p className="fmsBenchHint">A spoofed position is consistent across the satellites, so the receiver reports it as valid: only a comparison with GPS 2 or other sensors can catch it.</p>
    </section>
  );
}

const valueText = (word: Word<unknown>) => {
  if (word.value === null) return "—";
  if (typeof word.value === "number") return Math.abs(word.value) >= 1000 || Number.isInteger(word.value) ? word.value.toFixed(0) : word.value.toPrecision(6);
  return Object.entries(word.value as Record<string, unknown>).map(([k, v]) => `${k} ${typeof v === "object" ? JSON.stringify(v) : String(v)}`).join(", ");
};

/** The receiver's output words, live, each with its status; numeric words can be forced, frozen, biased or ramped. */
function BusMonitor({ name, rx, updated }: { name: string; rx: GpsReceiver; updated: () => void }) {
  const [active, setActive] = useState<Partial<Record<GpsLabel, string>>>({});
  const bus = rx.bus();
  const set = (label: GpsLabel, override: Override | null, text: string) => {
    rx.override(label, override);
    updated();
    setActive(current => { const next = { ...current }; if (override) next[label] = text; else delete next[label]; return next; });
  };
  return (
    <details className="fmsBenchCard fmsGpsMonitor">
      <summary>{name} bus monitor {bus ? "" : "(not transmitting)"}</summary>
      <table aria-label={`${name} bus monitor`}>
        <thead><tr><th>Label</th><th>Name</th><th>Value</th><th>SSM</th><th>Override</th></tr></thead>
        <tbody>
          {MONITOR_LABELS.map(({ label, name: title, numeric }) => {
            const word = bus ? (bus[label] as Word<unknown>) : null;
            return (
              <tr key={label} data-label={label}>
                <td>{label}</td>
                <td>{title}</td>
                <td className="value">{word ? valueText(word) : "—"}</td>
                <td>{word ? <span className={`fmsGpsSsm ${word.ssm}`}>{word.ssm}</span> : <span className="fmsGpsSsm FW">SILENT</span>}</td>
                <td>{numeric ? <OverrideForm label={label} active={active[label]} onSet={(o, text) => set(label, o, text)} />
                  : label in STATUS_FIELDS ? <StatusOverrideForm label={label as StatusLabel} rx={rx} updated={updated} /> : <small>{label === "scale" ? "model output" : "read only"}</small>}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </details>
  );
}

function OverrideForm({ label, active, onSet }: { label: GpsLabel; active: string | undefined; onSet: (o: Override | null, text: string) => void }) {
  const [kind, setKind] = useState<Override["kind"]>("BIAS");
  const [amount, setAmount] = useState(0);
  if (active) return <span className="fmsGpsOverride"><b>{active}</b> <button type="button" onClick={() => onSet(null, "")}>Clear</button></span>;
  return (
    <form className="fmsGpsOverride" onSubmit={event => { event.preventDefault(); onSet(overrideFor(kind, amount), kind === "FREEZE" ? "FREEZE" : `${kind} ${amount}`); }}>
      <select value={kind} aria-label={`Override ${label}`} onChange={event => setKind(event.target.value as Override["kind"])}>
        {(["FORCE", "FREEZE", "BIAS", "RAMP"] as const).map(option => <option key={option}>{option}</option>)}
      </select>
      {kind !== "FREEZE" ? <input type="number" value={amount} aria-label={`Override ${label} amount`} onChange={event => setAmount(Number(event.target.value))} /> : null}
      <button type="submit">Set</button>
    </form>
  );
}

/** The fields of a status word as the form offers them: nested ones (355's input buses) as "buses.dme". */
function statusFields(fields: { [field: string]: FieldType }, prefix = ""): { path: string; type: Exclude<FieldType, object> | readonly string[] }[] {
  return Object.entries(fields).flatMap(([field, type]) => (typeof type === "object" && !Array.isArray(type)
    ? statusFields(type as { [field: string]: FieldType }, `${prefix}${field}.`)
    : [{ path: `${prefix}${field}`, type: type as Exclude<FieldType, object> | readonly string[] }]));
}

/**
 * A typed override of a status word (273, 355, 156, 305): pick a field, give it a value of its type, and Set. Fields
 * set one after another add up; Clear removes them all. The receiver validates the patch and refuses an invalid one.
 */
function StatusOverrideForm({ label, rx, updated }: { label: StatusLabel; rx: GpsReceiver; updated: () => void }) {
  const fields = statusFields(STATUS_FIELDS[label]);
  const [path, setPath] = useState(fields[0].path);
  const [text, setText] = useState("");
  const [patch, setPatch] = useState<Record<string, unknown> | null>(null);
  const [refused, setRefused] = useState(false);
  const field = fields.find(entry => entry.path === path)!;
  const choices = Array.isArray(field.type) ? field.type : field.type === "boolean" ? ["true", "false"] : null;
  const value = choices ? (text || choices[0]) : text;
  const typed = field.type === "boolean" ? value === "true" : field.type === "number" ? Number(value) : field.type === "string?" && value === "" ? null : value;
  const submit = () => {
    const [head, tail] = path.split(".");
    const next = { ...(patch ?? {}) };
    next[head] = tail ? { ...((next[head] as object | undefined) ?? {}), [tail]: typed } : typed;
    const ok = rx.overrideStatus(label, next as StatusPatch[typeof label]);
    if (ok) updated();
    setRefused(!ok);
    if (ok) setPatch(next);
  };
  return (
    <form className="fmsGpsOverride" onSubmit={event => { event.preventDefault(); submit(); }}>
      {patch ? <b title={JSON.stringify(patch)}>FORCE {Object.keys(patch).join(", ")}</b> : null}
      <select value={path} aria-label={`Status field ${label}`} onChange={event => { setPath(event.target.value); setText(""); }}>
        {fields.map(entry => <option key={entry.path}>{entry.path}</option>)}
      </select>
      {choices
        ? <select value={value} aria-label={`Status value ${label}`} onChange={event => setText(event.target.value)}>{choices.map(choice => <option key={choice}>{choice}</option>)}</select>
        : <input value={text} type={field.type === "number" ? "number" : "text"} aria-label={`Status value ${label}`} onChange={event => setText(event.target.value)} />}
      <button type="submit">Set</button>
      {patch ? <button type="button" onClick={() => { rx.overrideStatus(label, null); updated(); setPatch(null); setRefused(false); }}>Clear</button> : null}
      {refused ? <small role="status">Refused: not a valid value for that field</small> : null}
    </form>
  );
}

