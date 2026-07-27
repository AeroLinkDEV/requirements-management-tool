import { useId, useMemo, useRef, useState, type ChangeEvent } from "react";
import {
  fromPlainText,
  hasStructure,
  readBlocks,
  toEditableText,
  writeBlocks,
  type RichBlock,
} from "./richContentModel";
import "./RichContent.css";

/**
 * The rendering and editing surfaces for authored content. The model itself is in ./richContentModel.
 *
 * That module is not named `richContent` — differing from this file only in case — because Windows and macOS
 * resolve both spellings to one file. TypeScript then collapses the two modules and reports every export of
 * whichever it discarded as missing, so the client compiled on Linux and nowhere else.
 */

export function RichContentView({ api, value, empty }: { api: string; value: string; empty?: string }) {
  const blocks = useMemo(() => readBlocks(value), [value]);
  if (blocks.length === 0)
    return <p className="richEmpty">{empty ?? "No content recorded."}</p>;
  return (
    <div className="richContentView">
      {blocks.map((block, index) => {
        if (block.type === "paragraph") return <p key={index}>{block.text}</p>;
        if (block.type === "symbol")
          return (
            <blockquote key={index}>
              <span>CONTROLLED SYMBOL</span>
              <b>{block.value}</b>
            </blockquote>
          );
        if (block.type === "reference")
          return (
            <p className="richReference" key={index}>
              <b>{block.label}</b>
              <code>{block.target}</code>
            </p>
          );
        if (block.type === "image")
          return (
            <figure key={index}>
              {/* The image resolves inside this deployment. There is no path by which authored content can
                  reach out to somewhere else. */}
              <img src={`${api}/api/content/images/${block.attachmentId}`} alt={block.alt || "Controlled inline image"} />
              {(block.caption || block.alt) && <figcaption>{block.caption || block.alt}</figcaption>}
            </figure>
          );
        return (
          <figure className="richTableFigure" key={index}>
            <div className="richTableScroll">
              <table>
                <tbody>
                  {block.rows.map((row, r) => (
                    <tr key={r}>
                      {row.map((cell, c) =>
                        r === 0 ? <th key={c} scope="col">{cell}</th> : <td key={c}>{cell}</td>,
                      )}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            {block.caption && <figcaption>{block.caption}</figcaption>}
          </figure>
        );
      })}
    </div>
  );
}

type EditorProps = {
  api: string;
  projectId: string;
  value: string;
  label: string;
  placeholder?: string;
  disabled?: boolean;
  onChange: (value: string) => void;
};

export function RichContentEditor({ api, projectId, value, label, placeholder, disabled, onChange }: EditorProps) {
  const blocks = useMemo(() => readBlocks(value), [value]);
  const [uploading, setUploading] = useState(false);
  const [error, setError] = useState("");
  const fileInput = useRef<HTMLInputElement>(null);

  const commit = (next: RichBlock[]) => onChange(writeBlocks(next));
  const replace = (index: number, block: RichBlock) =>
    commit(blocks.map((existing, i) => (i === index ? block : existing)));
  const append = (block: RichBlock) => commit([...blocks, block]);
  const remove = (index: number) => commit(blocks.filter((_, i) => i !== index));
  const move = (index: number, by: number) => {
    const target = index + by;
    if (target < 0 || target >= blocks.length) return;
    const next = [...blocks];
    [next[index], next[target]] = [next[target], next[index]];
    commit(next);
  };

  const upload = async (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    event.target.value = "";
    if (!file) return;
    setUploading(true);
    setError("");
    const body = new FormData();
    body.set("projectId", projectId);
    body.set("file", file);
    body.set("alt", file.name);
    const response = await fetch(`${api}/api/content/images`, { method: "POST", body });
    setUploading(false);
    if (!response.ok) {
      const detail = (await response.json().catch(() => ({}))) as { error?: string };
      setError(detail.error || "The image could not be stored.");
      return;
    }
    const stored = (await response.json()) as { id: string };
    append({ type: "image", attachmentId: stored.id, alt: file.name, caption: "" });
  };

  return (
    <div className="richEditor">
      <div className="richEditorHead">
        <b>{label}</b>
        <div className="richToolbar" role="group" aria-label={`Add content to ${label}`}>
          <button type="button" disabled={disabled} onClick={() => append({ type: "paragraph", text: "" })}>
            Paragraph
          </button>
          <button
            type="button"
            disabled={disabled}
            onClick={() => append({ type: "table", caption: "", rows: [["", ""], ["", ""]] })}
          >
            Table
          </button>
          <button type="button" disabled={disabled || uploading} onClick={() => fileInput.current?.click()}>
            {uploading ? "Storing…" : "Image"}
          </button>
          <button type="button" disabled={disabled} onClick={() => append({ type: "symbol", value: "≤ ≥ ± ° Δ" })}>
            Symbol
          </button>
          <button
            type="button"
            disabled={disabled}
            onClick={() => append({ type: "reference", label: "Referenced record", target: "REQ-00000001" })}
          >
            Reference
          </button>
        </div>
      </div>
      <input
        ref={fileInput}
        className="richFileInput"
        type="file"
        accept="image/png,image/jpeg"
        onChange={upload}
        tabIndex={-1}
        aria-hidden="true"
      />
      {error && <p className="richError" role="alert">{error}</p>}

      {blocks.length === 0 && (
        <p className="richEmpty">{placeholder ?? "Add a paragraph, a table, or a figure."}</p>
      )}

      <ol className="richBlocks">
        {blocks.map((block, index) => (
          <li key={index}>
            <div className="richBlockBar">
              <span>{block.type}</span>
              <div>
                <button type="button" disabled={disabled || index === 0} onClick={() => move(index, -1)} aria-label={`Move ${block.type} up`}>
                  ↑
                </button>
                <button
                  type="button"
                  disabled={disabled || index === blocks.length - 1}
                  onClick={() => move(index, 1)}
                  aria-label={`Move ${block.type} down`}
                >
                  ↓
                </button>
                <button type="button" disabled={disabled} onClick={() => remove(index)} aria-label={`Remove ${block.type}`}>
                  Remove
                </button>
              </div>
            </div>

            {block.type === "paragraph" && (
              <textarea
                value={block.text}
                disabled={disabled}
                placeholder="State the requirement, the analysis, or the context."
                onChange={(event) => replace(index, { ...block, text: event.target.value })}
              />
            )}

            {block.type === "symbol" && (
              <input
                value={block.value}
                disabled={disabled}
                onChange={(event) => replace(index, { ...block, value: event.target.value })}
              />
            )}

            {block.type === "reference" && (
              <div className="richPair">
                <label>
                  Label
                  <input
                    value={block.label}
                    disabled={disabled}
                    onChange={(event) => replace(index, { ...block, label: event.target.value })}
                  />
                </label>
                <label>
                  Controlled record
                  <input
                    value={block.target}
                    disabled={disabled}
                    onChange={(event) => replace(index, { ...block, target: event.target.value })}
                  />
                </label>
              </div>
            )}

            {block.type === "image" && (
              <div className="richImageEdit">
                <img src={`${api}/api/content/images/${block.attachmentId}`} alt={block.alt || "Stored image"} />
                <div className="richPair">
                  <label>
                    Alternative text
                    <input
                      value={block.alt ?? ""}
                      disabled={disabled}
                      placeholder="What the figure shows, for a reader who cannot see it"
                      onChange={(event) => replace(index, { ...block, alt: event.target.value })}
                    />
                  </label>
                  <label>
                    Caption
                    <input
                      value={block.caption ?? ""}
                      disabled={disabled}
                      placeholder="Figure 1 — FMS bus timing"
                      onChange={(event) => replace(index, { ...block, caption: event.target.value })}
                    />
                  </label>
                </div>
              </div>
            )}

            {block.type === "table" && (
              <TableEditor
                block={block}
                disabled={disabled}
                onChange={(next) => replace(index, next)}
              />
            )}
          </li>
        ))}
      </ol>
    </div>
  );
}

function TableEditor({
  block,
  disabled,
  onChange,
}: {
  block: Extract<RichBlock, { type: "table" }>;
  disabled?: boolean;
  onChange: (block: RichBlock) => void;
}) {
  const width = Math.max(1, ...block.rows.map((row) => row.length));
  const setCell = (r: number, c: number, value: string) =>
    onChange({ ...block, rows: block.rows.map((row, i) => (i === r ? row.map((cell, j) => (j === c ? value : cell)) : row)) });
  const addRow = () => onChange({ ...block, rows: [...block.rows, Array(width).fill("")] });
  const addColumn = () => onChange({ ...block, rows: block.rows.map((row) => [...row, ""]) });
  const dropRow = (r: number) =>
    block.rows.length > 1 && onChange({ ...block, rows: block.rows.filter((_, i) => i !== r) });
  const dropColumn = (c: number) =>
    width > 1 && onChange({ ...block, rows: block.rows.map((row) => row.filter((_, j) => j !== c)) });

  return (
    <div className="richTableEdit">
      <label>
        Caption
        <input
          value={block.caption ?? ""}
          disabled={disabled}
          placeholder="Table 1 — Mode parameters"
          onChange={(event) => onChange({ ...block, caption: event.target.value })}
        />
      </label>
      <div className="richTableScroll">
        <table>
          <tbody>
            {block.rows.map((row, r) => (
              <tr key={r}>
                {row.map((cell, c) => (
                  <td key={c}>
                    <input
                      value={cell}
                      disabled={disabled}
                      aria-label={r === 0 ? `Column ${c + 1} heading` : `Row ${r}, column ${c + 1}`}
                      placeholder={r === 0 ? "Heading" : ""}
                      onChange={(event) => setCell(r, c, event.target.value)}
                    />
                  </td>
                ))}
                <td className="richTableAction">
                  <button type="button" disabled={disabled || block.rows.length === 1} onClick={() => dropRow(r)} aria-label={`Remove row ${r + 1}`}>
                    −
                  </button>
                </td>
              </tr>
            ))}
            <tr>
              {Array.from({ length: width }, (_, c) => (
                <td className="richTableAction" key={c}>
                  <button type="button" disabled={disabled || width === 1} onClick={() => dropColumn(c)} aria-label={`Remove column ${c + 1}`}>
                    −
                  </button>
                </td>
              ))}
              <td />
            </tr>
          </tbody>
        </table>
      </div>
      <div className="richTableButtons">
        <button type="button" disabled={disabled} onClick={addRow}>
          Add row
        </button>
        <button type="button" disabled={disabled} onClick={addColumn}>
          Add column
        </button>
      </div>
      <p className="richTableNote">The first row is the heading row in every generated document.</p>
    </div>
  );
}

/**
 * One field of the change case.
 *
 * Most of what an engineer writes here is prose, and prose should cost exactly one textarea — so that is
 * what this is until somebody needs more. The moment they add a table or a figure the field becomes the
 * block editor and stays that way, because a field that flipped back would silently discard the structure.
 */
export function RichCaseField({
  api,
  projectId,
  label,
  value,
  placeholder,
  onChange,
}: {
  api: string;
  projectId: string;
  label: string;
  value: string;
  placeholder: string;
  onChange: (value: string) => void;
}) {
  const id = useId();
  if (hasStructure(value))
    return (
      <div className="pasField">
        <RichContentEditor api={api} projectId={projectId} label={label} value={value} onChange={onChange} />
      </div>
    );
  // The label names the textarea by reference rather than by wrapping it. Wrapping would fold the button
  // below into the field's accessible name, so a screen reader would announce this as "Analysis Add a table
  // or figure" — one control claiming to be two.
  return (
    <div className="pasField">
      <label htmlFor={id}>
        <b>{label}</b>
      </label>
      <textarea
        id={id}
        value={toEditableText(value)}
        onChange={(event) => onChange(fromPlainText(event.target.value))}
        placeholder={placeholder}
        required
      />
      <button
        type="button"
        className="pasStructure"
        onClick={() =>
          onChange(
            writeBlocks([
              ...readBlocks(value),
              { type: "table", caption: "", rows: [["", ""], ["", ""]] },
            ]),
          )
        }
      >
        Add a table or figure
      </button>
    </div>
  );
}
