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

export function fromPlainText(text: string): string {
  return text.trim() ? writeBlocks([{ type: "paragraph", text: text.trim() }]) : emptyRichContent;
}
