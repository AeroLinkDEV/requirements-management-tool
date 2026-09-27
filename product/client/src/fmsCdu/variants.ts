/**
 * CMA-9000 FMS front-panel hardware variations.
 *
 * Every variation shares the faceplate, the twelve line select keys, the first function row
 * (MENU PREV NEXT INIT/REF RTE DEP/ARR LEGS PROG EXEC), the numeric pad and the alphabetic keys. They differ
 * in the seven annunciators across the top and in the second function row. Source: CMA-9000 Operator's
 * Manual, Figures 2-1 to 2-9 and the key descriptions that follow them (items 13, 14, 17, 20, 22 and 33).
 */

/** What a key does, independent of where a variation puts it. */
export type CduFunction =
  | `LSK${1 | 2 | 3 | 4 | 5 | 6}${"L" | "R"}`
  | "MENU" | "PREV" | "NEXT" | "INIT_REF" | "RTE" | "DEP_ARR" | "LEGS" | "PROG" | "EXEC"
  | "RADIO" | "FUEL" | "MARK" | "HOLD" | "FIX" | "BRT" | "TPDR" | "MSG" | "ANS" | "SQK_IDT"
  | "FMC_COMM" | "VNAV" | "TACT" | "ATC"
  | "CLR" | "SP" | "SLASH" | "DOT" | "PLUSMINUS"
  | `CHAR_${string}`;

export type RowTwoFunction = Extract<CduFunction,
  "RADIO" | "FUEL" | "MARK" | "HOLD" | "FIX" | "BRT" | "TPDR" | "MSG" | "ANS" | "SQK_IDT" | "FMC_COMM" | "VNAV" | "TACT" | "ATC">;

export type AnnunciatorCode = "FAIL" | "MSG" | "POS" | "OFST" | "NPA" | "GSM" | "SMS" | "TX1" | "TX2" | "RNP" | "IND" | "ATC" | "V/UHF" | "HF";

export type CduVariant = {
  /** Hardware variation numbers as the manual prints them. */
  id: string;
  label: string;
  /** Left to right; null is a blank window. */
  annunciators: readonly (AnnunciatorCode | null)[];
  /** Left to right under INIT/REF .. EXEC. */
  rowTwo: readonly RowTwoFunction[];
};

export const CDU_VARIANTS: readonly CduVariant[] = [
  { id: "002/003/005/102/103/302/303/502/503", label: "Standard (FUEL, MARK, FIX)",
    annunciators: ["FAIL", "MSG", "POS", "OFST", "NPA", "GSM", "SMS"], rowTwo: ["RADIO", "FUEL", "MARK", "HOLD", "FIX", "BRT"] },
  { id: "030/430", label: "Transponder and radio transmit (TPDR, TX1, TX2)",
    annunciators: ["FAIL", "MSG", "POS", "OFST", "NPA", "TX1", "TX2"], rowTwo: ["RADIO", "TPDR", "MARK", "HOLD", "MSG", "BRT"] },
  { id: "001/101/301/501", label: "GSM and SMS (ANS, SQK IDT)",
    annunciators: ["FAIL", "MSG", "POS", "OFST", "NPA", "GSM", "SMS"], rowTwo: ["RADIO", "ANS", "SQK_IDT", "HOLD", "FIX", "BRT"] },
  { id: "045/445", label: "Datalink (FMC COMM, VNAV)",
    annunciators: ["FAIL", "MSG", "RNP", "OFST", "IND", null, "ATC"], rowTwo: ["RADIO", "FMC_COMM", "VNAV", "HOLD", "FIX", "BRT"] },
  { id: "050", label: "Tactical with ATC (TACT, VNAV, ATC)",
    annunciators: ["FAIL", "MSG", "RNP", "OFST", "IND", null, "ATC"], rowTwo: ["RADIO", "TACT", "VNAV", "HOLD", "ATC", "BRT"] },
  { id: "060/460", label: "ATC datalink (ATC, FMC COMM, VNAV)",
    annunciators: ["FAIL", "MSG", "RNP", "OFST", "IND", null, "ATC"], rowTwo: ["ATC", "FMC_COMM", "VNAV", "HOLD", "FIX", "BRT"] },
  { id: "070/470", label: "ATC with message recall (ATC, VNAV, MSG)",
    annunciators: ["FAIL", "MSG", "RNP", "OFST", "IND", null, "ATC"], rowTwo: ["RADIO", "ATC", "VNAV", "HOLD", "MSG", "BRT"] },
  { id: "106/506", label: "Tactical (FUEL, MARK, VNAV, TACT)",
    annunciators: ["FAIL", "MSG", "POS", "OFST", "NPA", "GSM", "SMS"], rowTwo: ["RADIO", "FUEL", "MARK", "VNAV", "TACT", "BRT"] },
  { id: "290", label: "V/UHF and HF radios (MSG, HOLD, FIX, MARK)",
    annunciators: ["FAIL", "MSG", "OFST", "POS", "RNP", "V/UHF", "HF"], rowTwo: ["RADIO", "MSG", "HOLD", "FIX", "MARK", "BRT"] },
];

export const DEFAULT_VARIANT_ID = CDU_VARIANTS[0].id;

export const variantById = (id: string | null | undefined): CduVariant =>
  CDU_VARIANTS.find(variant => variant.id === id) ?? CDU_VARIANTS[0];

/** Key legends as printed, one entry per line. The second line of a two-line legend sits lower and smaller. */
export const LEGENDS: Record<string, readonly string[]> = {
  MENU: ["MENU"], PREV: ["PREV"], NEXT: ["NEXT"], INIT_REF: ["INIT", "REF"], RTE: ["RTE"], DEP_ARR: ["DEP", "ARR"],
  LEGS: ["LEGS"], PROG: ["PROG"], EXEC: ["EXEC"], RADIO: ["RADIO"], FUEL: ["FUEL"], MARK: ["MARK"], HOLD: ["HOLD"],
  FIX: ["FIX"], BRT: ["BRT"], TPDR: ["TPDR"], MSG: ["MSG"], ANS: ["ANS"], SQK_IDT: ["SQK", "IDT"], FMC_COMM: ["FMC", "COMM"],
  VNAV: ["VNAV"], TACT: ["TACT"], ATC: ["ATC"], CLR: ["CLR"], SP: ["SP"], SLASH: ["/"], DOT: ["."], PLUSMINUS: ["+/-"],
};

/** Letters whose legend is circled on the panel: the four compass points. */
export const COMPASS_LETTERS = new Set(["E", "N", "S", "W"]);

/** The function a physical key performs on a given variation. Physical ids come from the Blender layout. */
export function functionFor(keyId: string, variant: CduVariant): CduFunction {
  const rowTwo = /^F2_([1-6])$/.exec(keyId);
  if (rowTwo) return variant.rowTwo[Number(rowTwo[1]) - 1];
  if (/^[A-Z0-9]$/.test(keyId)) return `CHAR_${keyId}`;
  return keyId as CduFunction;
}

export function legendFor(keyId: string, variant: CduVariant): readonly string[] {
  if (/^LSK/.test(keyId)) return [];
  const fn = functionFor(keyId, variant);
  if (fn.startsWith("CHAR_")) return [fn.slice(5)];
  return LEGENDS[fn] ?? [fn];
}
