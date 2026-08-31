import { useEffect, useId, useMemo, useRef, useState, type ChangeEvent, type CSSProperties, type ReactNode } from "react";
import {
  fromPlainText,
  hasStructure,
  readBlocks,
  toEditableText,
  writeBlocks,
  type RichBlock,
} from "./richContentModel";
import { RichParagraphEditor, RichParagraphView } from "./RichParagraph";
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
  const rendered: ReactNode[] = [];
  for (let index = 0; index < blocks.length; index += 1) {
    const block = blocks[index];
    if (block.type === "image") {
      const images: Extract<RichBlock, { type: "image" }>[] = [];
      while (index < blocks.length && blocks[index].type === "image") {
        images.push(blocks[index] as Extract<RichBlock, { type: "image" }>);
        index += 1;
      }
      index -= 1;
      rendered.push(
        <div className="richImageRow" key={`images-${index}`}>
          {images.map((image, imageIndex) => {
            const widthPercent = Math.min(100, Math.max(25, image.widthPercent ?? 100));
            return (
              <figure className="richImageFigure" key={`${image.attachmentId}-${imageIndex}`} style={{ width: `${widthPercent}%`, flex: `0 1 calc(${widthPercent}% - 6px)` }}>
                {/* The image resolves inside this deployment. There is no path by which authored content can
                    reach out to somewhere else. */}
                <ControlledInlineImage
                  api={api}
                  attachmentId={image.attachmentId}
                  alt={image.alt || "Controlled inline image"}
                />
                {(image.caption || image.alt) && <figcaption>{image.caption || image.alt}</figcaption>}
              </figure>
            );
          })}
        </div>,
      );
      continue;
    }
    if (block.type === "paragraph") rendered.push(<p key={index}><RichParagraphView block={block} /></p>);
    else if (block.type === "symbol")
      rendered.push(
        <blockquote key={index}>
          <span>CONTROLLED SYMBOL</span>
          <b>{block.value}</b>
        </blockquote>,
      );
    else if (block.type === "reference")
      rendered.push(
        <p className="richReference" key={index}>
          <b>{block.label}</b>
          <code>{block.target}</code>
        </p>,
      );
    else
      rendered.push(
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
        </figure>,
      );
  }
  return (
    <div className="richContentView">
      {rendered}
    </div>
  );
}

type EditorProps = {
  api: string;
  projectId: string;
  /** A live controlled checkout binds uploaded figures to the exact record and authorizes collaborative PR editors. */
  editSessionId?: string;
  value: string;
  label: string;
  placeholder?: string;
  disabled?: boolean;
  /** Use document-like narrative controls for Problem Report fields. Other authored surfaces retain the
   * explicit block editor because tables/symbols/references are first-class there. */
  documentLike?: boolean;
  /** Parents use this to keep controlled Save/check-in behind an in-flight image upload. */
  onUploadingChange?: (uploading: boolean) => void;
  onChange: (value: string) => void;
};

export function RichContentEditor({ api, projectId, editSessionId, value, label, placeholder, disabled, documentLike = false, onUploadingChange, onChange }: EditorProps) {
  const blocks = useMemo(() => readBlocks(value), [value]);
  const blocksRef = useRef(blocks);
  const [uploading, setUploading] = useState(false);
  const [error, setError] = useState("");
  const [dragging, setDragging] = useState(false);
  const [focusBlockIndex, setFocusBlockIndex] = useState<number>();
  const editorRoot = useRef<HTMLDivElement>(null);
  const fileInput = useRef<HTMLInputElement>(null);

  useEffect(() => { blocksRef.current = blocks; }, [blocks]);

  useEffect(() => {
    if (focusBlockIndex === undefined) return;
    const target = editorRoot.current?.querySelector<HTMLElement>(
      `[data-rich-block-index="${focusBlockIndex}"] [contenteditable="true"]`,
    );
    if (target) {
      target.focus();
      setFocusBlockIndex(undefined);
    }
  }, [blocks, focusBlockIndex]);

  const commit = (next: RichBlock[]) => { blocksRef.current = next; onChange(writeBlocks(next)); };
  const replace = (index: number, block: RichBlock) =>
    commit(blocks.map((existing, i) => (i === index ? block : existing)));
  const insert = (index: number, block: RichBlock) => {
    const next = [...blocks];
    next.splice(Math.max(0, Math.min(index, next.length)), 0, block);
    commit(next);
  };
  const append = (block: RichBlock) => insert(blocks.length, block);
  const remove = (index: number) => commit(blocks.filter((_, i) => i !== index));
  const move = (index: number, by: number) => {
    const target = index + by;
    if (target < 0 || target >= blocks.length) return;
    const next = [...blocks];
    [next[index], next[target]] = [next[target], next[index]];
    commit(next);
  };

  const imageFiles = (files: FileList | File[]) => Array.from(files).filter(file =>
    file.type === "image/png" || file.type === "image/jpeg");

  const insertionIndex = (target: EventTarget | null) => {
    const element = target instanceof HTMLElement ? target.closest<HTMLElement>("[data-rich-block-index]") : null;
    if (!element) return blocks.length;
    const index = Number(element.dataset.richBlockIndex);
    return Number.isInteger(index) ? (blocks.length === 0 ? 0 : index + 1) : blocks.length;
  };

  const uploadFiles = async (files: File[], at = blocks.length) => {
    if (disabled || !files.length) return;
    setUploading(true);
    onUploadingChange?.(true);
    setError("");
    try {
      const storedBlocks: Extract<RichBlock, { type: "image" }>[] = [];
      for (const file of files) {
        // The server repeats this allowlist and byte-signature check. Rejecting here gives paste/drop the same
        // useful feedback as the file picker and ensures an HTML/SVG drop is never treated as authored content.
        if (file.type !== "image/png" && file.type !== "image/jpeg") continue;
        const body = new FormData();
        body.set("projectId", projectId);
        if (editSessionId) body.set("editSessionId", editSessionId);
        else if (documentLike) body.set("authoringContext", "ProblemReport");
        body.set("file", file);
        body.set("alt", file.name);
        try {
          const response = await fetch(`${api}/api/content/images`, { method: "POST", body });
          if (!response.ok) {
            const detail = (await response.json().catch(() => ({}))) as { error?: string };
            setError(detail.error || "The image could not be stored.");
            continue;
          }
          const stored = (await response.json()) as { id: string };
          storedBlocks.push({ type: "image", attachmentId: stored.id, alt: file.name, caption: "", widthPercent: 100 });
        } catch {
          setError("The image could not be stored. Check the connection and try again.");
        }
      }
      if (storedBlocks.length) {
        // Text edits can arrive while the upload is in flight. Merge into the latest model instead of
        // restoring the array captured when the drop started and silently erasing those edits.
        const next = [...blocksRef.current];
        next.splice(Math.max(0, Math.min(at, next.length)), 0, ...storedBlocks);
        commit(next);
      }
    } finally {
      setUploading(false);
      onUploadingChange?.(false);
    }
  };

  const upload = async (event: ChangeEvent<HTMLInputElement>) => {
    const files = imageFiles(event.target.files ?? []);
    event.target.value = "";
    await uploadFiles(files);
  };

  const pasteImages = (event: React.ClipboardEvent<HTMLDivElement>) => {
    if (disabled) return;
    const files = imageFiles(event.clipboardData.files);
    if (!files.length) return;
    event.preventDefault();
    void uploadFiles(files, insertionIndex(event.target));
  };

  const dropImages = (event: React.DragEvent<HTMLDivElement>) => {
    const droppedFiles = Array.from(event.dataTransfer.files);
    if (!droppedFiles.length) {
      event.preventDefault();
      return;
    }
    event.preventDefault();
    setDragging(false);
    if (disabled) return;
    const files = imageFiles(droppedFiles);
    if (!files.length) {
      setError("Only PNG or JPEG images can be inserted into a Problem Report.");
      return;
    }
    void uploadFiles(files, insertionIndex(event.target));
  };

  const dragOver = (event: React.DragEvent<HTMLDivElement>) => {
    const hasFiles = event.dataTransfer.files.length > 0 || Array.from(event.dataTransfer.items).some(item => item.kind === "file");
    if (!hasFiles) return;
    event.preventDefault();
    if (disabled) return;
    const hasSupportedImage = imageFiles(event.dataTransfer.files).length > 0
      || Array.from(event.dataTransfer.items).some(item => item.type === "image/png" || item.type === "image/jpeg");
    event.dataTransfer.dropEffect = hasSupportedImage ? "copy" : "none";
    setDragging(hasSupportedImage);
  };

  return (
    <div
      ref={editorRoot}
      className={`richEditor${documentLike ? " richEditorDocument" : ""}${dragging ? " is-dragging" : ""}`}
      onPaste={documentLike ? pasteImages : undefined}
      onDragOver={documentLike ? dragOver : undefined}
      onDragLeave={documentLike ? () => setDragging(false) : undefined}
      onDrop={documentLike ? dropImages : undefined}
    >
      <div className="richEditorHead">
        <b>{label}</b>
        {documentLike && <span className="richEditorHint">Write naturally · paste or drop PNG/JPEG figures where they belong</span>}
        <div className="richToolbar" role="group" aria-label={`Add content to ${label}`}>
          {!documentLike && (
            <button type="button" disabled={disabled} onClick={() => append({ type: "paragraph", text: "" })}>Paragraph</button>
          )}
          {!documentLike && <button type="button" disabled={disabled} onClick={() => append({ type: "table", caption: "", rows: [["", ""], ["", ""]] })}>Table</button>}
          <button type="button" disabled={disabled || uploading} onClick={() => fileInput.current?.click()}>
            {uploading ? "Storing…" : documentLike ? "Insert image" : "Image"}
          </button>
          {!documentLike && <button type="button" disabled={disabled} onClick={() => append({ type: "symbol", value: "≤ ≥ ± ° Δ" })}>Symbol</button>}
          {!documentLike && <button type="button" disabled={disabled} onClick={() => append({ type: "reference", label: "Referenced record", target: "REQ-00000001" })}>Reference</button>}
        </div>
      </div>
      <input
        ref={fileInput}
        className="richFileInput"
        type="file"
        accept="image/png,image/jpeg"
        multiple
        onChange={upload}
        tabIndex={-1}
        aria-hidden="true"
      />
      {error && <p className="richError" role="alert">{error}</p>}
      {documentLike && <p className="richDropHint" aria-live="polite">{dragging ? "Drop to place the figure in this narrative." : "Figures stay controlled and appear at the current paragraph."}</p>}

      {blocks.length === 0 && !documentLike && (
        <p className="richEmpty">{placeholder ?? "Add a paragraph, a table, or a figure."}</p>
      )}

      <ol className={`richBlocks${documentLike ? " richBlocksDocument" : ""}`}>
        {documentLike && blocks.length === 0 && (
          <li data-rich-block-index={0} className="richEmptyParagraph">
            <RichParagraphEditor
              block={{ type: "paragraph", text: "" }}
              label={`${label} paragraph 1`}
              disabled={disabled}
              placeholder={placeholder ?? "Describe what happened, what was observed, and why it matters."}
              onChange={(next) => commit([next])}
              onSplit={(before, after) => { commit([before, after]); setFocusBlockIndex(1); }}
            />
          </li>
        )}
        {blocks.map((block, index) => (
          <li key={index} data-rich-block-index={index} className={documentLike && block.type === "paragraph" ? "richDocumentParagraph" : undefined}>
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
              <RichParagraphEditor
                block={block}
                label={`${label} paragraph ${index + 1}`}
                disabled={disabled}
                onChange={(next) => replace(index, next)}
                onSplit={documentLike ? (before, after) => {
                  const next = [...blocks];
                  next.splice(index, 1, before, after);
                  commit(next);
                  setFocusBlockIndex(index + 1);
                } : undefined}
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
                <ControlledInlineImage
                  api={api}
                  attachmentId={block.attachmentId}
                  alt={block.alt || "Stored image"}
                  style={{ width: `${Math.min(100, Math.max(25, block.widthPercent ?? 100))}%` }}
                />
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
                <label className="richImageSize">
                  Figure size <output>{block.widthPercent ?? 100}%</output>
                  <input
                    type="range"
                    min="25"
                    max="100"
                    step="5"
                    value={block.widthPercent ?? 100}
                    disabled={disabled}
                    aria-label={`${label} figure ${index + 1} size`}
                    onChange={(event) => replace(index, { ...block, widthPercent: Number(event.target.value) })}
                  />
                </label>
                {documentLike && <button type="button" className="richInsertBelow" disabled={disabled} onClick={() => insert(index + 1, { type: "paragraph", text: "" })}>Add text below figure</button>}
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

/**
 * An image endpoint can reject a withdrawn or integrity-mismatched controlled attachment. Keep that failure
 * visible in the authored record rather than presenting a broken-image icon as if the figure never existed.
 */
function ControlledInlineImage({
  api,
  attachmentId,
  alt,
  style,
}: {
  api: string;
  attachmentId: string;
  alt: string;
  style?: CSSProperties;
}) {
  const [unavailable, setUnavailable] = useState(false);
  if (unavailable)
    return <p className="richImageUnavailable" role="status" style={style}>Image unavailable in the current record: {alt}</p>;
  return (
    <img
      src={`${api}/api/content/images/${attachmentId}`}
      alt={alt}
      style={style}
      onError={() => setUnavailable(true)}
    />
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
  required = true,
  onChange,
}: {
  api: string;
  projectId: string;
  label: string;
  value: string;
  placeholder: string;
  required?: boolean;
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
        required={required}
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
