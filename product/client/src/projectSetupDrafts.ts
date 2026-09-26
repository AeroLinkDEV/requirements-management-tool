import { apiRequest } from "./apiClient";

export type SetupStep =
  | "Details"
  | "StartingPoint"
  | "Features"
  | "FirstBuild"
  | "Ladder"
  | "WorkingRules"
  | "Services"
  | "Review"
  | "Complete";

export type ProjectSetupDraftSummary = {
  draftId: string;
  state: string;
  currentStep: SetupStep;
  version: number;
  lastSavedAt?: string;
  project: { name: string; softwareProduct: string };
};

const setupSteps = new Set<SetupStep>([
  "Details",
  "StartingPoint",
  "Features",
  "FirstBuild",
  "Ladder",
  "WorkingRules",
  "Services",
  "Review",
  "Complete",
]);

export const isSetupStep = (value: unknown): value is SetupStep =>
  typeof value === "string" && setupSteps.has(value as SetupStep);

/** What the Projects list shows for one discard attempt: it either happened, or it says why it did not. */
export type DiscardSetupOutcome = { ok: true } | { ok: false; message: string };

export const setupDraftDisplayName = (draft: Pick<ProjectSetupDraftSummary, "project">) =>
  draft.project.name.trim() || "Untitled Project";

/**
 * Logically discards one unfinished saved setup.
 *
 * The version the list is showing travels with the request so a stale page cannot discard answers somebody
 * has since saved, and the server refuses a setup that is being finalized or has already completed. This is
 * not Project deletion: the action stops the setup being offered for resume and nothing else is removed. The
 * caller decides what to show; this helper performs exactly one request and never retries a version conflict.
 */
export async function discardProjectSetupDraft(
  api: string,
  draftId: string,
  expectedVersion: number,
): Promise<void> {
  await apiRequest(`${api}/api/project-setups/${draftId}/discard`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ expectedVersion }),
  });
}

const asObject = (value: unknown): Record<string, unknown> =>
  value && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : {};
const isUuid = (value: string) =>
  /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value.trim());

/** Decode the list projection defensively so malformed or partial server data cannot become a link. */
export function decodeProjectSetupDraftSummaries(value: unknown): ProjectSetupDraftSummary[] {
  if (!Array.isArray(value)) return [];
  return value.flatMap((item) => {
    const source = asObject(item);
    const draftId = typeof source.draftId === "string" ? source.draftId.trim() : "";
    const state = typeof source.state === "string" ? source.state : "";
    const currentStep = source.currentStep;
    const project = asObject(source.project);
    const name = typeof project.name === "string" ? project.name : "";
    const softwareProduct =
      typeof project.softwareProduct === "string" ? project.softwareProduct : "";
    const version = source.version;
    if (
      !isUuid(draftId) ||
      (state !== "Draft" && state !== "Finalizing") ||
      !isSetupStep(currentStep) ||
      typeof version !== "number" ||
      !Number.isInteger(version) ||
      version < 1
    )
      return [];
    return [
      {
        draftId,
        state,
        currentStep: currentStep as SetupStep,
        version,
        lastSavedAt: typeof source.lastSavedAt === "string" ? source.lastSavedAt : undefined,
        project: { name, softwareProduct },
      },
    ];
  });
}
