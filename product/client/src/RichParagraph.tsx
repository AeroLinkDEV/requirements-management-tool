import { useEffect, useRef, useState } from "react";
import { paragraphOf, runsOf, type RichBlock, type RichRun } from "./richContentModel";

/**
 * Emphasis inside a paragraph, read and written without ever touching markup.
 *
 * The content model is deliberately structure rather than markup, because this content is written by one
 * engineer and read by the approver who signs for it, and an approver whose session can be driven by the
 * content they are approving is a signature that means nothing. Inline formatting arrives without giving
 * that up:
 *
 *  - Rendering builds React elements from typed runs. Every character still reaches the DOM as an escaped
 *    text node; there is no `dangerouslySetInnerHTML` anywhere in this file or any other.
 *  - Editing reads the DOM as a *tree*, never as `innerHTML`. Node types map to marks through a closed
 *    allowlist, and any node outside it contributes its `textContent` and nothing else — so a pasted
 *    `<script>`, `<img onerror=…>` or `<a href=javascript:…>` becomes literal text, keeps no attributes,
 *    and cannot survive a round trip.
 *  - Paste is intercepted and read as `text/plain`, so foreign markup does not enter the document in the
 *    first place. The tree walk is the second line of defence, not the only one.
 *
 * Applying a mark is done against the model at character offsets rather than through `execCommand`.
 * `execCommand` was tried first and is the obvious tool, but what it emits depends on the browser and on
 * `styleWithCSS` — Chromium produced `<span style="font-weight:bold">`, which this allowlist correctly
 * ignores, so the emphasis silently did not stick. Splitting runs at the selection is deterministic,
 * behaves the same everywhere, and does not depend on a deprecated API.
 */

const MARKS = [
  { key: "bold", tag: "STRONG", alias: "B", label: "Bold", shortcut: "b", glyph: "B" },
  { key: "italic", tag: "EM", alias: "I", label: "Italic", shortcut: "i", glyph: "I" },
  { key: "underline", tag: "U", alias: "INS", label: "Underline", shortcut: "u", glyph: "U" },
  { key: "code", tag: "CODE", alias: "SAMP", label: "Inline code", shortcut: "e", glyph: "{ }" },
] as const;

type MarkKey = (typeof MARKS)[number]["key"];

const TAG_FOR: Record<MarkKey, "strong" | "em" | "u" | "code"> =
  { bold: "strong", italic: "em", underline: "u", code: "code" };

/** Renders one run as nested React elements — one element per mark it carries, innermost text escaped. */
function RunView({ run }: { run: RichRun }) {
  let node: React.ReactNode = run.text;
  for (const mark of MARKS) {
    if (!run[mark.key]) continue;
    const Tag = TAG_FOR[mark.key];
    node = <Tag>{node}</Tag>;
  }
  return <>{node}</>;
}

export function RichParagraphView({ block }: { block: Extract<RichBlock, { type: "paragraph" }> }) {
  if (!block.runs?.length) return <>{block.text}</>;
  return (
    <>
      {block.runs.map((run, index) => (
        <RunView key={index} run={run} />
      ))}
    </>
  );
}

/**
 * Walks the edited DOM and returns typed runs.
 *
 * The allowlist is the whole security boundary on the read side: an element whose tag is not a known mark
 * contributes its children's text and none of its identity, so markup cannot round-trip through the model
 * even if it somehow reached the document.
 */
function readRuns(root: HTMLElement): RichRun[] {
  const runs: RichRun[] = [];
  const walk = (node: Node, marks: Partial<Record<MarkKey, true>>) => {
    if (node.nodeType === Node.TEXT_NODE) {
      const text = node.textContent ?? "";
      if (text) runs.push({ text, ...marks });
      return;
    }
    if (node.nodeType !== Node.ELEMENT_NODE) return;
    const element = node as HTMLElement;
    // A line break inside a single paragraph is a space; paragraphs are separate blocks in this model.
    if (element.tagName === "BR") { runs.push({ text: " ", ...marks }); return; }
    const inherited = { ...marks };
    for (const mark of MARKS) if (element.tagName === mark.tag || element.tagName === mark.alias) inherited[mark.key] = true;
    for (const child of Array.from(element.childNodes)) walk(child, inherited);
  };
  for (const child of Array.from(root.childNodes)) walk(child, {});
  return runs;
}

/** Builds the editable DOM from runs. Text is set through `createTextNode`, never parsed as markup. */
function writeRuns(root: HTMLElement, runs: RichRun[]) {
  root.replaceChildren();
  for (const run of runs) {
    let node: Node = document.createTextNode(run.text);
    for (const mark of MARKS) {
      if (!run[mark.key]) continue;
      const element = document.createElement(TAG_FOR[mark.key]);
      element.appendChild(node);
      node = element;
    }
    root.appendChild(node);
  }
  if (!root.childNodes.length) root.appendChild(document.createTextNode(""));
}

/** The caret or selection as character offsets into the paragraph's text. */
function readSelection(root: HTMLElement): { start: number; end: number } | undefined {
  const selection = document.getSelection();
  if (!selection?.rangeCount) return undefined;
  const range = selection.getRangeAt(0);
  if (!root.contains(range.startContainer) || !root.contains(range.endContainer)) return undefined;
  const offsetOf = (container: Node, offset: number) => {
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    let total = 0;
    let node = walker.nextNode();
    while (node) {
      if (node === container) return total + offset;
      total += (node.textContent ?? "").length;
      node = walker.nextNode();
    }
    // A container that is the root itself addresses child nodes, not characters.
    return container === root ? total : total;
  };
  const start = offsetOf(range.startContainer, range.startOffset);
  const end = offsetOf(range.endContainer, range.endOffset);
  return start <= end ? { start, end } : { start: end, end: start };
}

/** Puts the caret or selection back at the same character offsets after the document was rebuilt. */
function restoreSelection(root: HTMLElement, at: { start: number; end: number }) {
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  const points: { node: Node; offset: number }[] = [];
  let total = 0;
  let node = walker.nextNode();
  while (node) {
    const length = (node.textContent ?? "").length;
    for (const target of [at.start, at.end])
      if (target >= total && target <= total + length && points.length < 2 + 0)
        points.push({ node, offset: target - total });
    total += length;
    node = walker.nextNode();
  }
  const first = points.find((_, index) => index === 0);
  const second = points.find((_, index) => index === 1) ?? first;
  if (!first || !second) return;
  const range = document.createRange();
  range.setStart(first.node, first.offset);
  range.setEnd(second.node, second.offset);
  const selection = document.getSelection();
  selection?.removeAllRanges();
  selection?.addRange(range);
}

/** Splits runs so that `offset` falls on a run boundary. */
function splitAt(runs: RichRun[], offset: number): RichRun[] {
  const result: RichRun[] = [];
  let seen = 0;
  for (const run of runs) {
    const end = seen + run.text.length;
    if (offset > seen && offset < end) {
      result.push({ ...run, text: run.text.slice(0, offset - seen) });
      result.push({ ...run, text: run.text.slice(offset - seen) });
    } else result.push(run);
    seen = end;
  }
  return result;
}

/**
 * Toggles one mark across a character range. The mark is removed when every character in the range
 * already carries it and applied otherwise, which is how every editor behaves and is what makes a second
 * press of the same button undo the first.
 */
function applyMark(runs: RichRun[], mark: MarkKey, start: number, end: number): RichRun[] {
  const split = splitAt(splitAt(runs, start), end);
  const within = (offset: number, length: number) => offset >= start && offset + length <= end;
  let seen = 0;
  const inRange: RichRun[] = [];
  for (const run of split) {
    if (within(seen, run.text.length)) inRange.push(run);
    seen += run.text.length;
  }
  const removing = inRange.length > 0 && inRange.every((run) => run[mark]);

  seen = 0;
  return split.map((run) => {
    const covered = within(seen, run.text.length);
    seen += run.text.length;
    if (!covered) return run;
    const next: RichRun = { ...run };
    if (removing) delete next[mark];
    else next[mark] = true;
    return next;
  });
}

/**
 * Replaces a character range with plain text, which is what a paste is once its markup has been thrown
 * away. The inserted text takes the marks of the run it lands in, because that is what typing there
 * would have done.
 */
function replaceRange(runs: RichRun[], start: number, end: number, text: string): RichRun[] {
  const split = splitAt(splitAt(runs, start), end);
  const before: RichRun[] = [];
  const after: RichRun[] = [];
  let seen = 0;
  let landing: RichRun | undefined;
  for (const run of split) {
    const finish = seen + run.text.length;
    if (finish <= start) { before.push(run); landing = run; }
    else if (seen >= end) after.push(run);
    seen = finish;
  }
  if (!text) return [...before, ...after];
  const marks: RichRun = { ...(landing ?? { text: "" }), text };
  return [...before, marks, ...after];
}

export function RichParagraphEditor({ block, label, disabled, placeholder, onChange, onSplit }: {
  block: Extract<RichBlock, { type: "paragraph" }>;
  label: string;
  disabled?: boolean;
  placeholder?: string;
  onChange: (block: RichBlock) => void;
  onSplit?: (before: Extract<RichBlock, { type: "paragraph" }>, after: Extract<RichBlock, { type: "paragraph" }>) => void;
}) {
  const host = useRef<HTMLDivElement>(null);
  // What this editor last emitted. Writing the DOM back on every render would move the caret to the start
  // on each keystroke, so the document is only rebuilt when the value changed somewhere other than here.
  const emitted = useRef<string>("");
  const [active, setActive] = useState<MarkKey[]>([]);
  // Whether there is anything to mark. Tracked as state rather than read from the ref during render,
  // because a ref read at render time reports the selection as it was on the previous paint.
  const [selecting, setSelecting] = useState(false);

  useEffect(() => {
    const root = host.current;
    if (!root) return;
    const incoming = JSON.stringify(runsOf(block));
    if (incoming === emitted.current) return;
    writeRuns(root, runsOf(block));
    emitted.current = incoming;
  }, [block]);

  const emit = (runs: RichRun[]) => {
    emitted.current = JSON.stringify(runs.length ? runs : [{ text: "" }]);
    onChange(paragraphOf(runs));
  };

  const refreshActive = () => {
    const root = host.current;
    if (!root) return;
    const at = readSelection(root);
    if (!at || at.start === at.end) { setActive([]); setSelecting(false); return; }
    setSelecting(true);
    const runs = readRuns(root);
    let seen = 0;
    const covered: RichRun[] = [];
    for (const run of runs) {
      if (seen >= at.start && seen + run.text.length <= at.end) covered.push(run);
      seen += run.text.length;
    }
    setActive(MARKS.map((mark) => mark.key).filter((key) => covered.length > 0 && covered.every((run) => run[key])));
  };

  const toggle = (mark: MarkKey) => {
    const root = host.current;
    if (!root || disabled) return;
    const at = readSelection(root);
    // Nothing selected means nothing to mark. A button that silently did nothing would be worse than one
    // that is visibly unavailable, so the toolbar disables itself instead — see `disabled` below.
    if (!at || at.start === at.end) return;
    const next = applyMark(readRuns(root), mark, at.start, at.end);
    writeRuns(root, next);
    restoreSelection(root, at);
    emit(next);
    refreshActive();
  };

  const onBlurToolbar = () => { setActive([]); setSelecting(false); };

  const onKeyDown = (event: React.KeyboardEvent) => {
    if (event.key === "Enter" && !event.shiftKey && onSplit) {
      event.preventDefault();
      const root = host.current;
      if (!root) return;
      const runs = readRuns(root);
      const endOfText = runs.reduce((total, run) => total + run.text.length, 0);
      const at = readSelection(root) ?? { start: endOfText, end: endOfText };
      const start = Math.min(at.start, at.end);
      const end = Math.max(at.start, at.end);
      const left: RichRun[] = [];
      const right: RichRun[] = [];
      let seen = 0;
      for (const run of runs) {
        const finish = seen + run.text.length;
        if (start > seen) left.push({ ...run, text: run.text.slice(0, Math.min(start, finish) - seen) });
        if (end < finish) right.push({ ...run, text: run.text.slice(Math.max(end, seen) - seen) });
        seen = finish;
      }
      onSplit(paragraphOf(left), paragraphOf(right));
      return;
    }
    if (!(event.ctrlKey || event.metaKey)) return;
    const mark = MARKS.find((candidate) => candidate.shortcut === event.key.toLowerCase());
    if (!mark) return;
    event.preventDefault();
    toggle(mark.key);
  };

  return (
    <div className="richParagraphEditor">
      <div className="richMarks" role="group" aria-label={`Emphasis for ${label}`}>
        {MARKS.map((mark) => (
          <button
            key={mark.key}
            type="button"
            // Genuinely unavailable rather than inert: with nothing selected there is nothing to mark,
            // and a control that looks pressable and does nothing is worse than one that says so.
            disabled={disabled || !selecting}
            aria-label={mark.label}
            aria-pressed={active.includes(mark.key)}
            title={selecting ? mark.label : `${mark.label} — select some text first`}
            className={active.includes(mark.key) ? "on" : undefined}
            // The selection is lost the moment the button takes focus, so it never does.
            onMouseDown={(event) => event.preventDefault()}
            onClick={() => toggle(mark.key)}
          >
            {mark.glyph}
          </button>
        ))}
      </div>
      <div
        ref={host}
        className="richParagraphBody"
        role="textbox"
        aria-multiline="true"
        aria-label={label}
        data-placeholder={placeholder ?? "State the requirement, the analysis, or the context."}
        contentEditable={!disabled}
        suppressContentEditableWarning
        onInput={() => { const root = host.current; if (root) emit(readRuns(root)); refreshActive(); }}
        onKeyDown={onKeyDown}
        onKeyUp={refreshActive}
        onMouseUp={refreshActive}
        onSelect={refreshActive}
        onBlur={onBlurToolbar}
        onPaste={(event) => {
          // Image paste is owned by the document composer above this paragraph. Let the event bubble so it
          // can store the controlled file and place it next to this paragraph; treating it as an empty text
          // paste here would swallow the figure before the composer saw it.
          if (Array.from(event.clipboardData.files).some(file => file.type === "image/png" || file.type === "image/jpeg")) return;
          // Read as text. Foreign markup never enters the document, so the tree walk never has to have
          // been the only thing standing between a paste and the record.
          event.preventDefault();
          const root = host.current;
          const text = event.clipboardData.getData("text/plain");
          if (!root || !text) return;
          const at = readSelection(root) ?? { start: 0, end: 0 };
          const next = replaceRange(readRuns(root), at.start, at.end, text);
          writeRuns(root, next);
          restoreSelection(root, { start: at.start + text.length, end: at.start + text.length });
          emit(next);
        }}
        onDragOver={(event) => {
          // The document composer owns image drops. Plain text drops are still handled here as text so a
          // browser never inserts foreign markup directly into this contenteditable surface.
          if (Array.from(event.dataTransfer.items).some(item => item.type === "image/png" || item.type === "image/jpeg")) return;
          event.preventDefault();
          event.dataTransfer.dropEffect = "copy";
        }}
        onDrop={(event) => {
          if (Array.from(event.dataTransfer.files).some(file => file.type === "image/png" || file.type === "image/jpeg")) return;
          event.preventDefault();
          const root = host.current;
          const text = event.dataTransfer.getData("text/plain");
          if (!root || !text) return;
          const at = readSelection(root) ?? { start: 0, end: 0 };
          const next = replaceRange(readRuns(root), at.start, at.end, text);
          writeRuns(root, next);
          restoreSelection(root, { start: at.start + text.length, end: at.start + text.length });
          emit(next);
        }}
      />
    </div>
  );
}
