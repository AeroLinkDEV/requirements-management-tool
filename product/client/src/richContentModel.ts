/**
 * Authored content is structure, not markup.
 *
 * Nothing here ever produces or consumes HTML. A paragraph is text, a table is rows of text, an image is the
 * identifier of a file this deployment holds — so every value reaches the DOM as a React text node, escaped,
 * and there is no markup to sanitise because none was ever created. That matters more here than in most
 * editors: this content is written by one engineer and read by the approver who signs for it, and an approver
 * whose session can be driven by the content they are approving is a signature that means nothing.
 *
 * The same block list drives the workspace, the generated Word document, and the generated PDF, so the three
 * cannot drift apart.
 */

export type RichBlock =
  | { type: "paragraph"; text: string }
  | { type: "table"; caption?: string; rows: string[][] }
  | { type: "image"; attachmentId: string; alt?: string; caption?: string }
  | { type: "symbol"; value: string }
  | { type: "reference"; label: string; target: string };

export const emptyRichContent = '{"blocks":[]}';

/**
 * Reads stored content. Content written before this model existed is plain text and is adopted as a single
 * paragraph, because refusing to show an approved requirement whose storage format predates the reader would
 * be a defect in the reader, not in the record.
 */
export function readBlocks(stored: string | undefined | null): RichBlock[] {
  const value = (stored ?? "").trim();
  if (!value) return [];
  if (!value.startsWith("{")) return [{ type: "paragraph", text: value }];
  try {
    const parsed = JSON.parse(value) as { blocks?: RichBlock[] };
    return Array.isArray(parsed.blocks) ? parsed.blocks : [{ type: "paragraph", text: value }];
  } catch {
    return [{ type: "paragraph", text: value }];
  }
}

export const writeBlocks = (blocks: RichBlock[]) => JSON.stringify({ blocks });

/**
 * The text exactly as it was typed, for a plain-text editor bound to this model.
 *
 * Distinct from `toPlainText` because an editor and a summary want opposite things. A summary should be
 * tidied; an editor must return every character or the author fights it. Pairing `toPlainText` with
 * `fromPlainText` around a controlled textarea meant each keystroke was normalised and written straight back
 * into the field: a trailing space was trimmed away before the next letter arrived, so `im testing the site`
 * was stored and redisplayed as `imtestingthesite`. Nobody could type a space at all.
 */
export function toEditableText(stored: string | undefined | null): string {
  const blocks = readBlocks(stored)
  if (blocks.length === 0) return ''
  // One paragraph is what `fromPlainText` writes, and its text is returned untouched. Anything else is
  // structured content, which this editor does not own — fall back to the readable form.
  const [first] = blocks
  return blocks.length === 1 && first.type === 'paragraph' ? first.text : toPlainText(stored)
}

/** The readable text, used for word counts, summaries, and anywhere structure cannot be shown. */
export function toPlainText(stored: string | undefined | null): string {
  return readBlocks(stored)
    .map((block) => {
      if (block.type === "paragraph") return block.text;
      if (block.type === "symbol") return block.value;
      if (block.type === "reference") return block.target ? `${block.label} (${block.target})` : block.label;
      if (block.type === "image") return block.caption || block.alt || "";
      return [block.caption ?? "", ...block.rows.map((row) => row.join("\t"))].filter(Boolean).join("\n");
    })
    .filter((line) => line.trim())
    .join("\n")
    .trim();
}

/** True when the content carries anything a plain field could not have carried. */
export function hasStructure(stored: string | undefined | null): boolean {
  const blocks = readBlocks(stored);
  return blocks.length > 1 || blocks.some((block) => block.type !== "paragraph");
}

/**
 * Stores text exactly as given. Deliberately does not trim.
 *
 * This is written on every keystroke of a controlled textarea, so trimming here is not tidying — it is
 * editing the author's input while they are still typing it. Only genuinely empty text becomes empty
 * content; a single space is content, because it is how the next word gets separated from the last.
 * Tidying belongs in `toPlainText`, which is read at the boundaries where a tidy value is wanted.
 */
export function fromPlainText(text: string): string {
  return text === "" ? emptyRichContent : writeBlocks([{ type: "paragraph", text }]);
}
