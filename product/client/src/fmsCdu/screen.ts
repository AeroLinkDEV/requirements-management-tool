import type { AnnunciatorCode, CduFunction } from "./variants";

/** The CMA-9000 display: an 8-colour AMLCD showing 14 lines of 24 characters (line 14 is the scratchpad). */
export const LINES = 14;
export const COLUMNS = 24;
export const SCRATCHPAD_LINE = LINES - 1;

/** The STANDARD colour conventions (Operator's Manual 2-11). */
export type CduColor = "white" | "cyan" | "green" | "magenta" | "amber" | "red";
/** LARGE: entries and navigation-database data. MEDIUM: computed data. SMALL: units and field captions. */
export type CduSize = "large" | "medium" | "small";

export type CduCell = { ch: string; color: CduColor; size: CduSize; inverse: boolean };
export type CduScreen = readonly (readonly CduCell[])[];

export type Lamp = AnnunciatorCode | "MENU" | "EXEC";

/**
 * Everything the panel needs from whatever is driving the display. The scripted simulation implements it
 * today; an adapter to the real CMA-9000 operational program can replace it without touching the panel.
 */
export interface CduBackend {
  press(fn: CduFunction, options?: { held?: boolean }): void;
  screen(): CduScreen;
  lamps(): ReadonlySet<Lamp>;
  /** Display brightness, 0 (darkest usable) to 1. */
  brightness(): number;
  subscribe(listener: () => void): () => void;
  /** Increases whenever the screen, lamps or brightness may have changed; the panel re-reads on change. */
  revision(): number;
}

export type Segment = { text: string; color?: CduColor; size?: CduSize; inverse?: boolean };
export type Line = { left?: Segment | Segment[]; right?: Segment | Segment[]; center?: Segment | Segment[] };

const blank = (): CduCell => ({ ch: " ", color: "white", size: "large", inverse: false });

const segments = (value: Segment | Segment[] | undefined): Segment[] =>
  value === undefined ? [] : Array.isArray(value) ? value : [value];

const width = (list: Segment[]) => list.reduce((sum, segment) => sum + segment.text.length, 0);

function write(row: CduCell[], start: number, list: Segment[]) {
  let column = start;
  for (const segment of list) {
    for (const ch of segment.text) {
      if (column >= 0 && column < COLUMNS)
        row[column] = { ch, color: segment.color ?? "white", size: segment.size ?? "large", inverse: segment.inverse ?? false };
      column += 1;
    }
  }
}

/** Composes a full screen from per-line left, centre and right segments. Text past column 24 is cut off. */
export function compose(lines: readonly (Line | undefined)[]): CduScreen {
  return Array.from({ length: LINES }, (_, index) => {
    const row = Array.from({ length: COLUMNS }, blank);
    const line = lines[index];
    if (!line) return row;
    const left = segments(line.left), right = segments(line.right), center = segments(line.center);
    write(row, 0, left);
    if (center.length) write(row, Math.floor((COLUMNS - width(center)) / 2), center);
    if (right.length) write(row, COLUMNS - width(right), right);
    return row;
  });
}

/** The screen as plain text, one string per line; used by tests and the key event log. */
export const screenText = (screen: CduScreen) => screen.map(row => row.map(cell => cell.ch).join(""));
