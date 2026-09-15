import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { AuthUser } from "./IdentityCenter";
import PortalHeader from "./PortalHeader";
import { ApiError, apiRequest, operationError } from "./apiClient";
import { buildVersionOrder, officialBuildName } from "./presentation";
import { isSetupStep, type SetupStep } from "./projectSetupDrafts";
import {
  authorityLabel,
  authorityToken,
  baseRoleAuthorities,
  leadershipAuthorities,
  parseAuthorityToken,
} from "./workflowAuthorities";
import ProjectSetupSourcePanel, {
  SourceAcceptanceFields,
} from "./ProjectSetupSourcePanel";
import { decodeSourceView, sourceFinalizationPayload } from "./projectSetupSource";
import type { SourceDraftState, SourceKind, SourceLadderSuggestion } from "./projectSetupSource";
import "./ProjectSetupWalkthrough.css";

type StartKind = "Fresh" | "AeroLinkBaseline" | "ExternalBaseline";
type VerificationKind = "Case" | "Procedure";
type LadderStep = {
  catalogueEntry: string;
  position: number;
  capabilities: number;
  /**
   * The persisted profile, kept exactly as it was saved. `undefined` means the draft never carried a
   * profile for this level, which is a different fact from an explicitly empty list and from a list this
   * version does not recognize; collapsing them is how a disabled level acquires artifacts nobody chose.
   */
  enabledArtifactKinds?: unknown;
};
type LadderDefinition = { steps: LadderStep[]; relationships: { parent: string; child: string }[] };

/**
 * One server-diagnosed problem, identified by code and by the level and field it belongs to. A review-rule
 * finding names the affected rule subject and, where one stage is at fault, its one-based stage index.
 */
type LadderFinding = {
  code: string;
  level?: string | null;
  field?: string | null;
  message: string;
  token?: string | null;
  subject?: string | null;
  stageIndex?: number | null;
};
type ReadinessStep = {
  level: string;
  capabilities: number;
  stored?: string[] | null;
  effective: string[];
  profileSource: string;
};
type ReadinessReview = {
  applicableSubjects: string[];
  acceptedSubjects: string[];
  missingSubjects: string[];
  unexpectedSubjects: string[];
  duplicateSubjects: string[];
  accepted: boolean;
  definitionConcrete: boolean;
  acceptanceMatchesConfiguration: boolean;
  covers: boolean;
  definitionValid: boolean;
  definitionFindings: LadderFinding[];
};
/**
 * The server's authoritative verdict for one saved configuration. Its scope is the ladder/profile and
 * review-rule compatibility only; administrator authority, unsaved edits, source reconciliation, the
 * source signature and the final transactional gate remain separate facts computed beside it.
 */
type ReadinessView = {
  draftId: string;
  version: number;
  evaluatedConfigurationHash?: string | null;
  ladderValid: boolean;
  steps: ReadinessStep[];
  findings: LadderFinding[];
  review: ReadinessReview;
  configurationReady: boolean;
};
type ReviewStage = {
  name: string;
  kind: "Review" | "Approval";
  requiredRole: string;
  authorityKind: "BaseRole" | "LeadershipPosition" | null;
};
type ReviewRule = { subject: string; name: string; stages: ReviewStage[] };
type ReviewRulesDefinition = { rules: ReviewRule[] };
type RepositoryStatus = "Pending" | "ConfiguredUnverified" | "Verified";
type RepositorySettings = {
  mode: "ConnectNow" | "ConfigureLater";
  status: RepositoryStatus;
  provider: string;
  endpoint: string;
};
type SetupDraft = {
  draftId: string;
  state: string;
  currentStep: SetupStep;
  version: number;
  lastSavedAt?: string;
  project: { name: string; softwareProduct: string };
  start: { kind?: StartKind; sourceBaselineId?: string | null; sourceImportId?: string | null };
  build: { version: string; officialName?: string };
  selectedCategories: string[];
  ladder: unknown;
  reviewRules: { accepted: boolean; acceptanceHash?: string | null; definition?: unknown; suggestedDefinition?: unknown };
  validation?: ReadinessView | null;
  repository: unknown;
  mapping: unknown;
  finalization?: { programId: string; projectId: string; releaseId: string } | null;
};
export type { ProjectSetupDraftSummary } from "./projectSetupDrafts";

type FinalizationResult = {
  state: "Completed";
  alreadyCompleted: boolean;
  programId: string;
  projectId: string;
  releaseId: string;
  version: string;
  officialBuildName: string;
};

type SetupValues = {
  projectName: string;
  softwareProduct: string;
  startKind: StartKind | "";
  sourceBaselineId: string;
  sourceImportId: string;
  buildVersion: string;
  selectedCategories: string[];
  ladder: LadderDefinition;
  reviewRulesDefinition?: ReviewRulesDefinition;
  /** The latest server proposal for the persisted ladder, kept beside any creator edits. */
  suggestedReviewRulesDefinition?: ReviewRulesDefinition;
  reviewRulesAccepted: boolean;
  repository: RepositorySettings;
  mapping: unknown;
};

const emptySourceState = (): SourceDraftState => ({
  source: null,
  assertionAccepted: false,
  password: "",
});

const steps: { id: SetupStep; label: string }[] = [
  { id: "Details", label: "Project details" },
  { id: "StartingPoint", label: "Starting point" },
  { id: "FirstBuild", label: "First build" },
  { id: "Ladder", label: "Requirement ladder" },
  { id: "WorkingRules", label: "Review rules" },
  { id: "Services", label: "Repository" },
  { id: "Review", label: "Review and finish" },
];

const levelCatalogue = [
  {
    id: "System",
    label: "System",
    capabilities: 7,
    verification: ["Procedure"] as VerificationKind[],
  },
  {
    id: "HighLevel",
    label: "High-Level software",
    capabilities: 7,
    verification: ["Case", "Procedure"] as VerificationKind[],
  },
  {
    id: "LowLevel",
    label: "Low-Level software",
    capabilities: 15,
    verification: ["Case", "Procedure"] as VerificationKind[],
  },
  { id: "Customer", label: "Customer", capabilities: 0, verification: [] as VerificationKind[] },
  { id: "Interface", label: "Interface", capabilities: 1, verification: [] as VerificationKind[] },
];

const capabilityLabels = [
  "Change control",
  "Verification",
  "Requirements document",
  "Code traceability",
];
const defaultLadder = (): LadderDefinition => ({
  steps: levelCatalogue.slice(0, 3).map((level, index) => ({
    catalogueEntry: level.id,
    position: index + 1,
    capabilities: level.capabilities,
    enabledArtifactKinds: [...level.verification],
  })),
  relationships: [
    { parent: "System", child: "HighLevel" },
    { parent: "HighLevel", child: "LowLevel" },
  ],
});

const defaultRepository = (): RepositorySettings => ({
  mode: "ConfigureLater",
  status: "Pending",
  provider: "GitLab",
  endpoint: "",
});

const asObject = (value: unknown): Record<string, unknown> =>
  value && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : {};
const isUuid = (value: string) =>
  /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value.trim());

/** Absent and null both mean "the draft carried no profile"; anything else is returned untouched. */
function preservedArtifactProfile(value: unknown): unknown {
  if (value === undefined || value === null) return undefined;
  return Array.isArray(value) ? [...value] : value;
}

/** The artifact kinds this build understands, or undefined when the draft carried no profile at all. */
function recognizedArtifactKinds(step: LadderStep): VerificationKind[] | undefined {
  if (!Array.isArray(step.enabledArtifactKinds)) return undefined;
  return step.enabledArtifactKinds.filter(
    (kind): kind is VerificationKind => kind === "Case" || kind === "Procedure",
  );
}

/** Tokens the draft carries that this build cannot interpret. They are shown, never silently dropped. */
function unrecognizedArtifactTokens(step: LadderStep): string[] {
  if (!Array.isArray(step.enabledArtifactKinds)) return [];
  return step.enabledArtifactKinds.filter(
    (kind): kind is string => typeof kind === "string" && kind !== "Case" && kind !== "Procedure",
  );
}

/** A profile the server could not read as a list at all. Kept verbatim so a save cannot rewrite it. */
function artifactProfileIsMalformed(step: LadderStep): boolean {
  return step.enabledArtifactKinds !== undefined && !Array.isArray(step.enabledArtifactKinds);
}

function hasVerificationCapability(step: LadderStep) {
  return (step.capabilities & 2) !== 0;
}

/**
 * Every artifact token the draft actually stores for a level — recognized or not — or undefined when the
 * draft carries no profile at all. Keeping this separate from the effective interpretation is what stops a
 * disabled capability from being described as "nothing is enabled" while artifacts are still stored.
 */
function savedArtifactTokens(step: LadderStep): string[] | undefined {
  if (!Array.isArray(step.enabledArtifactKinds)) return undefined;
  return step.enabledArtifactKinds.filter((kind): kind is string => typeof kind === "string");
}

/**
 * The saved profile, but only when it is exactly one of the profiles this level supports. Re-enabling
 * verification may restore an actual prior choice; it must never derive a compatible-looking choice by
 * filtering an invalid one.
 */
function compatibleRememberedProfile(step: LadderStep, catalogueId: string): VerificationKind[] | undefined {
  if (artifactProfileIsMalformed(step) || unrecognizedArtifactTokens(step).length > 0) return undefined;
  const kinds = recognizedArtifactKinds(step);
  if (!kinds || kinds.length === 0) return undefined;
  const allowed: VerificationKind[][] =
    catalogueId === "System" ? [["Procedure"]] : [["Case"], ["Case", "Procedure"]];
  return allowed.some(
    (candidate) => candidate.length === kinds.length && candidate.every((kind) => kinds.includes(kind)),
  )
    ? kinds
    : undefined;
}

/** Diagnostic tokens are bounded before they are shown; the stored value itself is never altered. */
const displayTokenLimit = 40;
const displayTokenCount = 3;

function boundedTokenList(tokens: string[]): string {
  const shown = tokens
    .slice(0, displayTokenCount)
    .map((token) => (token.length > displayTokenLimit ? `${token.slice(0, displayTokenLimit)}…` : token));
  const remaining = tokens.length - shown.length;
  return `${shown.join(", ")}${remaining > 0 ? ` and ${remaining} more` : ""}`;
}

/** The contradiction the final gate refuses: verification is off while the profile still enables artifacts. */
function disabledVerificationWithArtifacts(step: LadderStep): boolean {
  return !hasVerificationCapability(step) && (savedArtifactTokens(step)?.length ?? 0) > 0;
}

/** Verification is on, but the saved profile is empty, unreadable, or carries tokens this build cannot use. */
function enabledVerificationProfileInvalid(step: LadderStep): boolean {
  if (!hasVerificationCapability(step)) return false;
  if (artifactProfileIsMalformed(step)) return true;
  const kinds = recognizedArtifactKinds(step);
  return (kinds !== undefined && kinds.length === 0) || unrecognizedArtifactTokens(step).length > 0;
}

function profileSelection(step: LadderStep): "" | "Case" | "Case+Procedure" {
  const kinds = recognizedArtifactKinds(step);
  if (!kinds || kinds.length === 0) return "";
  return kinds.includes("Procedure") ? "Case+Procedure" : "Case";
}

function levelLabel(levelId: string) {
  return levelCatalogue.find((level) => level.id === levelId)?.label ?? levelId;
}

function capabilitySummary(step: LadderStep) {
  const labels = capabilityLabels.filter((_, index) => (step.capabilities & (1 << index)) !== 0);
  return labels.length ? labels.join(", ") : "No capabilities";
}

/**
 * What a level verifies, said plainly — including a stored contradiction that has not been repaired yet.
 * The selected capability, the saved artifacts and their effective interpretation are separate facts, and
 * a level whose profile is invalid is never described as if it had a valid one.
 */
function verificationSummary(step: LadderStep, verdict?: ReadinessStep) {
  const saved = savedArtifactTokens(step);
  if (!hasVerificationCapability(step)) {
    if (saved && saved.length)
      return `Verification disabled — but the saved profile still enables ${boundedTokenList(
        saved,
      )}. This contradiction must be repaired before the Project can be created.`;
    if (artifactProfileIsMalformed(step))
      return "Verification disabled — the saved profile is not a list of artifact kinds and cannot be kept as a valid choice.";
    return "Verification disabled — no verification artifacts are enabled.";
  }
  const effective = verdict?.effective ?? recognizedArtifactKinds(step);
  const stated = effective && effective.length ? effective.join(" + ") : "profile not yet chosen";
  const notes: string[] = [];
  if (verdict?.profileSource === "catalogue-fallback")
    notes.push("maintained default for an unspecified saved profile");
  if (unrecognizedArtifactTokens(step).length > 0)
    notes.push(`unrecognized saved tokens: ${boundedTokenList(unrecognizedArtifactTokens(step))}`);
  if (artifactProfileIsMalformed(step)) notes.push("the saved profile is not a list of artifact kinds");
  if (effective !== undefined && effective.length === 0 && notes.length === 0)
    notes.push("incomplete configuration, not a default");
  return `Verification enabled · ${stated}${notes.length ? ` (${notes.join("; ")})` : ""}`;
}

/** What the draft stores for this level, said without implying the stored value is usable. */
function savedArtifactsLabel(step: LadderStep): string {
  if (artifactProfileIsMalformed(step)) return "not a list of artifact kinds (kept as saved)";
  const saved = savedArtifactTokens(step);
  if (saved === undefined) return "none recorded";
  if (saved.length === 0) return "none selected";
  return boundedTokenList(saved);
}

/** The maintained interpretation of the saved answer, which is a different fact from the saved answer. */
function effectiveArtifactsLabel(step: LadderStep, verdict?: ReadinessStep): string {
  if (!hasVerificationCapability(step))
    return (savedArtifactTokens(step)?.length ?? 0) > 0
      ? "no verification artifacts (the capability is disabled)"
      : "none";
  const effective = verdict?.effective ?? recognizedArtifactKinds(step);
  const label = effective && effective.length ? effective.join(" + ") : "none yet";
  return verdict?.profileSource === "catalogue-fallback"
    ? `${label} (maintained default for an unspecified saved profile)`
    : label;
}

function normalizeLadder(value: unknown): LadderDefinition {
  const source = asObject(value);
  const rawSteps = Array.isArray(source.steps) ? source.steps : [];
  const parsed = rawSteps.flatMap((item, index) => {
    const row = asObject(item);
    const catalogueEntry = typeof row.catalogueEntry === "string" ? row.catalogueEntry : "";
    const catalogue = levelCatalogue.find((level) => level.id === catalogueEntry);
    if (!catalogue) return [];
    return [
      {
        catalogueEntry,
        position: typeof row.position === "number" ? row.position : index + 1,
        capabilities:
          typeof row.capabilities === "number" ? row.capabilities : catalogue.capabilities,
        // Saved intent is preserved verbatim: an absent profile stays absent, an explicit list — empty,
        // valid or unrecognized — stays exactly as the server stored it. Only the server decides what an
        // absent profile means, and it says so in the readiness verdict.
        enabledArtifactKinds: preservedArtifactProfile(row.enabledArtifactKinds),
      },
    ];
  });
  if (!parsed.length) return defaultLadder();
  const known = new Set(parsed.map((step) => step.catalogueEntry));
  const relationships = (Array.isArray(source.relationships) ? source.relationships : []).flatMap(
    (item) => {
      const row = asObject(item);
      const parent = typeof row.parent === "string" ? row.parent : "";
      const child = typeof row.child === "string" ? row.child : "";
      return known.has(parent) && known.has(child) ? [{ parent, child }] : [];
    },
  );
  return {
    steps: parsed.map((step, index) => ({ ...step, position: index + 1 })),
    relationships,
  };
}

function normalizeRepository(value: unknown): RepositorySettings {
  const source = asObject(value);
  const mode = source.mode === "ConnectNow" ? "ConnectNow" : "ConfigureLater";
  const status: RepositoryStatus =
    source.status === "Verified"
      ? "Verified"
      : source.status === "ConfiguredUnverified" || source.status === "Configured-unverified"
        ? "ConfiguredUnverified"
        : "Pending";
  const provider = typeof source.provider === "string" && source.provider.trim() ? source.provider.trim() : "GitLab";
  const endpoint = typeof source.endpoint === "string" ? source.endpoint.trim() : "";
  return { mode, status, provider, endpoint };
}

const subjectLabels: Record<string, string> = {
  System: "System change requests",
  Software: "Software change requests",
  Interface: "Interface change requests",
  SystemTest: "System test procedures",
  HighLevelSoftwareCase: "High-level software test cases",
  HighLevelSoftwareProcedure: "High-level software test procedures",
  LowLevelSoftwareCase: "Low-level software test cases",
  LowLevelSoftwareProcedure: "Low-level software test procedures",
};

function normalizeReviewRules(value: unknown): ReviewRulesDefinition | undefined {
  const source = asObject(value);
  if (!Array.isArray(source.rules)) return undefined;
  // An empty typed rules array is a concrete server decision for a ladder with no applicable
  // review subjects (for example a Customer-only project). It is distinct from an absent definition.
  if (source.rules.length === 0) return { rules: [] };
  const rules = source.rules.flatMap((item) => {
    const rule = asObject(item);
    const subject = typeof rule.subject === "string" ? rule.subject.trim() : "";
    const name = typeof rule.name === "string" && rule.name.trim() ? rule.name.trim() : subject;
    if (!subject || !name || !Array.isArray(rule.stages)) return [];
    const stages = rule.stages.flatMap((item) => {
      const stage = asObject(item);
      const stageName = typeof stage.name === "string" ? stage.name.trim() : "";
      const kind: ReviewStage["kind"] | undefined = stage.kind === "Review" || stage.kind === "Approval" ? stage.kind : undefined;
      const requiredRole = typeof stage.requiredRole === "string" ? stage.requiredRole.trim() : "";
      const authorityKind: ReviewStage["authorityKind"] = stage.authorityKind === "BaseRole" || stage.authorityKind === "LeadershipPosition"
        ? stage.authorityKind
        : null;
      if (!stageName || !kind || !requiredRole) return [];
      return [{ name: stageName, kind, requiredRole, authorityKind }];
    });
    if (stages.length !== rule.stages.length) return [];
    return [{ subject, name, stages }];
  });
  return rules.length === source.rules.length ? { rules } : undefined;
}

function reviewRulesAreComplete(definition?: ReviewRulesDefinition) {
  if (!definition) return false;
  if (definition.rules.length === 0) return true;
  return definition.rules.every((rule) => {
    const kinds = new Set(rule.stages.map((stage) => stage.kind));
    return rule.stages.length > 0
      && kinds.has("Review")
      && kinds.has("Approval")
      && rule.stages.every((stage) => {
        if (!stage.authorityKind) return false;
        const roleSet = stage.authorityKind === "LeadershipPosition" ? leadershipAuthorities : baseRoleAuthorities;
        return Boolean(stage.name.trim()) && roleSet.includes(stage.requiredRole);
      });
  });
}

type ReviewRulesSubjectDelta = { added: string[]; removed: string[] };

function reviewRulesSubjectDelta(
  current: ReviewRulesDefinition | undefined,
  suggested: ReviewRulesDefinition | undefined,
): ReviewRulesSubjectDelta | undefined {
  if (!current || !suggested) return undefined;
  const currentSubjects = new Set(current.rules.map((rule) => rule.subject));
  const suggestedSubjects = new Set(suggested.rules.map((rule) => rule.subject));
  return {
    added: suggested.rules
      .map((rule) => rule.subject)
      .filter((subject) => !currentSubjects.has(subject)),
    removed: current.rules
      .map((rule) => rule.subject)
      .filter((subject) => !suggestedSubjects.has(subject)),
  };
}

function cloneReviewRulesDefinition(definition: ReviewRulesDefinition): ReviewRulesDefinition {
  return {
    rules: definition.rules.map((rule) => ({
      ...rule,
      stages: rule.stages.map((stage) => ({ ...stage })),
    })),
  };
}

function repositoryStatusLabel(status: RepositoryStatus) {
  if (status === "Verified") return "Verified";
  if (status === "ConfiguredUnverified") return "Configured · unverified";
  return "Pending";
}

function startKindLabel(kind: StartKind | "") {
  if (kind === "AeroLinkBaseline") return "Existing AeroLink baseline";
  if (kind === "ExternalBaseline") return "External baseline";
  if (kind === "Fresh") return "Fresh project";
  return "Not chosen";
}

function valuesFromDraft(draft: SetupDraft): SetupValues {
  return {
    projectName: draft.project?.name ?? "",
    softwareProduct: draft.project?.softwareProduct ?? "",
    startKind: draft.start?.kind ?? "",
    sourceBaselineId: draft.start?.sourceBaselineId ?? "",
    sourceImportId: draft.start?.sourceImportId ?? "",
    buildVersion: draft.build?.version ?? "",
    selectedCategories: Array.isArray(draft.selectedCategories)
      ? draft.selectedCategories.filter((item): item is string => typeof item === "string")
      : [],
    ladder: normalizeLadder(draft.ladder),
    reviewRulesDefinition:
      normalizeReviewRules(draft.reviewRules?.definition)
      ?? normalizeReviewRules(draft.reviewRules?.suggestedDefinition),
    suggestedReviewRulesDefinition: normalizeReviewRules(draft.reviewRules?.suggestedDefinition),
    reviewRulesAccepted: draft.reviewRules?.accepted === true,
    repository: normalizeRepository(draft.repository),
    mapping: draft.mapping ?? {},
  };
}

/**
 * The saved facts an accepted source assertion was established against. A read that adopts different ladder,
 * source, mapping or category facts cannot keep the earlier acceptance as authority for them; unchanged
 * facts keep theirs.
 */
function sourceAcceptanceKey(source: SetupDraft) {
  return JSON.stringify({
    start: source.start ?? null,
    ladder: source.ladder ?? null,
    mapping: source.mapping ?? null,
    categories: source.selectedCategories ?? null,
  });
}

/** Structured refusal findings, read from an error body that carried them. */
function refusalFindings(failure: unknown): LadderFinding[] {
  if (!(failure instanceof ApiError)) return [];
  const raw = (failure.details as { findings?: unknown } | undefined)?.findings;
  if (!Array.isArray(raw)) return [];
  return raw.flatMap((item) => {
    const finding = asObject(item);
    const code = typeof finding.code === "string" ? finding.code : "";
    const message = typeof finding.message === "string" ? finding.message : "";
    if (!code || !message) return [];
    return [
      {
        code,
        message,
        level: typeof finding.level === "string" ? finding.level : null,
        field: typeof finding.field === "string" ? finding.field : null,
        token: typeof finding.token === "string" ? finding.token : null,
        subject: typeof finding.subject === "string" ? finding.subject : null,
        stageIndex: typeof finding.stageIndex === "number" ? finding.stageIndex : null,
      },
    ];
  });
}

function draftFromCreate(value: unknown): SetupDraft {
  const source = asObject(value);
  const draftId = typeof source.draftId === "string" ? source.draftId.trim() : "";
  if (!isUuid(draftId)) throw new Error("The setup service returned an invalid draft identity.");
  const currentStep = isSetupStep(source.currentStep)
    ? (source.currentStep as SetupStep)
    : "Details";
  return {
    draftId,
    state: String(source.state ?? "Draft"),
    currentStep,
    version: typeof source.version === "number" ? source.version : 1,
    project: { name: "", softwareProduct: "" },
    start: {},
    build: { version: "" },
    selectedCategories: [],
    ladder: {},
    reviewRules: { accepted: false },
    repository: defaultRepository(),
    mapping: {},
  };
}

function requestBody(values: SetupValues, currentStep: SetupStep, expectedVersion: number) {
  const uuidOrNull = (value: string) => (isUuid(value) ? value.trim() : null);
  return {
    expectedVersion,
    currentStep,
    project: { name: values.projectName, softwareProduct: values.softwareProduct },
    start: {
      kind: values.startKind || null,
      sourceBaselineId:
        values.startKind === "AeroLinkBaseline" ? uuidOrNull(values.sourceBaselineId) : null,
      sourceImportId:
        values.startKind === "ExternalBaseline" ? uuidOrNull(values.sourceImportId) : null,
    },
    // An empty build is a legitimate earlier draft state. Omitting it lets the server preserve that state
    // while Details or Starting Point are saved; sending `{ version: "" }` would invoke the parser too early.
    ...(values.buildVersion.trim() ? { build: { version: values.buildVersion } } : {}),
    // Once a source package exists, its versioned configuration endpoint owns categories. Keeping
    // them out of setup PUTs prevents Save-and-exit/resume from racing or overwriting that source
    // decision; Fresh and not-yet-selected source drafts still retain their local answer here.
    ...((!values.sourceBaselineId && !values.sourceImportId) || values.startKind === "Fresh"
      ? { selectedCategories: values.selectedCategories }
      : {}),
    ladder: values.ladder,
    reviewRules: values.reviewRulesDefinition ?? {},
    reviewRulesAccepted: values.reviewRulesAccepted,
    repository: {
      mode: values.repository.mode,
      provider: values.repository.provider || "GitLab",
      endpoint: values.repository.mode === "ConnectNow" ? values.repository.endpoint || null : null,
    },
    // Source mappings have their own optimistic versioned endpoint. Sending a stale setup-level
    // mapping after a source reconciliation could overwrite that server-owned source state.
    ...(values.startKind === "Fresh" ? { mapping: values.mapping } : {}),
  };
}

function stepIndex(current: SetupStep) {
  const index = steps.findIndex((step) => step.id === current);
  return index < 0 ? 0 : index;
}

export default function ProjectSetupWalkthrough({
  user,
  api,
  draftId,
  onExit,
  onSignOut,
  onCompleted,
  onDraftCreated,
}: {
  user: AuthUser;
  api: string;
  draftId?: string;
  onExit: () => void;
  onSignOut: () => void;
  onCompleted: (result: FinalizationResult) => void;
  /** Replace the temporary creation route once the server has allocated its durable draft identity. */
  onDraftCreated?: (draftId: string) => void;
}) {
  const [draft, setDraft] = useState<SetupDraft>();
  const [values, setValues] = useState<SetupValues>();
  const [currentStep, setCurrentStep] = useState<SetupStep>("Details");
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [finalizing, setFinalizing] = useState(false);
  const [revalidating, setRevalidating] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [sourceState, setSourceState] = useState<SourceDraftState>(emptySourceState);
  const finalizationKey = useRef<string | undefined>(undefined);
  const draftLoadGeneration = useRef(0);
  // Local edits made while a request is in flight. A response that no longer describes what is on screen
  // must not overwrite it, and must not restore a readiness claim for answers it never validated.
  const editGeneration = useRef(0);
  // The draft as it is on screen right now, so an asynchronous response can be judged against the answers it
  // would replace instead of against its own copy of the verdict it carries.
  const draftRef = useRef<SetupDraft | undefined>(undefined);
  // Structured refusal findings, retained with the exact draft version they were reported for.
  const [finalizationFindings, setFinalizationFindings] = useState<
    { draftId: string; version: number; findings: LadderFinding[] } | null
  >(null);
  // A compatible profile the creator chose in this session, keyed by draft and level identity rather than
  // by row position, so reordering or switching drafts cannot restore another level's choice.
  const rememberedProfiles = useRef<Map<string, VerificationKind[]>>(new Map());
  useEffect(() => {
    rememberedProfiles.current.clear();
  }, [draft?.draftId]);
  const sourceSaveRef = useRef<(() => Promise<number | null>) | null>(null);
  const flushingSourceRef = useRef(false);
  const registerSourceSave = useCallback((save: (() => Promise<number | null>) | null) => {
    sourceSaveRef.current = save;
  }, []);

  /**
   * Adopts a server draft only when it does not regress what is already on screen. A response for another
   * draft, or for an older version than one already adopted (for example after a source call advanced the
   * version), is refused rather than overwriting newer state — and its older verdict — onto newer answers.
   */
  const adoptServerDraft = useCallback((candidate: SetupDraft): boolean => {
    const current = draftRef.current;
    if (current && (candidate.draftId !== current.draftId || candidate.version < current.version))
      return false;
    draftRef.current = candidate;
    setDraft(candidate);
    return true;
  }, []);

  /**
   * Adopts a draft version reported by another endpoint. A source call can advance the draft without
   * returning the setup view, so the verdict that described the earlier version is dropped rather than
   * carried forward as though it validated answers nobody has read yet.
   */
  const adoptDraftVersion = useCallback((version: number) => {
    const current = draftRef.current;
    if (!current || version <= current.version) return;
    const next = { ...current, version, validation: null };
    draftRef.current = next;
    setDraft(next);
  }, []);

  const loadDraft = useCallback(
    async (id: string) => {
      const generation = ++draftLoadGeneration.current;
      const isCurrentLoad = () => draftLoadGeneration.current === generation;
      setLoading(true);
      setError("");
      try {
        const loaded = await apiRequest<SetupDraft>(`${api}/api/project-setups/${id}`);
        if (!isCurrentLoad()) return;
        adoptServerDraft(loaded);
        setValues(valuesFromDraft(loaded));
        setSourceState(emptySourceState());
        setCurrentStep(loaded.currentStep);
        if (loaded.start?.kind === "AeroLinkBaseline" || loaded.start?.kind === "ExternalBaseline") {
          try {
            const sourceEnvelope = await apiRequest<unknown>(`${api}/api/project-setups/${id}/source`);
            if (!isCurrentLoad()) return;
            const sourceRecord = asObject(sourceEnvelope);
            const source = decodeSourceView(sourceRecord.source ?? sourceEnvelope);
            const sourceVersion = typeof sourceRecord.draftVersion === "number" ? sourceRecord.draftVersion : loaded.version;
            if (source) {
              setSourceState({ source, assertionAccepted: false, password: "" });
              if (sourceVersion > loaded.version) {
                // The draft moved past the version the loaded verdict describes. Inheriting that verdict
                // would claim the newer answers were validated when they were never read.
                adoptDraftVersion(sourceVersion);
              }
            }
          } catch (failure) {
            if (!isCurrentLoad()) return;
            // A missing source is a truthful pending state on a resumable draft. Other failures are
            // surfaced while leaving the already loaded project answers available for retry.
            if (!(failure instanceof ApiError && failure.status === 404)) {
              setError(operationError(failure, "The saved source could not be loaded. Earlier project answers remain available."));
            }
          }
        }
      } catch (failure) {
        if (!isCurrentLoad()) return;
        setError(
          operationError(
            failure,
            "The setup draft could not be loaded. Your saved answers remain on the server.",
          ),
        );
      } finally {
        if (isCurrentLoad()) setLoading(false);
      }
    },
    [adoptDraftVersion, adoptServerDraft, api],
  );

  useEffect(() => {
    let active = true;
    if (draftId) {
      void loadDraft(draftId);
      return () => {
        active = false;
      };
    }
    setLoading(true);
    setError("");
    apiRequest<unknown>(`${api}/api/project-setups`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({}),
    })
      .then((created) => {
        if (!active) return;
        const createdDraft = draftFromCreate(created);
        onDraftCreated?.(createdDraft.draftId);
        draftRef.current = createdDraft;
        setDraft(createdDraft);
        setValues(valuesFromDraft(createdDraft));
        setSourceState(emptySourceState());
        setCurrentStep("Details");
      })
      .catch((failure) => {
        if (active) setError(operationError(failure, "A new project setup could not be started."));
      })
      .finally(() => {
        if (active) setLoading(false);
      });
    return () => {
      active = false;
    };
  }, [api, draftId, loadDraft, onDraftCreated]);

  const update = <K extends keyof SetupValues>(key: K, value: SetupValues[K]) => {
    editGeneration.current += 1;
    setValues((current) => {
      if (!current) return current;
      const next = { ...current, [key]: value };
      // A changed ladder or rule definition must be reviewed and accepted again. Keeping the old
      // checkbox checked would make the server hash a new definition under stale user intent.
      if (key === "ladder" || key === "reviewRulesDefinition") next.reviewRulesAccepted = false;
      return next;
    });
    if (key === "ladder") {
      setSourceState((current) =>
        current.source
          ? {
              ...current,
              source: { ...current.source, reconciliation: null, assertion: null },
              assertionAccepted: false,
              password: "",
            }
          : current,
      );
    }
    setNotice("");
  };
  const useSuggestedReviewRules = () => {
    if (!values?.suggestedReviewRulesDefinition) return;
    // Only replace the rule definition. Project details, source answers, ladder edits, repository
    // choices, and mappings remain untouched. `update` also clears the acceptance checkbox.
    update(
      "reviewRulesDefinition",
      cloneReviewRulesDefinition(values.suggestedReviewRulesDefinition),
    );
    setNotice(
      "The server rules for this ladder replaced the previous rule definition. Review and accept them again before continuing.",
    );
  };
  const updateReviewRule = (index: number, patch: Partial<ReviewRule>) => {
    if (!values?.reviewRulesDefinition) return;
    update("reviewRulesDefinition", {
      rules: values.reviewRulesDefinition.rules.map((rule, ruleIndex) =>
        ruleIndex === index ? { ...rule, ...patch } : rule,
      ),
    });
  };
  const updateReviewStage = (ruleIndex: number, stageIndex: number, patch: Partial<ReviewStage>) => {
    if (!values?.reviewRulesDefinition) return;
    update("reviewRulesDefinition", {
      rules: values.reviewRulesDefinition.rules.map((rule, currentRuleIndex) =>
        currentRuleIndex === ruleIndex
          ? { ...rule, stages: rule.stages.map((stage, currentStageIndex) => currentStageIndex === stageIndex ? { ...stage, ...patch } : stage) }
          : rule,
      ),
    });
  };
  const addReviewStage = (ruleIndex: number) => {
    if (!values?.reviewRulesDefinition) return;
    update("reviewRulesDefinition", {
      rules: values.reviewRulesDefinition.rules.map((rule, currentRuleIndex) =>
        currentRuleIndex === ruleIndex
          ? { ...rule, stages: [...rule.stages, { name: "", kind: "Review" as const, requiredRole: "", authorityKind: null }] }
          : rule,
      ),
    });
  };
  const removeReviewStage = (ruleIndex: number, stageIndex: number) => {
    if (!values?.reviewRulesDefinition) return;
    update("reviewRulesDefinition", {
      rules: values.reviewRulesDefinition.rules.map((rule, currentRuleIndex) =>
        currentRuleIndex === ruleIndex && rule.stages.length > 1
          ? { ...rule, stages: rule.stages.filter((_, currentStageIndex) => currentStageIndex !== stageIndex) }
          : rule,
      ),
    });
  };
  const hasUnsavedChanges = useMemo(
    () =>
      Boolean(draft && values && JSON.stringify(values) !== JSON.stringify(valuesFromDraft(draft))),
    [draft, values],
  );
  const versionIdentity = values ? officialBuildName(values.buildVersion) : undefined;
  const versionOrder = values ? buildVersionOrder(values.buildVersion) : undefined;
  /** The authoritative completed outcome a draft records, if it has one. */
  const finalizationResultFromDraft = (source: SetupDraft): FinalizationResult | undefined =>
    source.finalization?.programId && source.finalization.projectId && source.finalization.releaseId
      ? {
          state: "Completed",
          alreadyCompleted: true,
          programId: source.finalization.programId,
          projectId: source.finalization.projectId,
          releaseId: source.finalization.releaseId,
          version: source.build.version,
          officialBuildName:
            source.build.officialName ??
            officialBuildName(source.build.version) ??
            "Identity unavailable",
        }
      : undefined;
  const completedResult =
    draft?.state === "Completed" ? finalizationResultFromDraft(draft) : undefined;
  // The server's verdict describes one saved configuration. It is only evidence while it describes the
  // draft and version on screen; a verdict that arrived for other answers is not inherited by these ones.
  const validationIsCurrent = Boolean(
    draft?.validation &&
      draft.validation.draftId === draft.draftId &&
      draft.validation.version === draft.version,
  );
  const configurationReady = validationIsCurrent && draft?.validation?.configurationReady === true;
  const findingsForLevel = (level: string) =>
    validationIsCurrent
      ? (draft?.validation?.findings ?? []).filter((finding) => finding.level === level)
      : [];
  const readinessStepForLevel = (level: string) =>
    validationIsCurrent
      ? draft?.validation?.steps.find((step) => step.level === level)
      : undefined;
  const freshComplete =
    values?.startKind === "Fresh" &&
    Boolean(values.projectName.trim()) &&
    Boolean(values.softwareProduct.trim()) &&
    versionOrder !== undefined &&
    values.ladder.steps.length > 0 &&
    !hasUnsavedChanges &&
    configurationReady;
  const sourceComplete =
    values?.startKind !== "Fresh" &&
    Boolean(values?.projectName.trim()) &&
    Boolean(values?.softwareProduct.trim()) &&
    versionOrder !== undefined &&
    Boolean(values?.ladder.steps.length) &&
    !hasUnsavedChanges &&
    configurationReady &&
    Boolean(
      sourceState.source?.reconciliation?.ready &&
        sourceState.source.assertion?.hash &&
        sourceState.assertionAccepted &&
        sourceState.password,
    );
  const reviewRulesDelta = reviewRulesSubjectDelta(
    values?.reviewRulesDefinition,
    values?.suggestedReviewRulesDefinition,
  );
  const reviewRulesNeedRefresh = Boolean(
    reviewRulesDelta && (reviewRulesDelta.added.length || reviewRulesDelta.removed.length),
  );

  const flushSourceBeforeSetupSave = async () => {
    if (
      !draft ||
      !values ||
      values.startKind === "Fresh" ||
      !sourceState.source ||
      sourceState.source.reconciliation ||
      !sourceSaveRef.current
    ) {
      return draft?.version ?? null;
    }
    flushingSourceRef.current = true;
    try {
      const version = await sourceSaveRef.current();
      if (version !== null) {
        adoptDraftVersion(version);
      }
      return version;
    } finally {
      flushingSourceRef.current = false;
    }
  };

  const saveDraft = async (
    exitAfterSave = false,
    stepToSave = currentStep,
    expectedVersionOverride?: number,
    flushSource = false,
  ) => {
    if (!draft || !values) return false;
    // The edit generation and the answers it describes are captured together, before any await. Capturing
    // it after the asynchronous source flush would let a save claim the newer edit generation while
    // submitting the older payload — and then overwrite, or navigate away from, answers it never saved.
    const generationAtRequest = editGeneration.current;
    const submitted = values;
    if (values.startKind === "AeroLinkBaseline" && values.sourceBaselineId && !isUuid(values.sourceBaselineId)) {
      setError(
        "Enter the exact authorized AeroLink baseline ID as a UUID before saving this starting point.",
      );
      return false;
    }
    if (values.startKind === "ExternalBaseline" && values.sourceImportId && !isUuid(values.sourceImportId)) {
      setError(
        "Enter the exact staged external import ID as a UUID before saving this starting point.",
      );
      return false;
    }
    let expectedVersion = expectedVersionOverride ?? draft.version;
    if (flushSource) {
      const sourceVersion = await flushSourceBeforeSetupSave();
      if (sourceVersion === null) return false;
      expectedVersion = sourceVersion;
    }
    setSaving(true);
    setError("");
    setNotice("");
    try {
      const response = await apiRequest<SetupDraft | { saved: boolean; draft: SetupDraft }>(
        `${api}/api/project-setups/${draft.draftId}${exitAfterSave ? "/save-and-exit" : ""}`,
        {
          method: exitAfterSave ? "POST" : "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(requestBody(submitted, stepToSave, expectedVersion)),
        },
      );
      const saved = "draft" in response ? response.draft : response;
      const adopted = adoptServerDraft(saved);
      const superseded = editGeneration.current !== generationAtRequest;
      const foreignResponse = Boolean(draftRef.current && saved.draftId !== draftRef.current.draftId);
      // Answers typed while this save was in flight are newer than the response. Adopt the saved draft and
      // its verdict, but never replace what the creator is currently editing with what they had already
      // changed; the outstanding edit keeps readiness unavailable until they save again.
      if (adopted && !superseded) setValues(valuesFromDraft(saved));
      if (adopted) setCurrentStep(saved.currentStep);
      if (exitAfterSave && (superseded || foreignResponse)) {
        // Leaving now would abandon newer answers, or a response that does not even describe this draft, on a
        // confirmation that only ever covered the older ones.
        setError(
          superseded
            ? "Save and exit kept this setup open: answers changed while the save was in flight are newer than what the server committed. Your newer answers are still unsaved — save again to commit them and leave."
            : "Save and exit kept this setup open: the response did not describe this draft, so its answers were not treated as saved. Recheck this setup, then save again to leave.",
        );
        return saved;
      }
      setNotice(
        superseded
          ? "Saved the answers that were submitted. Answers you changed while the save was in flight are still unsaved."
          : adopted
            ? "Saved on the server. You can resume this setup after signing out or restarting."
            : "Saved the answers that were submitted. The setup has since reported a newer saved version (for example from the source pipeline); recheck it before finalizing.",
      );
      if (exitAfterSave) onExit();
      return saved;
    } catch (failure) {
      setError(
        operationError(
          failure,
          "The setup could not be saved. Your unsaved answers remain on this screen.",
        ),
      );
      return false;
    } finally {
      setSaving(false);
    }
  };

  const ensureSourceSaved = async () => {
    if (!draft || !values) return null;
    // Global Save and exit/navigation first flushes the source-owned configuration. During that
    // callback, the source endpoint already has the current setup token; avoid recursively issuing
    // a setup PUT that could invalidate the source while it is being saved.
    if (flushingSourceRef.current) return draft.version;
    const persisted = valuesFromDraft(draft);
    const pendingSourceChoice =
      values.startKind !== "Fresh" &&
      !values.sourceBaselineId &&
      !values.sourceImportId &&
      values.projectName === persisted.projectName &&
      values.softwareProduct === persisted.softwareProduct &&
      values.buildVersion === persisted.buildVersion &&
      JSON.stringify(values.selectedCategories) === JSON.stringify(persisted.selectedCategories) &&
      JSON.stringify(values.ladder) === JSON.stringify(persisted.ladder) &&
      JSON.stringify(values.reviewRulesDefinition) === JSON.stringify(persisted.reviewRulesDefinition) &&
      values.reviewRulesAccepted === persisted.reviewRulesAccepted &&
      JSON.stringify(values.repository) === JSON.stringify(persisted.repository);
    // The source route is the durable first step for a not-yet-selected source. The setup PUT
    // contract quite correctly rejects an incomplete non-Fresh identity, so retain this one local
    // choice until native selection or upload returns the exact source identity. All other answers
    // must still be committed before any source call.
    if (pendingSourceChoice) return draft.version;
    if (!hasUnsavedChanges) return draft.version;
    const saved = await saveDraft(false, currentStep);
    return saved ? saved.version : null;
  };

  const updateSourceVersion = adoptDraftVersion;

  const updateSourceCategories = useCallback((categories: string[]) => {
    setValues((current) => {
      if (!current || JSON.stringify(current.selectedCategories) === JSON.stringify(categories)) return current;
      return { ...current, selectedCategories: categories };
    });
  }, []);

  const updateSourceIdentity = useCallback((id: string) => {
    setValues((current) => {
      if (!current) return current;
      const key = current.startKind === "AeroLinkBaseline" ? "sourceBaselineId" : "sourceImportId";
      if (current[key] === id) return current;
      return { ...current, [key]: id };
    });
  }, []);

  const updateSourceState = useCallback((next: SourceDraftState) => {
    setSourceState(next);
  }, []);

  /**
   * Re-reads the saved configuration so the server can re-issue its verdict for a draft whose version
   * moved without the answers changing. It deliberately keeps the loaded source state: a recheck must not
   * throw away a staged source, an accepted assertion, or anything the creator has already answered.
   */
  const revalidate = async () => {
    if (!draft) return;
    const generationAtRequest = editGeneration.current;
    setRevalidating(true);
    try {
      const loaded = await apiRequest<SetupDraft>(`${api}/api/project-setups/${draft.draftId}`);
      // Compare the response with what is on screen *now*: a request issued before another read or save
      // adopted a newer version must not replace it with the older answers it was sent to describe.
      const live = draftRef.current ?? draft;
      if (loaded.draftId !== live.draftId || loaded.version < live.version) {
        // A response that describes another draft, or an older version than one already adopted, is not
        // evidence about the answers on this screen. Name it rather than applying it over newer state.
        setNotice(
          "The server returned a configuration older than the one on this screen, so nothing was replaced. Recheck again once the saved configuration has settled.",
        );
        return;
      }
      adoptServerDraft(loaded);
      if (editGeneration.current === generationAtRequest) setValues(valuesFromDraft(loaded));
      const sourceChanged = sourceAcceptanceKey(live) !== sourceAcceptanceKey(loaded);
      if (sourceChanged) {
        setSourceState((current) =>
          current.source
            ? {
                ...current,
                source: { ...current.source, reconciliation: null, assertion: null },
                assertionAccepted: false,
                password: "",
              }
            : current,
        );
      }
      setNotice(
        sourceChanged
          ? "Rechecked the saved configuration against the server's maintained rules. The saved ladder or source answers changed, so the earlier source acceptance no longer applies — reconcile the source and accept it again before finalizing."
          : "Rechecked the saved configuration against the server's maintained rules.",
      );
    } catch (failure) {
      setError(
        operationError(
          failure,
          "The saved configuration could not be rechecked. Your answers remain on the server.",
        ),
      );
    } finally {
      setRevalidating(false);
    }
  };

  /**
   * Decides the visible result of a failed finalization from authoritative recovered state rather than
   * from the fact that a request threw. A refusal, a committed result and an unresolved attempt are
   * three different facts, and only the first two are established by the server.
   */
  const recoverFinalization = async (failure: unknown, attempted: SetupDraft) => {
    const generationAtRequest = editGeneration.current;
    const status = failure instanceof ApiError ? failure.status : undefined;
    // Only a truthful, attributable statement may be repeated. The generic 5xx fallback ("No success was
    // recorded") asserts an outcome a failed finalization cannot establish, so it is never repeated as fact.
    const clientReport = failure instanceof ApiError && failure.message ? failure.message : undefined;
    const reported = clientReport && !clientReport.includes("No success was recorded")
      ? `${clientReport} `
      : status
        ? `AeroLink reported HTTP ${status} for this finalization attempt without an explanatory message. `
        : "";
    let recovered: SetupDraft | undefined;
    let recoveryFailed = false;
    try {
      recovered = await apiRequest<SetupDraft>(`${api}/api/project-setups/${attempted.draftId}`);
    } catch {
      recoveryFailed = true;
    }
    if (recovered) {
      if (recovered.draftId === attempted.draftId && recovered.version >= attempted.version) {
        adoptServerDraft(recovered);
        if (editGeneration.current === generationAtRequest) setValues(valuesFromDraft(recovered));
        setCurrentStep(recovered.currentStep);
        if (sourceAcceptanceKey(attempted) !== sourceAcceptanceKey(recovered)) {
          setSourceState((current) =>
            current.source
              ? {
                  ...current,
                  source: { ...current.source, reconciliation: null, assertion: null },
                  assertionAccepted: false,
                  password: "",
                }
              : current,
          );
          setNotice(
            "The server's current saved configuration differs from the one this source acceptance was established against. The staged source is kept; reconcile it and accept the source again before finalizing.",
          );
        }
      }
      // A recorded completion outranks the exception that prompted the recovery read.
      const completed = finalizationResultFromDraft(recovered);
      if (recovered.state === "Completed" && completed) {
        // Show the recorded outcome on this screen rather than navigating past it: the creator still gets
        // the explicit build selection, and a lost response is not retold as a failure.
        setError("");
        setFinalizationFindings(null);
        setNotice(
          "The Project was created. The earlier response was lost before it arrived, so this is the recorded result.",
        );
        return;
      }
    }
    // Structured findings keep the level/field detail the refusal identified, together with the exact draft
    // version they were reported for, so the repair path can use them without treating them as authority
    // over answers the creator has changed since.
    const findings = refusalFindings(failure);
    if (findings.length > 0)
      setFinalizationFindings({ draftId: attempted.draftId, version: attempted.version, findings });

    const unknownOutcome = (detail: string) =>
      `The result of this finalization cannot yet be confirmed. ${detail} Retry the same request from this draft rather than starting another setup.`;

    // A held-but-settled in-flight state is named before any status-specific wording, so a conflict or a
    // server error that is still completing is never described as a finished rejection.
    if (recovered?.state === "Finalizing") {
      setError(
        `${reported}The server reports this setup is still being finalized, so its outcome is not settled yet. Wait briefly, then retry from this draft.`,
      );
      return;
    }
    if (status === 400 && recovered) {
      setError(
        `${reported}The saved configuration is still a recoverable draft at version ${recovered.version}; repair what the findings identify, save it, and retry.`,
      );
      return;
    }
    if (status === 403) {
      // The follow-up read may itself have been refused, in which case continued access is not established.
      setError(
        recovered
          ? `${reported}Only an AeroLink administrator can create a Project. This setup is still saved as a draft at version ${recovered.version}; an authorized administrator can retry it.`
          : `${reported}Only an AeroLink administrator can create a Project, and the follow-up recovery read failed, so the current draft state could not be confirmed. Sign in with an authorized account and recheck this draft.`,
      );
      return;
    }
    if (status === 409 && recovered) {
      setError(
        `${reported}This saved setup was reloaded at its current version ${recovered.version}; review the answers and retry.`,
      );
      return;
    }
    if (recoveryFailed) {
      setError(
        unknownOutcome(
          `${reported}The follow-up recovery read failed, so the current server state could not be established.`,
        ),
      );
      return;
    }
    if (recovered?.state === "Draft") {
      // An unfinished draft read after a transport or server error is not proof the attempt rolled back.
      setError(
        unknownOutcome(
          `${reported}The server currently reports this setup as an unfinished draft at version ${recovered.version}; that alone does not prove the attempt failed.`,
        ),
      );
      return;
    }
    setError(unknownOutcome(`${reported}The server reported state ${recovered?.state ?? "unknown"}.`));
  };

  const finalize = async () => {
    if (!draft || !values || (!freshComplete && !sourceComplete)) return;
    if (!user.isAdministrator) {
      setError("Only an AeroLink administrator can finalize a new Project. Your saved setup remains available for editing and resume.");
      return;
    }
    setFinalizing(true);
    setError("");
    setNotice("");
    // A new attempt supersedes the previous refusal report; keeping it beside a fresh result would present
    // old findings as though they described the attempt now in flight.
    setFinalizationFindings(null);
    finalizationKey.current ??=
      globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random()}`;
    try {
      const result = await apiRequest<FinalizationResult>(
        `${api}/api/project-setups/${draft.draftId}/finalize`,
        {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            "Idempotency-Key": finalizationKey.current,
          },
          body: JSON.stringify({
            expectedVersion: draft.version,
            idempotencyKey: finalizationKey.current,
            ...(sourceComplete && values.startKind !== "Fresh"
              ? sourceFinalizationPayload(draft.version, finalizationKey.current, sourceState)
              : {}),
          }),
        },
      );
      onCompleted(result);
    } catch (failure) {
      // Recovery is awaited so the message on screen describes the settled outcome, not the request that
      // may or may not have committed.
      await recoverFinalization(failure, draft);
    } finally {
      setFinalizing(false);
    }
  };

  const goTo = async (target: SetupStep) => {
    if (target === currentStep) return;
    if (
      (hasUnsavedChanges || target !== draft?.currentStep) &&
      !(await saveDraft(false, target, undefined, currentStep === "StartingPoint"))
    )
      return;
    setCurrentStep(target);
  };

  const addLadderStep = () => {
    if (!values) return;
    const next = levelCatalogue.find(
      (level) => !values.ladder.steps.some((step) => step.catalogueEntry === level.id),
    );
    if (!next) return;
    update("ladder", {
      ...values.ladder,
      steps: [
        ...values.ladder.steps,
        {
          catalogueEntry: next.id,
          position: values.ladder.steps.length + 1,
          capabilities: next.capabilities,
          enabledArtifactKinds: [...next.verification],
        },
      ],
    });
  };
  const updateLadder = (next: LadderDefinition) =>
    update("ladder", {
      ...next,
      steps: next.steps.map((step, index) => ({ ...step, position: index + 1 })),
    });

  const applySourceLadderSuggestion = (suggestion: SourceLadderSuggestion) => {
    if (!values) return;
    const catalogueFor = (value: string) => {
      const normalized = value.trim().toLocaleLowerCase();
      return levelCatalogue.find(
        (level) =>
          level.id.toLocaleLowerCase() === normalized ||
          level.label.toLocaleLowerCase() === normalized,
      );
    };
    const supportedLevels = suggestion.levels.flatMap((level) => {
      const catalogue = catalogueFor(level);
      return catalogue ? [catalogue] : [];
    });
    const unsupportedLevels = suggestion.levels.filter((level) => !catalogueFor(level));
    const existing = new Map(values.ladder.steps.map((step) => [step.catalogueEntry, step]));
    const steps = [...values.ladder.steps];
    for (const catalogue of supportedLevels) {
      if (existing.has(catalogue.id)) continue;
      steps.push({
        catalogueEntry: catalogue.id,
        position: steps.length + 1,
        capabilities: catalogue.capabilities,
        enabledArtifactKinds: [...catalogue.verification],
      });
      existing.set(catalogue.id, steps[steps.length - 1]);
    }
    const compatibleLevels = new Set(steps.map((step) => step.catalogueEntry));
    const relationships = [...values.ladder.relationships];
    let addedRelationships = 0;
    for (const relationship of suggestion.relationships) {
      // Source trace rows carry the child at sourceLevel and the parent at targetLevel. This
      // preserves the typed source direction while keeping unrelated native relationships out of
      // the ladder; no ancestry is invented from numeric order or a relationship label.
      if (!["AllocatedFrom", "DerivedFrom"].includes(relationship.type)) continue;
      const parent = catalogueFor(relationship.targetLevel)?.id;
      const child = catalogueFor(relationship.sourceLevel)?.id;
      if (!parent || !child || parent === child || !compatibleLevels.has(parent) || !compatibleLevels.has(child)) continue;
      if (relationships.some((edge) => edge.parent === parent && edge.child === child)) continue;
      relationships.push({ parent, child });
      addedRelationships += 1;
    }
    if (!supportedLevels.length) {
      setNotice(
        unsupportedLevels.length
          ? `The source suggestion has no maintained levels that can be applied. Review: ${unsupportedLevels.join(", ")}.`
          : "The source did not suggest any maintained levels to apply.",
      );
      return;
    }
    updateLadder({ steps, relationships });
    setNotice(
      `Applied ${supportedLevels.length} source-informed maintained level${supportedLevels.length === 1 ? "" : "s"} and ${addedRelationships} compatible relationship${addedRelationships === 1 ? "" : "s"}. Review the ladder and review rules again; source reconciliation is now stale.` +
        (unsupportedLevels.length ? ` Unmapped source levels remain for explicit review: ${unsupportedLevels.join(", ")}.` : ""),
    );
  };

  if (loading)
    return (
      <div className="projectSetupPage">
        <PortalHeader user={user} onSignOut={onSignOut} />
        <main className="projectSetupMain">
          <p role="status">Opening the saved project setup…</p>
        </main>
      </div>
    );
  if (!draft || !values)
    return (
      <div className="projectSetupPage">
        <PortalHeader user={user} onSignOut={onSignOut} />
        <main className="projectSetupMain">
          <div className="projectSetupError" role="alert">
            <h1>Project setup unavailable</h1>
            <p>{error || "The setup draft could not be opened."}</p>
            <button type="button" onClick={onExit}>
              Back to Projects
            </button>
          </div>
        </main>
      </div>
    );

  const activeIndex = stepIndex(currentStep);
  const renderStep = () => {
    if (completedResult)
      return (
        <section className="setupStepPanel setupCompletionPanel">
          <h2>Project created</h2>
          <p>
            The Project and its first working build were committed. Source facts, if any, remain
            distinct from this new working context.
          </p>
          <dl className="setupReviewList">
            <div>
              <dt>Project</dt>
              <dd>{values.projectName}</dd>
            </div>
            <div>
              <dt>First build</dt>
              <dd>{completedResult.officialBuildName} · In Work</dd>
            </div>
          </dl>
          <button
            type="button"
            className="setupFinalizeButton"
            onClick={() => onCompleted(completedResult)}
          >
            Open build lineage
          </button>
        </section>
      );
    if (currentStep === "Details")
      return (
        <section className="setupStepPanel">
          <h2>Project details</h2>
          <p>
            Name the Project people will use. AeroLink keeps internal backing identities out of this
            walkthrough.
          </p>
          <div className="setupFormGrid">
            <label>
              Project name
              <input
                value={values.projectName}
                onChange={(event) => update("projectName", event.target.value)}
                maxLength={200}
                placeholder="e.g. Navigation Software"
              />
            </label>
            <label>
              Software product
              <input
                value={values.softwareProduct}
                onChange={(event) => update("softwareProduct", event.target.value)}
                maxLength={200}
                placeholder="e.g. Integrated Navigation Software"
              />
            </label>
          </div>
        </section>
      );
    if (currentStep === "StartingPoint")
      return (
        <section className="setupStepPanel">
          <h2>Choose a starting point</h2>
          <p>
            Fresh starts contain no inherited engineering content. Source starts retain exact source
            identity and remain pending until their supported pipeline is complete.
          </p>
          <fieldset className="setupChoiceList">
            <legend>Starting point</legend>
            {(["Fresh", "AeroLinkBaseline", "ExternalBaseline"] as StartKind[]).map((kind) => (
              <label key={kind}>
                <input
                  type="radio"
                  name="startKind"
                  checked={values.startKind === kind}
                  onChange={() => {
                    update("startKind", kind);
                    update("selectedCategories", kind === "Fresh" ? [] : values.selectedCategories);
                    update("sourceBaselineId", "");
                    update("sourceImportId", "");
                    setSourceState(emptySourceState());
                  }}
                />
                {kind === "Fresh"
                  ? "Fresh project"
                  : kind === "AeroLinkBaseline"
                    ? "Existing authorized AeroLink baseline"
                    : "External baseline from another tool"}
                <small>
                  {kind === "Fresh"
                    ? "Empty project structure only."
                    : "Source content is attributable and reviewed before it can be materialized."}
                </small>
              </label>
            ))}
          </fieldset>
          {values.startKind && values.startKind !== "Fresh" && draft && (
            <ProjectSetupSourcePanel
              api={api}
              draftId={draft.draftId}
              draftVersion={draft.version}
              kind={values.startKind as SourceKind}
              canAcceptSource={user.isAdministrator}
              selectedCategories={values.selectedCategories}
              levelOptions={levelCatalogue.map((level) => ({ id: level.id, label: level.label }))}
              ladderRevision={JSON.stringify(values.ladder)}
              initialState={sourceState}
              onSelectedCategoriesChange={updateSourceCategories}
              beforeSourceCall={ensureSourceSaved}
              onSourceVersion={updateSourceVersion}
              onSourceIdentity={updateSourceIdentity}
              onSourceStateChange={updateSourceState}
              onApplyLadderSuggestion={applySourceLadderSuggestion}
              onRegisterSourceSave={registerSourceSave}
            />
          )}
        </section>
      );
    if (currentStep === "FirstBuild")
      return (
        <section className="setupStepPanel">
          <h2>First working build</h2>
          <p>
            The first build is created in work. Historical source baseline state remains distinct
            from this new working context.
          </p>
          <label className="setupWideField">
            Version
            <input
              value={values.buildVersion}
              onChange={(event) => update("buildVersion", event.target.value)}
              placeholder="e.g. 1.02"
              inputMode="decimal"
              aria-describedby="setup-build-help"
            />
          </label>
          <p
            id="setup-build-help"
            className={versionIdentity ? "setupFieldHint" : "setupFieldError"}
          >
            {versionIdentity
              ? `Official identity: ${versionIdentity}`
              : "Use the maintained format major.minor, for example 0.01, 1.02, or 1.3."}
          </p>
        </section>
      );
    if (currentStep === "Ladder")
      return (
        <section className="setupStepPanel">
          <h2>Review the requirement ladder</h2>
          <p>
            Select maintained supported levels and compatible capabilities. Software verification
            can be Case-only or Case + Procedure; Customer and Interface retain only their supported
            non-verification capabilities.
          </p>
          <ol className="setupLadderRows">
            {values.ladder.steps.map((step, index) => {
              const catalogue =
                levelCatalogue.find((level) => level.id === step.catalogueEntry) ??
                levelCatalogue[0];
              const updateStep = (patch: Partial<LadderStep>) =>
                updateLadder({
                  ...values.ladder,
                  steps: values.ladder.steps.map((current, i) =>
                    i === index ? { ...current, ...patch } : current,
                  ),
                });
              /**
               * Turning verification off removes the artifacts it enabled, in the same action, so the
               * saved answers cannot contradict the capability the creator just cleared. Turning it back
               * on restores a choice made in this session for this draft and level, or asks for one; it
               * never silently substitutes the new-project default for a profile nobody chose.
               */
              const setVerification = (enabled: boolean) => {
                if (!enabled) {
                  // Only an exactly compatible saved profile is worth remembering; a remembered choice is
                  // never derived by filtering an invalid value into a shorter, valid-looking one.
                  const kinds = compatibleRememberedProfile(step, catalogue.id);
                  if (draft && kinds) rememberedProfiles.current.set(`${draft.draftId}:${step.catalogueEntry}`, kinds);
                  updateStep({ capabilities: step.capabilities & ~2, enabledArtifactKinds: [] });
                  return;
                }
                const restored = draft
                  ? rememberedProfiles.current.get(`${draft.draftId}:${step.catalogueEntry}`)
                  : undefined;
                updateStep({
                  capabilities: step.capabilities | 2,
                  enabledArtifactKinds: catalogue.id === "System" ? ["Procedure"] : (restored ?? []),
                });
              };
              return (
                <li key={`${step.catalogueEntry}-${index}`}>
                  <span>{index + 1}</span>
                  <label>
                    Level
                    <select
                      value={step.catalogueEntry}
                      onChange={(event) => {
                        const next =
                          levelCatalogue.find((level) => level.id === event.target.value) ??
                          catalogue;
                        updateStep({
                          catalogueEntry: next.id,
                          capabilities: next.capabilities,
                          enabledArtifactKinds: [...next.verification],
                        });
                      }}
                    >
                      {levelCatalogue.map((level) => (
                        <option
                          key={level.id}
                          value={level.id}
                          disabled={
                            level.id !== step.catalogueEntry &&
                            values.ladder.steps.some((other) => other.catalogueEntry === level.id)
                          }
                        >
                          {level.label}
                        </option>
                      ))}
                    </select>
                  </label>
                  <fieldset>
                    <legend>Capabilities</legend>
                    {capabilityLabels.map((label, capabilityIndex) => {
                      const allowed = (catalogue.capabilities & (1 << capabilityIndex)) !== 0;
                      const isVerification = capabilityIndex === 1;
                      return (
                        <label key={label}>
                          <input
                            type="checkbox"
                            checked={allowed && (step.capabilities & (1 << capabilityIndex)) !== 0}
                            disabled={!allowed}
                            onChange={(event) =>
                              isVerification
                                ? setVerification(event.target.checked)
                                : updateStep({
                                    capabilities: event.target.checked
                                      ? step.capabilities | (1 << capabilityIndex)
                                      : step.capabilities & ~(1 << capabilityIndex),
                                  })
                            }
                          />
                          {label}
                        </label>
                      );
                    })}
                  </fieldset>
                  {catalogue.verification.length > 0 && (
                    <div className="setupVerificationState">
                      {/* The selected capability, the saved artifacts and the effective interpretation are
                          three different facts, and an invalid configuration is named rather than described
                          as if it were a valid one. */}
                      <dl className="setupVerificationFacts">
                        <div>
                          <dt>Capability</dt>
                          <dd>
                            {hasVerificationCapability(step)
                              ? "Verification enabled"
                              : "Verification disabled"}
                          </dd>
                        </div>
                        <div>
                          <dt>Saved artifacts</dt>
                          <dd>{savedArtifactsLabel(step)}</dd>
                        </div>
                        <div>
                          <dt>Effective</dt>
                          <dd>{effectiveArtifactsLabel(step, readinessStepForLevel(step.catalogueEntry))}</dd>
                        </div>
                      </dl>
                      {disabledVerificationWithArtifacts(step) && (
                        <>
                          <p className="setupFieldError" role="alert">
                            {levelLabel(step.catalogueEntry)} — Verification is disabled, but the saved
                            profile still enables {boundedTokenList(savedArtifactTokens(step) ?? [])}. This
                            configuration cannot be created until it is repaired.
                          </p>
                          <div className="setupRowActions">
                            <button
                              type="button"
                              onClick={() =>
                                updateStep({
                                  capabilities: step.capabilities & ~2,
                                  enabledArtifactKinds: [],
                                })
                              }
                            >
                              Keep verification disabled and remove the enabled artifacts
                            </button>
                            <button type="button" onClick={() => setVerification(true)}>
                              Enable verification for this level
                            </button>
                          </div>
                        </>
                      )}
                      {enabledVerificationProfileInvalid(step) && (
                        <p className="setupFieldError" role="alert">
                          {levelLabel(step.catalogueEntry)} — Verification is enabled but the saved profile{" "}
                          {artifactProfileIsMalformed(step)
                            ? "is not a list of artifact kinds"
                            : unrecognizedArtifactTokens(step).length > 0
                              ? `contains artifact kinds this version does not recognize (${boundedTokenList(
                                  unrecognizedArtifactTokens(step),
                                )})`
                              : "selects no artifacts"}
                          , so it cannot be finalized as it stands.
                        </p>
                      )}
                      {hasVerificationCapability(step) &&
                        recognizedArtifactKinds(step) !== undefined &&
                        compatibleRememberedProfile(step, "System") === undefined &&
                        catalogue.id === "System" && (
                          <button
                            type="button"
                            onClick={() => updateStep({ enabledArtifactKinds: ["Procedure"] })}
                          >
                            Use the maintained System profile (Procedure)
                          </button>
                        )}
                      {recognizedArtifactKinds(step) === undefined && (
                        <p className="setupFieldHint">
                          No verification profile is saved for this level. The maintained interpretation
                          of an unspecified profile here is{" "}
                          {(readinessStepForLevel(step.catalogueEntry)?.effective ?? []).join(" + ") ||
                            "no artifacts"}
                          ; choose a profile to record an explicit decision.
                        </p>
                      )}
                      {!hasVerificationCapability(step) && (
                        <p className="setupFieldHint">
                          Verification is disabled for {levelLabel(step.catalogueEntry)}. Enabling it again
                          does not silently substitute a default: a compatible choice made in this session
                          is restored, otherwise you are asked for one.
                        </p>
                      )}
                      {hasVerificationCapability(step) && catalogue.id !== "System" && (
                        <>
                          <label>
                            Verification profile
                            <select
                              value={profileSelection(step)}
                              onChange={(event) =>
                                updateStep({
                                  enabledArtifactKinds:
                                    event.target.value === "Case+Procedure"
                                      ? ["Case", "Procedure"]
                                      : event.target.value === "Case"
                                        ? ["Case"]
                                        : [],
                                })
                              }
                            >
                              <option value="">Choose a verification profile…</option>
                              <option value="Case">Case-only</option>
                              <option value="Case+Procedure">Case + Procedure</option>
                            </select>
                          </label>
                        </>
                      )}
                      {!hasVerificationCapability(step) && artifactProfileIsMalformed(step) && (
                        <p className="setupFieldError" role="alert">
                          The saved verification profile for this level is not a list of artifact kinds, so
                          it cannot be kept as a valid choice. Repair this level before finalizing.
                        </p>
                      )}
                    </div>
                  )}
                  {findingsForLevel(step.catalogueEntry).length > 0 && (
                    <>
                      {hasUnsavedChanges && (
                        <p className="setupFieldHint">
                          From the last saved check — save your changes so the server can re-check the
                          answers you have edited.
                        </p>
                      )}
                      {findingsForLevel(step.catalogueEntry).map((finding) => (
                        <p
                          className="setupFieldError"
                          role="alert"
                          key={`${finding.code}-${finding.token ?? ""}`}
                        >
                          {finding.level ? `${levelLabel(finding.level)} — ` : ""}
                          {finding.message}
                        </p>
                      ))}
                    </>
                  )}
                  <div className="setupRowActions">
                    <button
                      type="button"
                      onClick={() => {
                        if (index === 0) return;
                        const reordered = [...values.ladder.steps];
                        [reordered[index - 1], reordered[index]] = [
                          reordered[index],
                          reordered[index - 1],
                        ];
                        updateLadder({ ...values.ladder, steps: reordered });
                      }}
                      disabled={index === 0}
                    >
                      ↑
                    </button>
                    <button
                      type="button"
                      onClick={() => {
                        if (index === values.ladder.steps.length - 1) return;
                        const reordered = [...values.ladder.steps];
                        [reordered[index], reordered[index + 1]] = [
                          reordered[index + 1],
                          reordered[index],
                        ];
                        updateLadder({ ...values.ladder, steps: reordered });
                      }}
                      disabled={index === values.ladder.steps.length - 1}
                    >
                      ↓
                    </button>
                    <button
                      type="button"
                      onClick={() =>
                        updateLadder({
                          ...values.ladder,
                          steps: values.ladder.steps.filter((_, i) => i !== index),
                          relationships: values.ladder.relationships.filter(
                            (edge) =>
                              edge.parent !== step.catalogueEntry &&
                              edge.child !== step.catalogueEntry,
                          ),
                        })
                      }
                      disabled={values.ladder.steps.length <= 1}
                    >
                      Remove
                    </button>
                  </div>
                </li>
              );
            })}
          </ol>
          <button
            type="button"
            onClick={addLadderStep}
            disabled={values.ladder.steps.length >= levelCatalogue.length}
          >
            Add supported level
          </button>
          <p className="setupFieldHint">
            The server validates capabilities, relationships, and the effective ladder again before
            content can be accepted.
          </p>
        </section>
      );
    if (currentStep === "WorkingRules")
      return (
        <section className="setupStepPanel">
          <h2>Review and approval rules</h2>
          <p>
            The server supplies the maintained ladder-appropriate standard. Review each stage, adjust
            its name, signature meaning, or supported project authority, then explicitly accept the
            resulting definition. No people are assigned automatically.
          </p>
          {reviewRulesNeedRefresh && reviewRulesDelta && (
            <div className="setupRulesRefresh" role="alert">
              <strong>The saved ladder has a newer review standard</strong>
              <p>
                The server recalculated required subjects from the ladder you saved. This draft still
                shows the prior definition, so finalization will remain blocked until the current
                standard is applied and accepted again.
              </p>
              {reviewRulesDelta.added.length > 0 && (
                <p>
                  Required subjects added:{" "}
                  {reviewRulesDelta.added.map((subject) => subjectLabels[subject] ?? subject).join(", ")}.
                </p>
              )}
              {reviewRulesDelta.removed.length > 0 && (
                <p>
                  Subjects no longer required:{" "}
                  {reviewRulesDelta.removed.map((subject) => subjectLabels[subject] ?? subject).join(", ")}.
                </p>
              )}
              <p>
                Using the current standard replaces this draft&apos;s existing rule definition, including
                any custom edits. Other setup answers remain unchanged.
              </p>
              <button type="button" onClick={useSuggestedReviewRules}>
                Use rules for this ladder
              </button>
            </div>
          )}
          {values.reviewRulesDefinition ? (
            <div className="setupRulesDefinition">
              <p className="setupFieldHint">
                These are the concrete rules returned by the server for this ladder. Stage names and
                authority selections are saved as part of the accepted configuration.
              </p>
              {validationIsCurrent && draft?.validation?.review.covers === false && (
                <p className="setupFieldError" role="alert">
                  The saved rules do not cover this ladder exactly
                  {draft.validation.review.missingSubjects.length > 0
                    ? `: required subjects are missing (${draft.validation.review.missingSubjects
                        .map((subject) => subjectLabels[subject] ?? subject)
                        .join(", ")})`
                    : ""}
                  {draft.validation.review.unexpectedSubjects.length > 0
                    ? ` and subjects this ladder no longer requires are still present (${draft.validation.review.unexpectedSubjects
                        .map((subject) => subjectLabels[subject] ?? subject)
                        .join(", ")})`
                    : ""}
                  . Apply the current standard for this ladder and accept it again.
                </p>
              )}
              {validationIsCurrent && (draft?.validation?.review.definitionFindings?.length ?? 0) > 0 && (
                <div className="setupFieldError" role="alert">
                  <strong>These rules are not yet a complete definition</strong>
                  <ul>
                    {(draft?.validation?.review.definitionFindings ?? []).map((finding) => (
                      <li key={`${finding.code}-${finding.subject ?? ""}-${finding.stageIndex ?? ""}`}>
                        {finding.subject
                          ? subjectLabels[finding.subject] ?? finding.subject
                          : "Review rules"}
                        {finding.stageIndex ? ` — stage ${finding.stageIndex}` : ""} — {finding.message}
                      </li>
                    ))}
                  </ul>
                  <p>
                    A rule needs a named stage, a Review signature and an Approval signature with a
                    supported project authority. Adjust the affected rule and save again; finalization
                    remains blocked until the definition is complete and accepted.
                  </p>
                </div>
              )}
              {values.reviewRulesDefinition.rules.length === 0 && <p className="setupFieldHint">The server found no applicable review subjects for this ladder. Acknowledging this empty standard is still required before finalization.</p>}
              {values.reviewRulesDefinition.rules.map((rule, ruleIndex) => (
                <article className="setupRule" key={`${rule.subject}-${ruleIndex}`}>
                  <header>
                    <div>
                      <strong>{subjectLabels[rule.subject] ?? rule.subject}</strong>
                      <small>{rule.subject}</small>
                    </div>
                    <label>Rule name
                      <input
                        value={rule.name}
                        aria-label={`Rule name ${ruleIndex + 1}`}
                        onChange={(event) => updateReviewRule(ruleIndex, { name: event.target.value })}
                      />
                    </label>
                  </header>
                  <ol className="setupRuleStages">
                    {rule.stages.map((stage, stageIndex) => (
                      <li className="setupRuleStage" key={`${rule.subject}-${stageIndex}`}>
                        <span className="setupRuleStageNumber">{stageIndex + 1}</span>
                        <label>Stage name
                          <input
                            value={stage.name}
                            aria-label={`${rule.subject} stage name ${stageIndex + 1}`}
                            onChange={(event) => updateReviewStage(ruleIndex, stageIndex, { name: event.target.value })}
                          />
                        </label>
                        <label>Signature meaning
                          <select
                            value={stage.kind}
                            aria-label={`${rule.subject} signature meaning ${stageIndex + 1}`}
                            onChange={(event) => updateReviewStage(ruleIndex, stageIndex, { kind: event.target.value as ReviewStage["kind"] })}
                          >
                            <option value="Review">Review</option>
                            <option value="Approval">Approval</option>
                          </select>
                        </label>
                        <label>Required project authority
                          <select
                            value={`${stage.authorityKind ?? ""}:${stage.requiredRole}`}
                            aria-label={`${rule.subject} project authority ${stageIndex + 1}`}
                            onChange={(event) => {
                              const selected = parseAuthorityToken(event.target.value);
                              updateReviewStage(ruleIndex, stageIndex, {
                                authorityKind: selected?.kind ?? null,
                                requiredRole: selected?.value ?? "",
                              });
                            }}
                          >
                            <option value=":">Choose authority…</option>
                            <optgroup label="Base project roles">
                              {baseRoleAuthorities.map((role) => <option key={`base-${role}`} value={authorityToken("BaseRole", role)}>{authorityLabel(role)}</option>)}
                            </optgroup>
                            <optgroup label="Project Leadership">
                              {leadershipAuthorities.map((role) => <option key={`leadership-${role}`} value={authorityToken("LeadershipPosition", role)}>{authorityLabel(role)} — leadership position</option>)}
                            </optgroup>
                          </select>
                        </label>
                        <button type="button" onClick={() => removeReviewStage(ruleIndex, stageIndex)} disabled={rule.stages.length <= 1}>Remove</button>
                      </li>
                    ))}
                  </ol>
                  <button type="button" onClick={() => addReviewStage(ruleIndex)}>Add stage</button>
                </article>
              ))}
            </div>
          ) : (
            <p className="setupFieldError" role="alert">
              The server has not supplied a concrete standard definition for this ladder yet. Save
              the ladder and resume when the standard is available; finalization remains blocked.
            </p>
          )}
          <label className="setupAccept">
            <input
              type="checkbox"
              checked={values.reviewRulesAccepted}
              disabled={!reviewRulesAreComplete(values.reviewRulesDefinition)}
              onChange={(event) => update("reviewRulesAccepted", event.target.checked)}
            />{" "}
            I reviewed and explicitly accept these concrete Review and Approval rules for this ladder.
          </label>
          {values.reviewRulesDefinition && !reviewRulesAreComplete(values.reviewRulesDefinition) && (
            <p className="setupFieldError" role="alert">
              Every rule needs a named stage, at least one Review and one Approval signature, and a
              supported base project role or Project Leadership authority before it can be accepted.
            </p>
          )}
        </section>
      );
    if (currentStep === "Services")
      return (
        <section className="setupStepPanel">
          <h2>Repository setup</h2>
          <p>
            Choose whether to connect now or configure later. A deferred connection remains visibly
            Pending and does not satisfy later repository prerequisites.
          </p>
          <fieldset className="setupChoiceList">
            <legend>Repository</legend>
            <p className="setupFieldHint">Supported provider: GitLab. Credentials stay with the server and are never entered in this browser form.</p>
            <label>
              <input
                type="radio"
                name="repositoryMode"
                checked={values.repository.mode === "ConnectNow"}
                onChange={() => update("repository", { ...values.repository, mode: "ConnectNow", status: "Pending" })}
              />
              Connect now
              <small>
                {values.repository.mode === "ConnectNow"
                  ? `${repositoryStatusLabel(values.repository.status)} until the repository service confirms it.`
                  : "Attempt setup during this walkthrough when supported."}
              </small>
            </label>
            {values.repository.mode === "ConnectNow" && <label className="setupRepositoryEndpoint">GitLab project endpoint (HTTPS)
              <input
                value={values.repository.endpoint}
                onChange={(event) => update("repository", { ...values.repository, endpoint: event.target.value, status: "Pending" })}
                placeholder="https://gitlab.example/group/project"
                autoComplete="off"
              />
              <small>Use the project URL without credentials, query parameters, or a fragment. The server verifies access after saving.</small>
            </label>}
            <label>
              <input
                type="radio"
                name="repositoryMode"
                checked={values.repository.mode === "ConfigureLater"}
                onChange={() => update("repository", { ...values.repository, mode: "ConfigureLater", status: "Pending" })}
              />
              Configure later
              <small>{values.repository.mode === "ConfigureLater" ? "Pending. Unrelated project work can continue after creation." : "Keep this project pending until repository setup is ready."}</small>
            </label>
          </fieldset>
          <p className="setupFieldHint">
            No repository connection is claimed by this screen. The server owns credentials,
            verification, and lifecycle prerequisites.
          </p>
        </section>
      );
    return (
      <section className="setupStepPanel">
        <h2>Review and finish</h2>
        <p>Check the durable answers before saving or finalizing this setup.</p>
        <dl className="setupReviewList">
          <div>
            <dt>Project</dt>
            <dd>
              {values.projectName || "Not provided"} ·{" "}
              {values.softwareProduct || "Software product not provided"}
            </dd>
          </div>
          <div>
            <dt>Starting point</dt>
            <dd>
              {startKindLabel(values.startKind)}
              {values.selectedCategories.length
                ? ` · ${values.selectedCategories.length} inherited categories`
                : " · no inherited categories"}
            </dd>
          </div>
          <div>
            <dt>First build</dt>
            <dd>
              {values.buildVersion || "Not provided"}
              {versionIdentity ? ` · ${versionIdentity}` : ""}
            </dd>
          </div>
          <div>
            <dt>Requirement ladder</dt>
            <dd>
              {values.ladder.steps.length === 0 ? (
                "Not provided"
              ) : (
                <ul className="setupLadderSummary">
                  {values.ladder.steps.map((step) => (
                    <li key={step.catalogueEntry}>
                      <strong>{levelLabel(step.catalogueEntry)}</strong> · {capabilitySummary(step)} ·{" "}
                      {verificationSummary(step, readinessStepForLevel(step.catalogueEntry))}
                    </li>
                  ))}
                </ul>
              )}
            </dd>
          </div>
          <div>
            <dt>Rules</dt>
            <dd>{values.reviewRulesAccepted ? `Explicitly accepted · ${values.reviewRulesDefinition?.rules.length ?? 0} server-supplied rules` : values.reviewRulesDefinition ? "Review and acceptance required" : "Server standard unavailable"}</dd>
          </div>
          <div>
            <dt>Repository</dt>
            <dd>
              {values.repository.mode === "ConnectNow"
                ? `Connect now · ${repositoryStatusLabel(values.repository.status)}${values.repository.endpoint ? ` · ${values.repository.endpoint}` : " · endpoint required"}`
                : `Configure later · ${repositoryStatusLabel(values.repository.status)}`}
            </dd>
          </div>
        </dl>
        {validationIsCurrent && (draft?.validation?.findings.length ?? 0) > 0 && (
          <div className="setupFieldError" role="alert">
            <strong>This saved configuration is not ready to be created</strong>
            <ul>
              {(draft?.validation?.findings ?? []).map((finding) => (
                <li key={`${finding.code}-${finding.level ?? "ladder"}-${finding.token ?? ""}`}>
                  {finding.level ? `${levelLabel(finding.level)} — ` : ""}
                  {finding.message}
                </li>
              ))}
            </ul>
          </div>
        )}
        {validationIsCurrent && draft?.validation?.review.covers === false && draft?.validation?.ladderValid && (
          <p className="setupFieldError" role="alert">
            The accepted review rules do not cover this saved ladder exactly. Use the review-rules step to
            apply the current standard, review it, and accept it again.
          </p>
        )}
        {validationIsCurrent && (draft?.validation?.review.definitionFindings?.length ?? 0) > 0 && (
          <div className="setupFieldError" role="alert">
            <strong>The accepted rule definition is incomplete</strong>
            <ul>
              {(draft?.validation?.review.definitionFindings ?? []).map((finding) => (
                <li key={`${finding.code}-${finding.subject ?? ""}-${finding.stageIndex ?? ""}`}>
                  {finding.subject ? subjectLabels[finding.subject] ?? finding.subject : "Review rules"}
                  {finding.stageIndex ? ` — stage ${finding.stageIndex}` : ""} — {finding.message}
                </li>
              ))}
            </ul>
          </div>
        )}
        {finalizationFindings && finalizationFindings.findings.length > 0 && (
          <div className="setupFieldError" role="alert">
            <strong>What the server refused</strong>
            <p>
              Reported for the saved configuration at version {finalizationFindings.version}. These findings
              are kept with their version so they are never applied as authority over newer answers.
            </p>
            <ul>
              {finalizationFindings.findings.map((finding) => (
                <li
                  key={`${finding.code}-${finding.level ?? finding.subject ?? "ladder"}-${
                    finding.stageIndex ?? ""
                  }`}
                >
                  {finding.level
                    ? `${levelLabel(finding.level)} — `
                    : finding.subject
                      ? `${subjectLabels[finding.subject] ?? finding.subject} — `
                      : ""}
                  {finding.message}
                </li>
              ))}
            </ul>
            {finalizationFindings.version !== draft.version ? (
              <p>
                These findings describe version {finalizationFindings.version}; this draft is now at version{" "}
                {draft.version}. Save and recheck before retrying.
              </p>
            ) : configurationReady ? (
              <p>
                The current saved verdict says this configuration is ready while the earlier attempt was
                refused. Those two facts disagree — recheck the saved configuration before retrying.
              </p>
            ) : null}
            <button type="button" onClick={() => void goTo("Ladder")}>
              Review the requirement ladder
            </button>
          </div>
        )}
        {!hasUnsavedChanges && !validationIsCurrent && (
          <p className="setupPendingNotice" role="status">
            The saved configuration has moved since it was last checked, so this screen cannot say it is
            ready. Recheck it against the server&apos;s maintained rules.{" "}
            <button
              type="button"
              onClick={() => void revalidate()}
              disabled={revalidating || saving || finalizing}
            >
              {revalidating ? "Rechecking…" : "Recheck the saved configuration"}
            </button>
          </p>
        )}
        {values.startKind !== "Fresh" && sourceState.source && (
          <>
            <p className={sourceComplete ? "setupReadyNotice" : "setupPendingNotice"} role="status">
              {sourceComplete
                ? "The exact source snapshot is reconciled and explicitly accepted. Finalization will create the new working build while retaining source facts separately."
                : sourceState.source.reconciliation?.errors.length
                  ? "The source is saved but has reconciliation findings. Resolve them in the Starting point step before finalization."
                  : "The source path is saved for recovery. Reconcile the selected categories and mappings, then accept the server assertion before finalization."}
            </p>
            <SourceAcceptanceFields
              source={sourceState.source}
              accepted={sourceState.assertionAccepted}
              password={sourceState.password}
              disabled={!user.isAdministrator}
              onAcceptedChange={(accepted) => setSourceState((current) => ({ ...current, assertionAccepted: accepted }))}
              onPasswordChange={(password) => setSourceState((current) => ({ ...current, password }))}
            />
          </>
        )}
        {values.startKind !== "Fresh" && !sourceState.source && (
          <p className="setupPendingNotice" role="status">
            This source path is saved for recovery, but no source snapshot is available yet. Finalization remains blocked until a supported source pipeline returns an exact source assertion.
          </p>
        )}
        {values.startKind === "Fresh" && !freshComplete && (
          <p className="setupFieldError" role="alert">
            Complete the project details, supported build version, ladder, and explicit rule
            acceptance before finalization.
          </p>
        )}
        {values.startKind === "Fresh" && freshComplete && hasUnsavedChanges && (
          <p className="setupFieldError" role="alert">
            Save this review before finalization. The server only finalizes answers already
            committed to this draft.
          </p>
        )}
        {values.startKind === "Fresh" && freshComplete && !hasUnsavedChanges && (
          <p className="setupReadyNotice" role="status">
            {user.isAdministrator
              ? "Fresh setup is ready for the server's finalization gate. The resulting build will be In Work."
              : "Your fresh setup answers are saved. An AeroLink administrator must finalize this Project; you can continue editing and resume this draft."}
          </p>
        )}
        {!user.isAdministrator && (
          <p className="setupPendingNotice" role="status">
            Only an AeroLink administrator can accept source facts or create a Project. You can
            still review, edit, and save this setup draft.
          </p>
        )}
        <button
          type="button"
          className="setupFinalizeButton"
          disabled={!user.isAdministrator || finalizing || saving || (!freshComplete && !sourceComplete) || hasUnsavedChanges}
          onClick={() => void finalize()}
        >
          {finalizing ? "Finalizing…" : "Create Project"}
        </button>
      </section>
    );
  };

  return (
    <div className="projectSetupPage">
      <PortalHeader user={user} onSignOut={onSignOut} />
      <main className="projectSetupMain">
        <nav className="projectSetupBreadcrumb" aria-label="Breadcrumb">
          <button type="button" onClick={onExit}>
            Projects
          </button>
          <span aria-hidden="true">/</span>
          <strong>New Project</strong>
        </nav>
        <header className="projectSetupHeading">
          <div>
            <p className="eyebrow">PROJECT SETUP · RECOVERABLE DRAFT</p>
            <h1>Create New Project</h1>
            <p>
              Save progress at every step. This draft remains separate from a usable Project until
              the server accepts finalization.
            </p>
          </div>
          <button
            type="button"
            onClick={() => void saveDraft(true, currentStep, undefined, true)}
            disabled={saving || finalizing}
          >
            {saving ? "Saving…" : "Save and exit"}
          </button>
        </header>
        {error && (
          <p className="projectSetupError" role="alert">
            {error}
          </p>
        )}
        {notice && (
          <p className="projectSetupNotice" role="status">
            {notice}
          </p>
        )}
        <div className="projectSetupLayout">
          <nav className="projectSetupSteps" aria-label="Project setup steps">
            <ol>
              {steps.map((step, index) => (
                <li key={step.id}>
                  <button
                    type="button"
                    className={
                      step.id === currentStep ? "selected" : index < activeIndex ? "complete" : ""
                    }
                    onClick={() => void goTo(step.id)}
                    disabled={saving || finalizing}
                  >
                    {index + 1}. {step.label}
                    <small>
                      {step.id === currentStep
                        ? "Current"
                        : index < activeIndex
                          ? "Saved answer"
                          : "Not reviewed"}
                    </small>
                  </button>
                </li>
              ))}
            </ol>
            <p className="projectSetupSavedState">
              {hasUnsavedChanges
                ? "Unsaved changes"
                : draft.lastSavedAt
                  ? `Saved ${new Date(draft.lastSavedAt).toLocaleString()}`
                  : "Draft created"}
            </p>
          </nav>
          <section className="projectSetupContent">
            {renderStep()}
            {!completedResult && (
              <footer className="projectSetupFooter">
                <button
                  type="button"
                  onClick={() => {
                    const previous = steps[Math.max(activeIndex - 1, 0)];
                    if (previous) void goTo(previous.id);
                  }}
                  disabled={activeIndex === 0 || saving || finalizing}
                >
                  Back
                </button>
                <span>
                  Step {activeIndex + 1} of {steps.length}
                </span>
                {activeIndex < steps.length - 1 ? (
                  <button
                    type="button"
                    className="primarySetupAction"
                    onClick={() => {
                      const next = steps[activeIndex + 1];
                      if (next) void goTo(next.id);
                    }}
                    disabled={saving || finalizing}
                  >
                    {saving ? "Saving…" : "Continue"}
                  </button>
                ) : (
                  <button
                    type="button"
                    className="primarySetupAction"
                    onClick={() => void saveDraft()}
                    disabled={saving || finalizing || !hasUnsavedChanges}
                  >
                    {saving ? "Saving…" : "Save review"}
                  </button>
                )}
              </footer>
            )}
          </section>
        </div>
      </main>
    </div>
  );
}
