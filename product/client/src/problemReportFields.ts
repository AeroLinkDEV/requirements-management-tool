/**
 * Every narrative field a Problem Report carries, in the order somebody fills them.
 *
 * Declared once and shared, because the create form and the checkout editor showing different fields is
 * the exact defect this replaces: Workaround, Root cause, Effects and Containment existed on the record
 * and on neither form, or on one and not the other. A field added here appears in both or in neither.
 *
 * `plain` is the column that carries the readable projection — what search, the generated documents and
 * every reader that cannot show structure actually read — and travels beside the authored value.
 *
 * The two forms are deliberately still separate components. They persist through different mechanisms:
 * the editor holds an exclusive server lease with a recovery snapshot and an explicit check-in, while
 * creating a report has nothing to lease yet and autosaves to this browser. Forcing one component to do
 * both would mean a lease-shaped component pretending it has no lease half the time. Sharing the field
 * list gets the property that actually matters — the same record, whole, on both.
 */
export const PROBLEM_REPORT_NARRATIVE = [
  { key: "analysisRich", plain: "analysis", label: "Analysis" },
  { key: "rootCauseRich", plain: "rootCause", label: "Root cause" },
  { key: "effectsRich", plain: "effects", label: "Effects" },
  { key: "containmentRich", plain: "containment", label: "Containment" },
  // What can be done in the meantime. Empty is a real answer — it means none has been found.
  { key: "workaroundRich", plain: "workaround", label: "Workaround" },
  { key: "correctiveActionRich", plain: "correctiveAction", label: "Corrective-action narrative" },
  { key: "systemAircraftImpactRich", plain: "systemAircraftImpact", label: "System / aircraft impact" },
] as const;

export type NarrativeField = (typeof PROBLEM_REPORT_NARRATIVE)[number];
export type NarrativeKey = NarrativeField["key"];
