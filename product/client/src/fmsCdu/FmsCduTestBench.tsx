import { useCallback, useMemo, useState } from "react";
import FmsCduPanel from "./FmsCduPanel";
import { useCduLayout, type CduKeyEvent } from "./layout";
import { ScriptedFms } from "./scriptedFms";
import { screenText } from "./screen";
import { CDU_VARIANTS, DEFAULT_VARIANT_ID, variantById } from "./variants";
import "./FmsCduTestBench.css";

const VARIANT_KEY = "aerolink.fmsCdu.variant";

const storedVariant = () => {
  try { return window.localStorage.getItem(VARIANT_KEY) ?? DEFAULT_VARIANT_ID; } catch { return DEFAULT_VARIANT_ID; }
};

type LogEntry = CduKeyEvent & { title: string };

/**
 * An interactive CMA-9000 control display unit for engineers to exercise before, and later with, the real
 * operational program. Today it runs the scripted simulation; the key event log is the seam a future test
 * procedure integration records from.
 */
export default function FmsCduTestBench() {
  const { layout, failed } = useCduLayout();
  const [variantId, setVariantId] = useState(storedVariant);
  const [session, setSession] = useState(0);
  const backend = useMemo(() => new ScriptedFms(), [session]); // eslint-disable-line react-hooks/exhaustive-deps
  const [log, setLog] = useState<LogEntry[]>([]);
  const [alert, setAlert] = useState("");
  const variant = variantById(variantId);

  const chooseVariant = (id: string) => {
    setVariantId(id);
    try { window.localStorage.setItem(VARIANT_KEY, id); } catch { /* a remembered choice is a convenience only */ }
  };

  const onKey = useCallback((event: CduKeyEvent) => {
    const title = screenText(backend.screen())[0].trim();
    setLog(entries => [{ ...event, title }, ...entries].slice(0, 200));
  }, [backend]);

  const reset = () => { setSession(value => value + 1); setLog([]); };

  return (
    <section className="fmsBench">
      <header className="fmsBenchHeader">
        <div>
          <span className="fmsBenchEyebrow">TEST BENCH</span>
          <h1>CMA-9000 FMS control display unit</h1>
          <p>
            A photorealistic, touchable CDU running a <strong>scripted simulation</strong>: key behaviour follows the
            CMA-9000 Operator's Manual, and page values come from a fixed demonstration flight plan. It is not a
            navigation computer, and it is built so the real operational program can drive it later.
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
        <div className="fmsBenchPanel">
          {layout
            ? <FmsCduPanel backend={backend} variant={variant} layout={layout} onKey={onKey} />
            : <p className="fmsBenchLoading" role="status">{failed ? "The CDU model could not be loaded." : "Loading the CDU model…"}</p>}
        </div>

        <aside className="fmsBenchSide">
          <section>
            <h2>Session</h2>
            <form className="fmsBenchAlert" onSubmit={event => { event.preventDefault(); if (alert.trim()) { backend.raiseAlert(alert.trim()); setAlert(""); } }}>
              <input value={alert} maxLength={24} placeholder="Alert message, e.g. UNABLE RNP" aria-label="Alert message to raise"
                onChange={event => setAlert(event.target.value)} />
              <button type="submit" disabled={!alert.trim()}>Raise alert</button>
            </form>
            <button type="button" className="fmsBenchReset" onClick={reset}>Restart the simulation</button>
          </section>

          <section>
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

          <section className="fmsBenchLog">
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
    </section>
  );
}
