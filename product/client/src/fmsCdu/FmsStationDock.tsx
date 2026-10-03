import type { StationSurfaceId } from "./useFmsStationWindows";

export type StationArrangement = { preset: "single" | "two" | "three"; instructorApart: boolean };
const STATION_LABELS: Record<StationSurfaceId, string> = {
  outside: "Out the window", cockpit: "Cockpit", instructor: "Instructor station",
};
const STATION_OPEN_LABELS: Record<StationSurfaceId, string> = {
  outside: "Open outside view in a window", cockpit: "Open cockpit in a window", instructor: "Open instructor station in a window",
};

type Props = {
  arrangement: StationArrangement;
  onArrangement: (value: StationArrangement) => void;
  windows: Partial<Record<StationSurfaceId, Window>>;
  opening: StationSurfaceId[];
  errors: Partial<Record<StationSurfaceId, string>>;
  onOpen: (id: StationSurfaceId) => void;
  onReturn: (id: StationSurfaceId) => void;
  onReturnAll: () => void;
};

/** Remembered arrangements express intent. Only the controller's ready windows establish the live dock. */
export function FmsStationDock({ arrangement, onArrangement, windows, opening, errors, onOpen, onReturn, onReturnAll }: Props) {
  const surfaces: StationSurfaceId[] = ["outside", "cockpit", "instructor"];
  const requested: StationSurfaceId[] = [
    ...(arrangement.preset !== "single" ? ["outside" as const] : []),
    ...(arrangement.preset === "three" ? ["cockpit" as const] : []),
    ...(arrangement.instructorApart ? ["instructor" as const] : []),
  ];
  const next = requested.find(id => !windows[id]);
  const count = surfaces.filter(id => windows[id]).length;
  return <details className="fmsStationDock">
    <summary data-fms-station-return-focus>Station windows{count ? ` · ${count} open` : ""}</summary>
    <section className="fmsStationDockPanel" aria-label="Station arrangement">
      <label>Fixed arrangement <select aria-label="Fixed station arrangement" value={arrangement.preset}
        onChange={event => onArrangement({ ...arrangement, preset: event.target.value as StationArrangement["preset"] })}>
        <option value="single">Single screen</option><option value="two">Two screens</option><option value="three">Three screens</option>
      </select></label>
      <label><input type="checkbox" checked={arrangement.instructorApart}
        onChange={event => onArrangement({ ...arrangement, instructorApart: event.target.checked })} />Instructor apart</label>
      <div className="fmsBenchActions">
        <button type="button" disabled={!next || opening.length > 0} onClick={() => { if (next) onOpen(next); }}>
          {opening.length ? "Opening station window…" : next ? STATION_OPEN_LABELS[next] : "Arrangement ready"}
        </button>
        <button type="button" disabled={count === 0 && opening.length === 0} onClick={onReturnAll}>Return all panels</button>
      </div>
      <p className="fmsBenchHint">Choose an arrangement, then open each requested window. Move windows to your screens manually; use your browser's full-screen control.</p>
      <ul className="fmsStationDockList" aria-label="Live station dock">
        {surfaces.map(id => <li key={id}>
          <strong>{STATION_LABELS[id]}</strong>
          <span>{windows[id] ? "In another window" : opening.includes(id) ? "Opening; panel stays here" : "Here"}</span>
          {windows[id] ? <><button type="button" onClick={() => onOpen(id)}>Show {STATION_LABELS[id]}</button>
            <button type="button" onClick={() => onReturn(id)}>Return {STATION_LABELS[id]}</button></> : null}
          {errors[id] ? <p role="status">{errors[id]}</p> : null}
        </li>)}
      </ul>
      <p className="fmsStationOwnerNotice">Keep this bench tab open and visible for continuous updates. Browsers may throttle a hidden tab. Closing this bench tab ends the run and closes its station windows.</p>
    </section>
  </details>;
}

export function FmsStationPlaceholder({ id, onReturn }: { id: StationSurfaceId; onReturn: () => void }) {
  return <div className={`fmsStationPlaceholder fmsStationPlaceholder-${id}`} role="region" aria-label={`${STATION_LABELS[id]} dock`}>
    <strong>{STATION_LABELS[id]} · in another window</strong>
    <button type="button" onClick={onReturn}>Return {STATION_LABELS[id]}</button>
  </div>;
}
