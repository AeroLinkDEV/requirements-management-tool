import type { ProcedureEndpoint } from "./navData";

/**
 * What a procedure's chart says that the decoded ARINC 424 fields do not carry: the kind of visual segment after a
 * point-in-space MAP, and notes (restrictions, speed notes, minima). Only charts that have been read are listed; any
 * other procedure's visual segment stays UNKNOWN and unvalidated (Stage C, C.2). The notes are displayed, never
 * enforced (C.10).
 *
 * The 87N entry is from the FAA chart COPTER RNAV (GPS) 190, Southampton, AL-9013 (FAA), Orig-B 10 SEP 20, in d-TPP
 * 2609 (a US Government work in the public domain). The speed notes are the oracle the coded speed limits are checked
 * against (C.5).
 */
export type ProcedureChart = {
  source: string;
  visualSegment: Exclude<ProcedureEndpoint["visualSegment"]["kind"], "RUNWAY" | "UNKNOWN">;
  notes: string[];
};

export const PROCEDURE_CHARTS: Record<string, ProcedureChart> = {
  "87N R190": {
    source: "FAA AL-9013 COPTER RNAV (GPS) 190, Orig-B, d-TPP 2609",
    visualSegment: "PROCEED VFR",
    notes: [
      "RNP APCH.",
      "Procedure NA at night.",
      "Use Westhampton Beach altimeter setting.",
      "Procedure NA for arrival on CCC VOR/DME airway radials 057 CW 105.",
      "Procedure NA for arrivals at HTO VORTAC on V46 eastbound.",
      "Limit final and missed approach to 70K.",
      "Increase to 90K upon reaching the missed approach altitude; maintain 90K while in holding.",
      "Proceed VFR from CRANN or conduct the specified missed approach.",
      "LNAV MDA 560-1.",
    ],
  },
};

/** Departure chart facts absent from HD/PD primary records. The IDF and altitude must still agree with the load. */
export const DEPARTURE_CHARTS: Record<string, { transition: string; fix: string; altitude: string; source: string; visualSegment: "PROCEED VFR" | "PROCEED VISUALLY" }> = {
  "KJRA HUDSN1": { transition: "YOMAN", fix: "HUDSN", altitude: "920A", visualSegment: "PROCEED VFR",
    source: "FAA AL-10972 HUDSN ONE (COPTER RNAV), d-TPP 2609, https://aeronav.faa.gov/d-tpp/2609/10972HUDSN.PDF" },
};
