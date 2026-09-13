import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { AuthUser } from "./IdentityCenter";
import PortalHeader from "./PortalHeader";
import { apiRequest, operationError } from "./apiClient";
import { buildVersionOrder, officialBuildName } from "./presentation";
import { isSetupStep, type SetupStep } from "./projectSetupDrafts";
import {
  authorityLabel,
  authorityToken,
  baseRoleAuthorities,
  leadershipAuthorities,
  parseAuthorityToken,
} from "./workflowAuthorities";
import "./ProjectSetupWalkthrough.css";

type StartKind = "Fresh" | "AeroLinkBaseline" | "ExternalBaseline";
type VerificationKind = "Case" | "Procedure";
type LadderStep = {
  catalogueEntry: string;
  position: number;
  capabilities: number;
  enabledArtifactKinds?: VerificationKind[];
};
type LadderDefinition = { steps: LadderStep[]; relationships: { parent: string; child: string }[] };
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
const inheritedCategories = [
  { id: "Requirements", label: "Requirements and traceability" },
  { id: "Verification", label: "Verification artifacts and evidence" },
  { id: "ChangeRequests", label: "Change requests and decisions" },
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

function normalizeLadder(value: unknown): LadderDefinition {
  const source = asObject(value);
  const rawSteps = Array.isArray(source.steps) ? source.steps : [];
  const parsed = rawSteps.flatMap((item, index) => {
    const row = asObject(item);
    const catalogueEntry = typeof row.catalogueEntry === "string" ? row.catalogueEntry : "";
    const catalogue = levelCatalogue.find((level) => level.id === catalogueEntry);
    if (!catalogue) return [];
    const enabled = Array.isArray(row.enabledArtifactKinds)
      ? row.enabledArtifactKinds.filter(
          (kind): kind is VerificationKind => kind === "Case" || kind === "Procedure",
        )
      : [...catalogue.verification];
    return [
      {
        catalogueEntry,
        position: typeof row.position === "number" ? row.position : index + 1,
        capabilities:
          typeof row.capabilities === "number" ? row.capabilities : catalogue.capabilities,
        enabledArtifactKinds: enabled.length ? enabled : [...catalogue.verification],
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
    selectedCategories: values.selectedCategories,
    ladder: values.ladder,
    reviewRules: values.reviewRulesDefinition ?? {},
    reviewRulesAccepted: values.reviewRulesAccepted,
    repository: {
      mode: values.repository.mode,
      provider: values.repository.provider || "GitLab",
      endpoint: values.repository.mode === "ConnectNow" ? values.repository.endpoint || null : null,
    },
    mapping: values.mapping,
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
}: {
  user: AuthUser;
  api: string;
  draftId?: string;
  onExit: () => void;
  onSignOut: () => void;
  onCompleted: (result: FinalizationResult) => void;
}) {
  const [draft, setDraft] = useState<SetupDraft>();
  const [values, setValues] = useState<SetupValues>();
  const [currentStep, setCurrentStep] = useState<SetupStep>("Details");
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [finalizing, setFinalizing] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const finalizationKey = useRef<string | undefined>(undefined);

  const loadDraft = useCallback(
    async (id: string) => {
      setLoading(true);
      setError("");
      try {
        const loaded = await apiRequest<SetupDraft>(`${api}/api/project-setups/${id}`);
        setDraft(loaded);
        setValues(valuesFromDraft(loaded));
        setCurrentStep(loaded.currentStep);
      } catch (failure) {
        setError(
          operationError(
            failure,
            "The setup draft could not be loaded. Your saved answers remain on the server.",
          ),
        );
      } finally {
        setLoading(false);
      }
    },
    [api],
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
        setDraft(createdDraft);
        setValues(valuesFromDraft(createdDraft));
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
  }, [api, draftId, loadDraft]);

  const update = <K extends keyof SetupValues>(key: K, value: SetupValues[K]) => {
    setValues((current) => {
      if (!current) return current;
      const next = { ...current, [key]: value };
      // A changed ladder or rule definition must be reviewed and accepted again. Keeping the old
      // checkbox checked would make the server hash a new definition under stale user intent.
      if (key === "ladder" || key === "reviewRulesDefinition") next.reviewRulesAccepted = false;
      return next;
    });
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
  const completedDraft = draft?.state === "Completed" ? draft : undefined;
  const completedResult: FinalizationResult | undefined =
    completedDraft?.finalization?.programId &&
    completedDraft.finalization.projectId &&
    completedDraft.finalization.releaseId
      ? {
          state: "Completed",
          alreadyCompleted: true,
          programId: completedDraft.finalization.programId,
          projectId: completedDraft.finalization.projectId,
          releaseId: completedDraft.finalization.releaseId,
          version: completedDraft.build.version,
          officialBuildName:
            completedDraft.build.officialName ??
            officialBuildName(completedDraft.build.version) ??
            "Identity unavailable",
        }
      : undefined;
  const freshComplete =
    values?.startKind === "Fresh" &&
    Boolean(values.projectName.trim()) &&
    Boolean(values.softwareProduct.trim()) &&
    versionOrder !== undefined &&
    values.ladder.steps.length > 0 &&
    values.reviewRulesAccepted &&
    reviewRulesAreComplete(values.reviewRulesDefinition);
  const reviewRulesDelta = reviewRulesSubjectDelta(
    values?.reviewRulesDefinition,
    values?.suggestedReviewRulesDefinition,
  );
  const reviewRulesNeedRefresh = Boolean(
    reviewRulesDelta && (reviewRulesDelta.added.length || reviewRulesDelta.removed.length),
  );

  const saveDraft = async (exitAfterSave = false, stepToSave = currentStep) => {
    if (!draft || !values) return false;
    if (values.startKind === "AeroLinkBaseline" && !isUuid(values.sourceBaselineId)) {
      setError(
        "Enter the exact authorized AeroLink baseline ID as a UUID before saving this starting point.",
      );
      return false;
    }
    if (values.startKind === "ExternalBaseline" && !isUuid(values.sourceImportId)) {
      setError(
        "Enter the exact staged external import ID as a UUID before saving this starting point.",
      );
      return false;
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
          body: JSON.stringify(requestBody(values, stepToSave, draft.version)),
        },
      );
      const saved = "draft" in response ? response.draft : response;
      setDraft(saved);
      setValues(valuesFromDraft(saved));
      setCurrentStep(saved.currentStep);
      setNotice("Saved on the server. You can resume this setup after signing out or restarting.");
      if (exitAfterSave) onExit();
      return true;
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

  const finalize = async () => {
    if (!draft || !values || !freshComplete) return;
    setFinalizing(true);
    setError("");
    setNotice("");
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
          }),
        },
      );
      onCompleted(result);
    } catch (failure) {
      setError(
        `${operationError(failure, "The project could not be finalized.")} Retry with the same request; the server protects against duplicate project creation.`,
      );
      await loadDraft(draft.draftId);
    } finally {
      setFinalizing(false);
    }
  };

  const goTo = async (target: SetupStep) => {
    if (target === currentStep) return;
    if ((hasUnsavedChanges || target !== draft?.currentStep) && !(await saveDraft(false, target)))
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
          {values.startKind === "AeroLinkBaseline" && (
            <label className="setupWideField">
              Exact AeroLink baseline ID
              <input
                value={values.sourceBaselineId}
                onChange={(event) => update("sourceBaselineId", event.target.value)}
                placeholder="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
              />
            </label>
          )}
          {values.startKind === "ExternalBaseline" && (
            <label className="setupWideField">
              Staged external import ID
              <input
                value={values.sourceImportId}
                onChange={(event) => update("sourceImportId", event.target.value)}
                placeholder="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
              />
            </label>
          )}
          {values.startKind && values.startKind !== "Fresh" && (
            <fieldset className="setupChoiceList">
              <legend>Inherited categories</legend>
              {inheritedCategories.map((category) => (
                <label key={category.id}>
                  <input
                    type="checkbox"
                    checked={values.selectedCategories.includes(category.id)}
                    onChange={(event) =>
                      update(
                        "selectedCategories",
                        event.target.checked
                          ? [...values.selectedCategories, category.id]
                          : values.selectedCategories.filter((item) => item !== category.id),
                      )
                    }
                  />
                  {category.label}
                </label>
              ))}
              <small>
                Dependency and source checks remain server-owned. Finalization stays unavailable
                until the source pipeline is delivered.
              </small>
            </fieldset>
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
                      return (
                        <label key={label}>
                          <input
                            type="checkbox"
                            checked={allowed && (step.capabilities & (1 << capabilityIndex)) !== 0}
                            disabled={!allowed}
                            onChange={(event) =>
                              updateStep({
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
                  {(step.catalogueEntry === "HighLevel" || step.catalogueEntry === "LowLevel") && (
                    <label>
                      Verification profile
                      <select
                        value={
                          step.enabledArtifactKinds?.includes("Procedure")
                            ? "Case+Procedure"
                            : "Case"
                        }
                        onChange={(event) =>
                          updateStep({
                            enabledArtifactKinds:
                              event.target.value === "Case+Procedure"
                                ? ["Case", "Procedure"]
                                : ["Case"],
                          })
                        }
                      >
                        <option value="Case">Case-only</option>
                        <option value="Case+Procedure">Case + Procedure</option>
                      </select>
                    </label>
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
              {values.startKind || "Not chosen"}
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
              {values.ladder.steps
                .map(
                  (step) =>
                    levelCatalogue.find((level) => level.id === step.catalogueEntry)?.label ??
                    step.catalogueEntry,
                )
                .join(" → ") || "Not provided"}
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
        {values.startKind !== "Fresh" && (
          <p className="setupPendingNotice" role="status">
            This source path is saved for recovery, but this installation's source pipeline has not
            reported a materializable result. Finalization remains blocked until it does.
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
            Fresh setup is ready for the server's finalization gate. The resulting build will be In
            Work.
          </p>
        )}
        <button
          type="button"
          className="setupFinalizeButton"
          disabled={finalizing || saving || !freshComplete || hasUnsavedChanges}
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
            onClick={() => void saveDraft(true)}
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
