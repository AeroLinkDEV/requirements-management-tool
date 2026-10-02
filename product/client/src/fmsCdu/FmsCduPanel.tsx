import { memo, useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore, type CSSProperties, type KeyboardEvent } from "react";
import { CDU_ASSETS as ASSETS, type CduKeyEvent, type CduLayout } from "./layout";
import { displayLuminance, screenBrightness, type Lighting } from "./lighting";
import { COLUMNS, type CduBackend, type CduCell, type Lamp } from "./screen";
import { COMPASS_LETTERS, functionFor, legendFor, type CduFunction, type CduVariant } from "./variants";
import { useFmsStationDocument } from "./FmsStationSurface";
import "./FmsCduPanel.css";

const HOLD_MS = 1000;
const DAYLIGHT: Lighting = { mode: "day", ambient: 0.8 };

/** Physical keyboard shortcuts, for engineers driving the panel from a desk. */
function functionForKeyboard(event: KeyboardEvent): CduFunction | null {
  const key = event.key;
  if (/^[a-z0-9]$/i.test(key)) return `CHAR_${key.toUpperCase()}`;
  const lsk = /^F([1-6])$/.exec(key);
  if (lsk) return `LSK${lsk[1] as "1"}${event.shiftKey ? "R" : "L"}`;
  switch (key) {
    case " ": return "SP";
    case "/": return "SLASH";
    case ".": return "DOT";
    case "-": case "+": return "PLUSMINUS";
    case "Backspace": return "CLR";
    case "Enter": return "EXEC";
    case "PageDown": return "NEXT";
    case "PageUp": return "PREV";
    default: return null;
  }
}

const pct = (value: number, total: number) => `${(value / total) * 100}%`;

function Cell({ cell }: { cell: CduCell }) {
  return <span className={`cduCell cdu-${cell.color} cdu-${cell.size}${cell.inverse ? " cduInverse" : ""}`}>{cell.ch === " " ? " " : cell.ch}</span>;
}

const sameCell = (a: CduCell, b: CduCell) => a.ch === b.ch && a.color === b.color && a.size === b.size && a.inverse === b.inverse;
/**
 * One screen line. The screen is rebuilt on every simulation tick, four times a second, but most lines are unchanged:
 * a line is redrawn only when one of its cells is (#1349: rebuilding all 336 cells each tick held the main thread).
 */
const CduLine = memo(function CduLine({ row }: { row: readonly CduCell[] }) {
  return (
    <div className="cduLine" style={{ gridTemplateColumns: `repeat(${COLUMNS}, 1fr)` }}>
      {row.map((cell, column) => <Cell key={column} cell={cell} />)}
    </div>
  );
}, (before, after) => before.row.length === after.row.length && before.row.every((cell, i) => sameCell(cell, after.row[i])));

type Props = {
  backend: CduBackend;
  variant: CduVariant;
  layout: CduLayout;
  onKey?: (event: CduKeyEvent) => void;
  lighting?: Lighting;
};

export default function FmsCduPanel({ backend, variant, layout, onKey, lighting = DAYLIGHT }: Props) {
  // Resolve in the bench owner's JavaScript document before the same faceplate moves to an about:blank child.
  const assetBase = useMemo(() => new URL(ASSETS, document.baseURI).href, []);
  const destinationDocument = useFmsStationDocument();
  const destinationWindow = destinationDocument.defaultView ?? window;
  const subscribe = useCallback((listener: () => void) => backend.subscribe(listener), [backend]);
  const version = useSyncExternalStore(subscribe, () => backend.revision());
  const screen = useMemo(() => backend.screen(), [backend, version]); // eslint-disable-line react-hooks/exhaustive-deps
  const lamps = useMemo(() => backend.lamps(), [backend, version]); // eslint-disable-line react-hooks/exhaustive-deps
  // The display follows the light sensor and BRT together; see lighting.ts.
  const luminance = displayLuminance(backend.brightness(), lighting);
  const [pressed, setPressed] = useState<ReadonlySet<string>>(new Set());
  const holdTimer = useRef<number | null>(null);
  const heldFired = useRef(false);
  const { w: W, h: H } = layout.image;

  const fire = useCallback((keyId: string, held = false) => {
    const fn = functionFor(keyId, variant);
    backend.press(fn, { held });
    onKey?.({ keyId, fn, held, at: new Date() });
  }, [backend, onKey, variant]);

  const down = useCallback((keyId: string) => {
    setPressed(current => new Set(current).add(keyId));
    heldFired.current = false;
    if (functionFor(keyId, variant) === "CLR") {
      // CLR held for more than one second clears the whole scratchpad (Operator's Manual item 15).
      holdTimer.current = destinationWindow.setTimeout(() => { heldFired.current = true; fire(keyId, true); }, HOLD_MS);
    } else fire(keyId);
  }, [fire, variant, destinationWindow]);
  const up = useCallback((keyId: string) => {
    setPressed(current => { const next = new Set(current); next.delete(keyId); return next; });
    if (holdTimer.current !== null) {
      destinationWindow.clearTimeout(holdTimer.current);
      holdTimer.current = null;
      if (!heldFired.current) fire(keyId);
    }
  }, [fire, destinationWindow]);
  /**
   * Abandons every press without firing anything: the key-up may never reach the panel once focus, pointer capture or
   * the page itself has gone, and a CLR hold timer left running would clear the scratchpad after the operator moved
   * away (R18).
   */
  const cancel = useCallback(() => {
    setPressed(new Set());
    if (holdTimer.current !== null) { destinationWindow.clearTimeout(holdTimer.current); holdTimer.current = null; }
  }, [destinationWindow]);
  useEffect(() => {
    const hidden = () => { if (destinationDocument.visibilityState === "hidden") cancel(); };
    destinationDocument.addEventListener("visibilitychange", hidden);
    destinationWindow.addEventListener("blur", cancel);
    return () => { destinationDocument.removeEventListener("visibilitychange", hidden); destinationWindow.removeEventListener("blur", cancel); cancel(); };
  }, [cancel, backend, destinationDocument, destinationWindow]);

  const keyForFunction = useMemo(() => {
    const map = new Map<CduFunction, string>();
    for (const key of layout.keys) map.set(functionFor(key.id, variant), key.id);
    return map;
  }, [layout.keys, variant]);

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    const fn = functionForKeyboard(event);
    const keyId = fn ? keyForFunction.get(fn) : undefined;
    if (!keyId || event.repeat) return;
    event.preventDefault();
    down(keyId);
  };
  const onKeyUp = (event: KeyboardEvent<HTMLDivElement>) => {
    const fn = functionForKeyboard(event);
    const keyId = fn ? keyForFunction.get(fn) : undefined;
    if (keyId) up(keyId);
  };

  const annunciatorLegend = (id: string): string | null => {
    const top = /^A([1-7])$/.exec(id);
    return top ? variant.annunciators[Number(top[1]) - 1] : null;
  };
  const lit = (id: string): boolean => {
    if (id === "MENU_LIGHT") return lamps.has("MENU");
    if (id === "EXEC_LIGHT") return lamps.has("EXEC");
    const legend = annunciatorLegend(id);
    return legend !== null && lamps.has(legend as Lamp);
  };

  const s = layout.screen;
  return (
    <div
      className={`fmsCdu mode-${lighting.mode}`}
      data-luminance={luminance.toFixed(2)}
      style={{ aspectRatio: `${W} / ${H}`, "--cdu-brightness": screenBrightness(luminance) } as CSSProperties}
      tabIndex={0}
      role="group"
      aria-label={`CMA-9000 control display unit, hardware variation ${variant.id}`}
      onKeyDown={onKeyDown}
      onKeyUp={onKeyUp}
      onBlur={cancel}
    >
      <img className="fmsCduImage" src={`${assetBase}panel.webp`} alt="" draggable={false} />

      <div className="fmsCduScreen" role="img" aria-label={screen.map(row => row.map(cell => cell.ch).join("").trimEnd()).join("\n")}
        style={{ left: pct(s.x, W), top: pct(s.y, H), width: pct(s.w, W), height: pct(s.h, H) }}>
        {screen.map((row, line) => <CduLine key={line} row={row} />)}
      </div>

      {layout.annunciators.map(annunciator => {
        const legend = annunciatorLegend(annunciator.id);
        const side = annunciator.id === "MENU_LIGHT" || annunciator.id === "EXEC_LIGHT";
        return (
          <div key={annunciator.id}
            className={`fmsCduLamp${side ? " side" : ""}${lit(annunciator.id) ? " lit" : ""}${legend === "FAIL" || legend === "MSG" ? " caution" : ""}`}
            data-lamp={legend ?? annunciator.id}
            style={{ left: pct(annunciator.x, W), top: pct(annunciator.y, H), width: pct(annunciator.w, W), height: pct(annunciator.h, H) }}>
            {legend}
          </div>
        );
      })}

      <CduKeys keys={layout.keys} variant={variant} pressed={pressed} assetBase={assetBase} width={W} height={H} down={down} up={up} cancel={cancel} />
    </div>
  );
}

type CduKeyProps = {
  physical: CduLayout["keys"][number]; variant: CduVariant; pressed: boolean; assetBase: string; width: number; height: number;
  down: (keyId: string) => void; up: (keyId: string) => void; cancel: () => void;
};
type CduKeysProps = Omit<CduKeyProps, "physical" | "pressed"> & { keys: CduLayout["keys"]; pressed: ReadonlySet<string> };
/** The physical key collection stays unchanged between presses, so ticks need not rebuild its elements. */
const CduKeys = memo(function CduKeys({ keys, variant, pressed, assetBase, width, height, down, up, cancel }: CduKeysProps) {
  return <>{keys.map(key => (
    <CduKey key={key.id} physical={key} variant={variant} pressed={pressed.has(key.id)} assetBase={assetBase} width={width} height={height} down={down} up={up} cancel={cancel} />
  ))}</>;
});
/** One physical key. Its props change only with the variation or its own press, so a simulation tick skips it (#1349). */
const CduKey = memo(function CduKey({ physical: key, variant, pressed, assetBase, width: W, height: H, down, up, cancel }: CduKeyProps) {
  const legend = legendFor(key.id, variant);
  const fn = functionFor(key.id, variant);
  const label = key.kind === "lsk" ? `Line select key ${key.id.slice(3, 4)} ${key.id.endsWith("L") ? "left" : "right"}` : legend.join(" ");
  return (
    <button
      type="button"
      tabIndex={-1}
      className={`fmsCduKey ${key.kind} role-${key.role}${pressed ? " pressed" : ""}`}
      data-key={key.id}
      data-fn={fn}
      aria-label={label}
      style={{
        left: pct(key.x, W), top: pct(key.y, H), width: pct(key.w, W), height: pct(key.h, H),
        backgroundImage: pressed ? `url(${assetBase}pressed.webp)` : undefined,
        backgroundSize: `${(W / key.w) * 100}% ${(H / key.h) * 100}%`,
        backgroundPosition: `${(key.x / (W - key.w)) * 100}% ${(key.y / (H - key.h)) * 100}%`,
      }}
      onPointerDown={event => { event.preventDefault(); event.currentTarget.parentElement?.focus(); event.currentTarget.setPointerCapture(event.pointerId); down(key.id); }}
      onPointerUp={() => up(key.id)}
      onPointerCancel={cancel}
    >
      {key.kind === "lsk" ? <i className={`lskTick ${key.id.endsWith("L") ? "l" : "r"}`} /> : null}
      {legend.length > 0 && (
        <span className={`legend${legend.length > 1 ? " two" : ""}${legend[0].length >= 4 ? " long" : ""}${COMPASS_LETTERS.has(legend[0]) ? " compass" : ""}`}>
          <span>{legend[0]}</span>{legend[1] ? <span className="second">{legend[1]}</span> : null}
        </span>
      )}
    </button>
  );
});
