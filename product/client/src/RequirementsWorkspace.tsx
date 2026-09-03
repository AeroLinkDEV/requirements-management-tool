import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { PersonName } from "./People";
import { artifactAcronym, coverageLabel, stateLabel, verificationArtifactNoun } from './presentation'
import { apiRequest, operationError, recordClientOperationFailure } from './apiClient'
import type { FormEvent } from "react";
import { AutosaveState, DraftRestore } from "./DraftNotice";
import { useFormDraft } from "./autosave";
import DocumentActions from "./DocumentActions";
import {
  ControlledArtifactExplorerHeader,
  ControlledArtifactExplorerLayout,
  ControlledArtifactInspector,
} from "./ControlledArtifactExplorer";
import { targetsFor } from "./presentation";
import { LadderCapability, ladderAllows, ladderHasAny } from "./projectLadder";
import type { ProjectLadderProjection } from "./projectLadder";
import ExactArtifactLink from "./ExactArtifactLink";
import "./RequirementsWorkspace.css";

type Field = {
  id: string;
  key: string;
  label: string;
  type: string;
  isRequired: boolean;
  sortOrder: number;
  optionsJson: string;
};
type Schema = {
  id: string;
  key: string;
  name: string;
  appliesTo: string;
  description: string;
  version: number;
  fields: Field[];
};
type Section = { id: string; heading: string; position: number; count: number };
type Specification = {
  id: string;
  documentNumber: string;
  title: string;
  level: string;
  description: string;
  nodeCount: number;
  sections: Section[];
};
type SavedView = {
  id: string;
  name: string;
  queryJson: string;
  columnsJson: string;
  isShared: boolean;
  owned: boolean;
};
type Requirement = {
  id: string;
  baseNumber: string;
  displayNumber: string;
  level: string;
  revisionId: string;
  revision: number;
  statement: string;
  rationale: string;
  verificationMethod: string;
  state: string;
  // The server's name, not a convenient shorthand for it. This was declared as `sourceScrId` and the server
  // has always sent `sourceChangeRequestId`, so every read produced undefined and every link built a route
  // ending in the literal string "undefined". A hand-written interface asserts what the payload contains; it
  // does not check, so the mismatch type-checked cleanly for as long as it existed.
  sourceChangeRequestId: string;
  /** The build that owns the change request, which for a historical revision is an earlier one. */
  sourceChangeRequestReleaseId: string | null;
  sourceScr: string;
  createdAt: string;
  richText: string;
  attributesJson: string;
  tagsJson: string;
  commentCount: number;
  openCommentCount: number;
  /** Covered, Suspect or Uncovered — the server's single definition, never recomputed here. */
  coverageState: string;
};
type Workspace = {
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  queryElapsedMs: number;
  schemas: Schema[];
  specifications: Specification[];
  views: SavedView[];
  items: Requirement[];
};
type History = {
  id: string;
  revision: number;
  displayNumber: string;
  statement: string;
  rationale: string;
  verificationMethod: string;
  state: string;
  // The server's name, not a convenient shorthand for it. This was declared as `sourceScrId` and the server
  // has always sent `sourceChangeRequestId`, so every read produced undefined and every link built a route
  // ending in the literal string "undefined". A hand-written interface asserts what the payload contains; it
  // does not check, so the mismatch type-checked cleanly for as long as it existed.
  sourceChangeRequestId: string;
  /** The build that owns the change request, which for a historical revision is an earlier one. */
  sourceChangeRequestReleaseId: string | null;
  sourceScr: string;
  createdAt: string;
  originBuild: string;
  isHistorical: boolean;
  attributesJson: string;
  tagsJson: string;
};
type Detail = {
  id: string;
  baseNumber: string;
  level: string;
  history: History[];
  placements: {
    id: string;
    documentNumber: string;
    title: string;
    section: string;
    position: number;
  }[];
  traceCount: number;
  testCoverageCount: number;
};
type ImpactItem = {
  id: string;
  displayNumber?: string;
  buildNumber?: string;
  documentNumber?: string;
  title?: string;
  statement?: string;
  level?: string;
  state?: string;
  release?: string;
  baseline?: string;
  name?: string;
  type?: string;
  revisionId?: string;
  artifactRevisionId?: string;
  artifactKind?: string;
  isSuspect?: boolean;
  coverageState?: "Confirmed" | "Suspect";
};
type Impact = {
  parents: ImpactItem[];
  children: ImpactItem[];
  tests: ImpactItem[];
  baselines: ImpactItem[];
  builds: ImpactItem[];
  documents: ImpactItem[];
  activeChanges: (ImpactItem & { kind?: string; proposedRevision?: number })[];
};
type ProposalOption = {
  id: string;
  baseNumber: string;
  revision: number;
  displayNumber: string;
  title: string;
  state: string;
  targetReleaseId: string;
  type: string;
  softwareLevel?: string | null;
  version: number;
  requirementCount: number;
  eligible: boolean;
  existingProposalId?: string | null;
  reason?: string | null;
  heldBy?: string | null;
  heldByCurrentUser?: boolean;
};
type ProposalOptions = {
  requirement: { id: string; baseNumber: string; level: string; revisionId: string; revision: number; displayNumber: string };
  targetRelease: { id: string; version: string; isReleased: boolean };
  drafts: ProposalOption[];
};
type Comment = {
  id: string;
  parentCommentId?: string;
  body: string;
  mentionsJson: string;
  state: string;
  createdBy: string;
  createdAt: string;
  resolvedBy?: string;
  resolvedAt?: string;
  disposition?: string;
};
type Props = {
  api: string;
  projectId: string;
  scope: "System" | "Software";
  ladder: ProjectLadderProjection | null;
  /**
   * The build being read. It decides which document these requirements belong to — the approved one for a
   * released build, a stamped draft for an in-work one — so the reader does not have to leave the requirements
   * to go and find it on the Digital Thread.
   */
  release?: { id: string; version: string; isReleased: boolean };
  initialViewId?: string;
  initialArtifactId?: string;
  /** The exact requirement revision a procedure trace deep link opens, when one is named. */
  initialRevisionId?: string;
  onBack: () => void;
  /** The owning build travels with the identifier so a change request opens in its own context. */
  onOpenScr: (id: string, owningReleaseId?: string | null, proposalId?: string) => void;
  onProposeChange: (requirementId: string, level?: Requirement["level"]) => void;
  onOpenRequirement: (id: string) => void;
  requirementHref: (id: string) => string;
  /** Native exact link for a controlled requirement revision. Aggregate routes are not a safe fallback here. */
  requirementRevisionHref?: (requirement: { id: string; revisionId: string; level: string }) => string | undefined;
  /** SPA selection for an exact revision; native modified clicks remain on the exact href. */
  onOpenRequirementRevision?: (requirement: { id: string; revisionId: string; level: string }) => void;
  onCloseRequirement: () => void;
  onOpenTraceability: (artifactId?: string) => void;
  verificationArtifactHref?: (artifact: { artifactId: string; procedureId?: string; revisionId?: string; artifactRevisionId?: string; artifactKind?: string; displayNumber?: string; level?: string }) => string | undefined;
  onOpenVerification: (artifact?: { artifactId: string; procedureId?: string; revisionId?: string; artifactRevisionId?: string; artifactKind?: string; displayNumber?: string; level?: string }) => void;
};

const parseTags = (json: string) => {
  try {
    return JSON.parse(json) as string[];
  } catch {
    return [];
  }
};

export default function RequirementsWorkspace({
  api,
  projectId,
  scope,
  ladder,
  release,
  initialViewId,
  initialArtifactId,
  initialRevisionId,
  onBack,
  onOpenScr,
  onProposeChange,
  onOpenRequirement,
  requirementHref,
  requirementRevisionHref,
  onOpenRequirementRevision,
  onCloseRequirement,
  onOpenTraceability,
  verificationArtifactHref,
  onOpenVerification,
}: Props) {
  const appliedInitialView = useRef(false);
  const autoSelected = useRef(false);
  const proposalTrigger = useRef<HTMLButtonElement>(null);
  const proposalSearchInput = useRef<HTMLInputElement>(null);
  const loadGeneration = useRef(0);
  // Inspector fetches answer for one requirement, and a slower earlier read must never replace a newer
  // one: open() assigns the selected requirement before its responses arrive, so the comment form can be
  // used while that requirement's own detail and comment requests are still in flight — and a successful
  // comment POST starts a newer comment read whose result the still-pending pre-create read must not
  // overwrite (the "successful comment disappeared" defect). Each state stream therefore carries a
  // monotonic intent token plus the requirement the intent was issued for; a response may only assign
  // state when it is still the latest intent and still matches the requirement being shown.
  const detailIntent = useRef(0);
  const detailTarget = useRef<string | undefined>(undefined);
  const commentIntent = useRef(0);
  const commentTarget = useRef<string | undefined>(undefined);
  const [data, setData] = useState<Workspace>(),
    [loading, setLoading] = useState(true),
    [search, setSearch] = useState(""),
    [level, setLevel] = useState<string>(scope),
    [verification, setVerification] = useState(""),
    [tag, setTag] = useState(""),
    [stateFilter, setStateFilter] = useState(""),
    [owner, setOwner] = useState(""),
    [sourceScr, setSourceScr] = useState(""),
    [openComments, setOpenComments] = useState(false),
    [coverageState, setCoverageState] = useState(""),
    [sort, setSort] = useState("identifier"),
    [showAdvanced, setShowAdvanced] = useState(false),
    [specificationId, setSpecificationId] = useState(""),
    [sectionId, setSectionId] = useState(""),
    [page, setPage] = useState(1),
    [pageSize, setPageSize] = useState(25),
    [mode, setMode] = useState<"table" | "document">("table"),
    [selected, setSelected] = useState<Requirement>(),
    [detail, setDetail] = useState<Detail>(),
    [impact, setImpact] = useState<Impact>(),
    [comments, setComments] = useState<Comment[]>([]),
    [deepLinkMissing, setDeepLinkMissing] = useState(false),
    [inspectorTab, setInspectorTab] = useState<
      "details" | "trace" | "history" | "discussion"
    >("details"),
    [error, setError] = useState(""),
    [showSave, setShowSave] = useState(false),
    [showSchema, setShowSchema] = useState(false),
    [redline, setRedline] = useState<{
      from: number;
      to: number;
      statement: { kind: string; text: string }[];
      rationale: { kind: string; text: string }[];
      verificationChanged: boolean;
      fromVerification: string;
      toVerification: string;
    }>(),
    [proposalTarget, setProposalTarget] = useState<Requirement>(),
    [proposalSearch, setProposalSearch] = useState(""),
    [proposalOptions, setProposalOptions] = useState<ProposalOptions>(),
    [proposalLoading, setProposalLoading] = useState(false),
    [proposalSaving, setProposalSaving] = useState(false),
    [proposalError, setProposalError] = useState("");
  useEffect(() => {
    autoSelected.current = false;
    setLevel(scope);
    setSpecificationId("");
    setSectionId("");
    setPage(1);
    setSelected(undefined);
    setDeepLinkMissing(false);
  }, [scope]);
  useEffect(() => {
    if (!proposalTarget || !release) return;
    let cancelled = false;
    setProposalLoading(true);
    setProposalError("");
    const query = new URLSearchParams({ targetReleaseId: release.id });
    if (proposalSearch.trim()) query.set("search", proposalSearch.trim());
    fetch(`${api}/api/enterprise-requirements/${proposalTarget.id}/propose-options?${query}`)
      .then(async (response) => {
        if (!response.ok) {
          const body = (await response.json().catch(() => ({}))) as { error?: string };
          throw new Error(body.error || "Eligible change requests could not be loaded.");
        }
        return response.json() as Promise<ProposalOptions>;
      })
      .then((value) => { if (!cancelled) setProposalOptions(value); })
      .catch((reason: unknown) => {
        if (!cancelled) setProposalError(reason instanceof Error ? reason.message : "Eligible change requests could not be loaded.");
      })
      .finally(() => { if (!cancelled) setProposalLoading(false); });
    return () => { cancelled = true; };
  }, [api, proposalSearch, proposalTarget, release]);
  useEffect(() => {
    if (proposalTarget) requestAnimationFrame(() => proposalSearchInput.current?.focus());
  }, [proposalTarget]);
  const params = useMemo(() => {
    const p = new URLSearchParams({
      projectId,
      page: String(page),
      pageSize: String(pageSize),
      sort,
    });
    if (search) p.set("search", search);
    // The scope is which explorer this is, not a filter somebody chose, so it is a floor rather than a
    // default. Two paths cleared `level` to empty — applying a saved view that carried none, and selecting a
    // specification — and an empty level means *no* level constraint, so the System Requirements Explorer
    // listed all 1,250 requirements with HLR-000001 at the top. Nothing looked wrong: the level control is a
    // disabled select holding one option, and a select whose value matches no option still displays the
    // first, so it went on reading "System requirements" while sending nothing of the kind.
    const effectiveLevel = level || scope;
    if (effectiveLevel) p.set("level", effectiveLevel);
    if (verification) p.set("verification", verification);
    if (tag) p.set("tag", tag);
    if (stateFilter) p.set("state", stateFilter);
    if (owner) p.set("owner", owner);
    if (sourceScr) p.set("sourceScr", sourceScr);
    if (openComments) p.set("openComments", "true");
    if (coverageState) p.set("coverageState", coverageState);
    if (specificationId) p.set("specificationId", specificationId);
    if (sectionId) p.set("sectionId", sectionId);
    if (release?.id) p.set("releaseId", release.id);
    return p;
  }, [
    projectId,
    page,
    pageSize,
    search,
    level,
    scope,
    verification,
    tag,
    stateFilter,
    owner,
    sourceScr,
    openComments,
    coverageState,
    sort,
    specificationId,
    sectionId,
    release?.id,
  ]);
  /**
   * Only the specifications this explorer is about.
   *
   * The rail listed every specification in the project, so the System explorer offered HLRD-000001 and
   * LLRD-000001 — documents it cannot show a single requirement from. Selecting one also cleared the level,
   * which is how the explorer ended up listing all three levels at once.
   */
  const scopedSpecifications = useMemo(
    () =>
      (data?.specifications ?? []).filter((spec) =>
        scope === "System" ? spec.level === "System" : spec.level === "HighLevel" || spec.level === "LowLevel",
      ),
    [data?.specifications, scope],
  );
  const load = useCallback(async () => {
    const generation = ++loadGeneration.current;
    setLoading(true);
    try {
      const response = await fetch(
        `${api}/api/enterprise-requirements/workspace?${params}`,
      );
      if (generation !== loadGeneration.current) return;
      if (response.ok) {
        const payload = await response.json();
        if (generation !== loadGeneration.current) return;
        setData(payload);
        setError("");
      } else
        setError(
          (await response.json()).error ||
            "Requirements workspace could not be loaded.",
        );
    } catch {
      if (generation !== loadGeneration.current) return;
      setError(
        "Requirements could not be loaded. Check the AeroLink service and try again.",
      );
    } finally {
      if (generation === loadGeneration.current) setLoading(false);
    }
  }, [api, params]);
  useEffect(() => {
    const timer = setTimeout(load, 180);
    return () => clearTimeout(timer);
  }, [load]);
  const loadComments = async (artifactId: string) => {
    const intent = ++commentIntent.current;
    const response = await fetch(
      `${api}/api/enterprise-requirements/${artifactId}/comments`,
    );
    if (
      response.ok &&
      intent === commentIntent.current &&
      commentTarget.current === artifactId
    )
      setComments(await response.json());
  };
  const open = useCallback(async (item: Requirement) => {
    setDeepLinkMissing(false);
    setSelected(item);
    setInspectorTab("details");
    // Issuing an intent for this requirement retires every pending fetch of the same streams — including
    // this requirement's own earlier in-flight comment read, which a successful addComment would otherwise
    // race against.
    const detailRequest = ++detailIntent.current;
    const commentRequest = ++commentIntent.current;
    detailTarget.current = item.id;
    commentTarget.current = item.id;
    const [a, b, c] = await Promise.all([
      fetch(`${api}/api/enterprise-requirements/${item.id}${release?.id ? `?releaseId=${release.id}` : ""}`),
      fetch(`${api}/api/enterprise-requirements/${item.id}/comments`),
      fetch(`${api}/api/enterprise-requirements/${item.id}/impact${release?.id ? `?releaseId=${release.id}` : ""}`),
    ]);
    if (a.ok && detailRequest === detailIntent.current && detailTarget.current === item.id)
      setDetail(await a.json());
    if (b.ok && commentRequest === commentIntent.current && commentTarget.current === item.id)
      setComments(await b.json());
    if (c.ok && detailRequest === detailIntent.current && detailTarget.current === item.id)
      setImpact(await c.json());
  }, [api, release?.id]);
  useEffect(() => {
    if (
      autoSelected.current ||
      initialArtifactId ||
      loading ||
      !data?.items.length ||
      selected
    )
      return;
    autoSelected.current = true;
    void open(data.items[0]);
  }, [data?.items, initialArtifactId, loading, open, selected]);
  useEffect(() => {
    if (!initialArtifactId || selected?.id === initialArtifactId) return;
    let cancelled = false;
    setDeepLinkMissing(false);
    const detailRequest = ++detailIntent.current;
    const commentRequest = ++commentIntent.current;
    detailTarget.current = initialArtifactId;
    commentTarget.current = initialArtifactId;
    (async () => {
      const [detailResponse, commentsResponse, impactResponse] = await Promise.all([
        fetch(`${api}/api/enterprise-requirements/${initialArtifactId}${release?.id ? `?releaseId=${release.id}` : ""}`),
        fetch(
          `${api}/api/enterprise-requirements/${initialArtifactId}/comments`,
        ),
        fetch(`${api}/api/enterprise-requirements/${initialArtifactId}/impact${release?.id ? `?releaseId=${release.id}` : ""}`),
      ]);
      if (!detailResponse.ok)
      {
        if (!cancelled) setDeepLinkMissing(true);
        return;
      }
      const value: Detail = await detailResponse.json();
      // Fail closed: an explicit requirementRevisionId that does not belong to this requirement must never
      // silently become "the latest revision". "Open revision X" that cannot be honored is an unavailable
      // record, not permission to substitute revision Y.
      const requested = initialRevisionId
        ? value.history.find((entry) => entry.id === initialRevisionId)
        : undefined;
      if (initialRevisionId && !requested)
      {
        if (!cancelled) setDeepLinkMissing(true);
        return;
      }
      const latest = requested ?? value.history[0];
      if (!latest || cancelled) return;
      // A newer selection has taken over while this deep link was in flight; it owns the detail, the
      // selection and the inspector, so this response retires without moving any of them.
      if (detailRequest !== detailIntent.current || detailTarget.current !== initialArtifactId) return;
      const item: Requirement = {
        id: value.id,
        baseNumber: value.baseNumber,
        displayNumber: latest.displayNumber,
        level: value.level,
        revisionId: latest.id,
        revision: latest.revision,
        statement: latest.statement,
        rationale: latest.rationale,
        verificationMethod: latest.verificationMethod,
        state: latest.state,
        sourceChangeRequestId: latest.sourceChangeRequestId,
        sourceChangeRequestReleaseId: latest.sourceChangeRequestReleaseId,
        sourceScr: latest.sourceScr,
        createdAt: latest.createdAt,
        richText: latest.statement,
        attributesJson: latest.attributesJson,
        tagsJson: latest.tagsJson,
        commentCount: 0,
        openCommentCount: 0,
        // Synthesized from the detail endpoint to select a deep-linked requirement, never rendered as a
        // list row. Left empty rather than guessed, because a coverage state this screen did not compute
        // is exactly the kind of value that becomes believed once it is displayed.
        coverageState: "",
      };
      setDetail(value);
      setSelected(item);
      setInspectorTab("details");
      if (
        commentsResponse.ok &&
        !cancelled &&
        commentRequest === commentIntent.current &&
        commentTarget.current === initialArtifactId
      )
        setComments(await commentsResponse.json());
      if (
        impactResponse.ok &&
        !cancelled &&
        detailRequest === detailIntent.current &&
        detailTarget.current === initialArtifactId
      )
        setImpact(await impactResponse.json());
    })();
    return () => {
      cancelled = true;
    };
  }, [api, initialArtifactId, initialRevisionId, release?.id, selected?.id]);
  const clearFilters = () => {
    setSearch("");
    setLevel(scope);
    setVerification("");
    setTag("");
    setStateFilter("");
    setOwner("");
    setSourceScr("");
    setOpenComments(false);
    setCoverageState("");
    setSort("identifier");
    setSpecificationId("");
    setSectionId("");
    setPage(1);
  };
  const requirementIdentity = (item: ImpactItem) => item.revisionId
    ? { id: item.id, revisionId: item.revisionId, level: item.level ?? scope }
    : undefined;
  const focusImpactRequirement = (item: ImpactItem) => {
    const identity = requirementIdentity(item);
    if (identity && onOpenRequirementRevision) onOpenRequirementRevision(identity);
    else onOpenRequirement(item.id);
  };
  const impactRequirementLink = (item: ImpactItem) => {
    const identity = requirementIdentity(item);
    return <ExactArtifactLink
      href={identity ? requirementRevisionHref?.(identity) : undefined}
      onOpen={identity && onOpenRequirementRevision ? () => onOpenRequirementRevision(identity) : undefined}
      title={identity ? "Open this exact requirement revision" : "This requirement revision is not available as an exact link"}
    >{item.displayNumber}</ExactArtifactLink>;
  };
  const rowRequirementHref = (item: Requirement) => requirementRevisionHref?.({
    id: item.id,
    revisionId: item.revisionId,
    level: item.level,
  }) ?? requirementHref(item.id);
  const openRequirementRow = (item: Requirement) => onOpenRequirementRevision?.({
    id: item.id,
    revisionId: item.revisionId,
    level: item.level,
  }) ?? onOpenRequirement(item.id);
  /**
   * Owner lifecycle. The server is the authority — it answers Not Found for a view that is not yours — so
   * these read the same failure the API reports rather than guessing at one.
   */
  const mutateView = async (view: SavedView, method: "PUT" | "DELETE", body?: unknown) => {
    setError("");
    try {
      await apiRequest(`${api}/api/enterprise-requirements/views/${view.id}`, {
        method,
        ...(body === undefined ? {} : { headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) }),
      });
      await load();
    } catch (reason) {
      recordClientOperationFailure("enterprise.view.lifecycle", reason);
      setError(operationError(reason, "The saved view could not be updated."));
    }
  };
  const renameView = async (view: SavedView) => {
    const name = prompt("Rename saved view", view.name);
    if (name === null || name.trim() === "" || name.trim() === view.name) return;
    await mutateView(view, "PUT", { name: name.trim() });
  };
  const shareView = (view: SavedView, isShared: boolean) => mutateView(view, "PUT", { isShared });
  const deleteView = async (view: SavedView) => {
    if (!confirm(`Delete the saved view "${view.name}"? Anyone holding its link will no longer be able to open it.`)) return;
    await mutateView(view, "DELETE");
  };
  const applyView = useCallback((view: SavedView) => {
    try {
      const q = JSON.parse(view.queryJson);
      setSearch(q.search || "");
      const savedLevel = typeof q.level === "string" ? q.level : "";
      const savedLevelAllowed = savedLevel === "System"
        ? ladderAllows(ladder, "System")
        : savedLevel === "Software"
          ? ladderHasAny(ladder, ["HighLevel", "LowLevel"])
          : savedLevel === "HighLevel" || savedLevel === "LowLevel"
            ? ladderAllows(ladder, savedLevel)
            : false;
      setLevel(savedLevelAllowed ? savedLevel : scope === "System" ? "System" : "Software");
      setVerification(q.verification || "");
      setTag(q.tag || "");
      setStateFilter(q.state || "");
      setOwner(q.owner || "");
      setSourceScr(q.sourceScr || "");
      setOpenComments(!!q.openComments);
      setCoverageState(q.coverageState || "");
      setSort(q.sort || "identifier");
      setSpecificationId(q.specificationId || "");
      setPage(1);
    } catch {
      setError("Saved view configuration is invalid.");
    }
  }, [ladder, scope]);
  useEffect(() => {
    if (!appliedInitialView.current && initialViewId && data?.views.length) {
      const view = data.views.find((x) => x.id === initialViewId);
      if (view) {
        appliedInitialView.current = true;
        applyView(view);
      }
    }
  }, [applyView, data?.views, initialViewId]);
  const saveView = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    const f = new FormData(e.currentTarget);
    const response = await fetch(`${api}/api/enterprise-requirements/views`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        projectId,
        name: f.get("name"),
        isShared: f.has("shared"),
        queryJson: JSON.stringify({
          search,
          level,
          verification,
          tag,
          state: stateFilter,
          owner,
          sourceScr,
          openComments,
          coverageState,
          sort,
          specificationId,
        }),
        columnsJson:
          '["identifier","statement","level","verification","state","comments"]',
      }),
    });
    if (!response.ok) {
      setError((await response.json()).error);
      return;
    }
    setShowSave(false);
    await load();
  };
  const commentForm = useRef<HTMLFormElement>(null);
  // Keyed to the requirement, so a comment drafted against one is never offered against another.
  const commentDraft = useFormDraft(commentForm, `aerolink:requirement-comment:${selected?.id ?? "none"}`,
    { enabled: !!selected });

  const addComment = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!selected) return;
    const form = e.currentTarget;
    const artifactId = selected.id;
    const f = new FormData(form);
    const body = String(f.get("body"));
    const mentions = [...body.matchAll(/@([a-z0-9._-]+)/gi)].map((x) => x[1]);
    const response = await fetch(
      `${api}/api/enterprise-requirements/${artifactId}/comments`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          revisionId: selected.revisionId,
          body,
          mentions,
        }),
      },
    );
    if (!response.ok) {
      setError((await response.json()).error);
      return;
    }
    form.reset();
    // The comment is on the record now; the browser copy has nothing left to protect.
    commentDraft.clear();
    setInspectorTab("discussion");
    await loadComments(artifactId);
    await load();
  };
  const resolveComment = async (id: string) => {
    const disposition =
      window.prompt("Disposition or resolution rationale (optional):") ?? "";
    const response = await fetch(
      `${api}/api/enterprise-requirements/comments/${id}/resolve`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ disposition }),
      },
    );
    if (response.ok && selected) {
      await open(selected);
      await load();
    }
  };
  const compare = async () => {
    if (!selected || !detail || detail.history.length < 2) return;
    const [to, from] = detail.history;
    const response = await fetch(
      `${api}/api/enterprise-requirements/${selected.id}/redline?fromRevisionId=${from.id}&toRevisionId=${to.id}`,
    );
    if (response.ok) setRedline(await response.json());
  };
  const filterChips: { key: string; label: string; clear: () => void }[] = [];
  if (search.trim())
    filterChips.push({
      key: "search",
      label: `Search: ${search.trim()}`,
      clear: () => setSearch(""),
    });
  if (level && level !== scope)
    filterChips.push({
      key: "level",
      label: `Level: ${level === "HighLevel" ? "HLR" : level === "LowLevel" ? "LLR" : level}`,
      clear: () => setLevel(scope),
    });
  if (verification)
    filterChips.push({
      key: "verification",
      label: `Verification: ${verification}`,
      clear: () => setVerification(""),
    });
  if (tag.trim())
    filterChips.push({
      key: "tag",
      label: `Tag: ${tag.trim()}`,
      clear: () => setTag(""),
    });
  if (stateFilter)
    filterChips.push({
      key: "state",
      label: `State: ${stateFilter}`,
      clear: () => setStateFilter(""),
    });
  if (owner.trim())
    filterChips.push({
      key: "owner",
      label: `Owner: ${owner.trim()}`,
      clear: () => setOwner(""),
    });
  if (sourceScr.trim())
    filterChips.push({
      key: "source",
      label: `Source: ${sourceScr.trim()}`,
      clear: () => setSourceScr(""),
    });
  if (openComments)
    filterChips.push({
      key: "comments",
      label: "Open discussions",
      clear: () => setOpenComments(false),
    });
  if (coverageState)
    filterChips.push({
      key: "coverage",
      label: `Coverage: ${coverageLabel(coverageState)}`,
      clear: () => setCoverageState(""),
    });
  if (specificationId)
    filterChips.push({
      key: "specification",
      label: `Specification: ${data?.specifications.find((x) => x.id === specificationId)?.documentNumber ?? "selected"}`,
      clear: () => setSpecificationId(""),
    });
  if (sort !== "identifier")
    filterChips.push({
      key: "sort",
      label: `Sort: ${sort === "updated" ? "Recently revised" : sort === "verification" ? "Verification method" : "Lifecycle state"}`,
      clear: () => setSort("identifier"),
    });
  const removeFilter = (item: (typeof filterChips)[number]) => {
    item.clear();
    setPage(1);
  };
  const documentTargets = scope === "System"
    ? (ladderAllows(ladder, "System", LadderCapability.RequirementsDocument) ? targetsFor(scope, level) : [])
    : targetsFor(scope, level).filter((target) =>
        target.type === "SwrdHighLevel"
          ? ladderAllows(ladder, "HighLevel", LadderCapability.RequirementsDocument)
          : ladderAllows(ladder, "LowLevel", LadderCapability.RequirementsDocument));
  const addToExistingDraft = async (draft: ProposalOption) => {
    if (!proposalTarget || !release || !proposalOptions) return;
    if (draft.existingProposalId) {
      setProposalTarget(undefined);
      setProposalOptions(undefined);
      setProposalError("");
      onOpenScr(draft.id, release.id, draft.existingProposalId);
      return;
    }
    setProposalSaving(true);
    setProposalError("");
    try {
      const result = await apiRequest<{ id: string; proposalId: string; duplicate?: boolean }>(
        `${api}/api/enterprise-requirements/${proposalTarget.id}/propose`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            targetReleaseId: release.id,
            kind: "Modify",
            existingScrId: draft.id,
            requirementRevisionId: proposalOptions.requirement.revisionId,
            expectedVersion: draft.version,
          }),
        },
      );
      setProposalTarget(undefined);
      setProposalOptions(undefined);
      onOpenScr(result.id, release.id, result.proposalId);
    } catch (reason) {
      recordClientOperationFailure("enterprise.requirement.propose-existing", reason);
      setProposalError(operationError(reason, "The Draft could not accept this requirement. Refresh and try again."));
      setProposalSaving(false);
    }
  };
  const closeProposalChooser = () => {
    setProposalTarget(undefined);
    setProposalOptions(undefined);
    setProposalError("");
    requestAnimationFrame(() => proposalTrigger.current?.focus());
  };
  return (
    <main className="reqWorkspace">
      <ControlledArtifactExplorerHeader
        back={{ label: "Command Center", onClick: onBack }}
        eyebrow="CONTROLLED REQUIREMENTS / READ-ONLY EXPLORER"
        title={`${scope} Requirements Explorer`}
      />
      {error && <div className="workspaceError">{error}</div>}
      {/* The document these requirements belong to, offered where they are read. Which one you get follows the
          build: approved for a released one, a stamped draft for an in-work one. Level-aware, so the Software
          explorer filtered to HLR offers the high-level document and nothing else. */}
      {release && documentTargets.length > 0 && (
        <DocumentActions
          api={api}
          projectId={projectId}
          release={release}
          targets={documentTargets}
          heading={release.isReleased ? `Approved documents for ${release.version}` : `Draft documents for ${release.version}`}
        />
      )}
      <section className={`reqCommand requirementsCommand${selected || deepLinkMissing ? " inspecting" : ""}`}>
        <div className="reqSearch">
          <span>⌕</span>
          <input
            aria-label="Search requirements"
            value={search}
            onChange={(e) => {
              setSearch(e.target.value);
              setPage(1);
            }}
            placeholder="Search any identifier fragment, statement, rationale…"
          />
          <kbd>
            {loading
              ? "Updating…"
              : `${data?.totalCount.toLocaleString() ?? 0} found`}
          </kbd>
        </div>
        <div className="reqPrimaryFilters" role="group" aria-label="Requirement filters">
          <select
            aria-label="Level filter"
            value={level}
            disabled={scope === "System"}
            onChange={(e) => {
              setLevel(e.target.value);
              setPage(1);
            }}
          >
            {scope === "System" ? (
              <option value="System">System requirements</option>
            ) : (
              <>
                <option value="Software">All software requirements</option>
                {ladderAllows(ladder, "HighLevel") && <option value="HighLevel">Software HLR</option>}
                {ladderAllows(ladder, "LowLevel") && <option value="LowLevel">Software LLR</option>}
              </>
            )}
          </select>
          <select
            aria-label="Verification filter"
            value={verification}
            onChange={(e) => {
              setVerification(e.target.value);
              setPage(1);
            }}
          >
            <option value="">All verification</option>
            <option>Test</option>
            <option>Analysis</option>
            <option>Inspection</option>
            <option>Demonstration</option>
          </select>
          {/*
            The control beside this one filters on the verification *method* an author declared, which says
            what kind of evidence is intended and nothing about whether any exists. This one answers the
            question an engineer actually arrives with.
          */}
          <select
            aria-label="Coverage state filter"
            value={coverageState}
            onChange={(e) => {
              setCoverageState(e.target.value);
              setPage(1);
            }}
          >
            <option value="">All coverage</option>
            <option value="covered">Covered</option>
            <option value="suspect">Suspect</option>
            <option value="uncovered">Not covered</option>
          </select>
          <input
            className="tagFilter"
            aria-label="Tag filter"
            value={tag}
            onChange={(e) => {
              setTag(e.target.value);
              setPage(1);
            }}
            placeholder="Filter tag"
          />
        </div>
        <div className="reqToolbarActions" role="group" aria-label="Explorer display controls">
          <button
            className={showAdvanced ? "advanced active" : "advanced"}
            onClick={() => setShowAdvanced((x) => !x)}
          >
            Advanced
          </button>
          <button
            className="clear"
            disabled={!filterChips.length}
            onClick={clearFilters}
          >
            Clear
          </button>
          <label className="pageSizeControl">
            Rows
            <select
              aria-label="Rows per page"
              value={pageSize}
              onChange={(e) => {
                setPageSize(Number(e.target.value));
                setPage(1);
              }}
            >
              <option value="25">25</option>
              <option value="50">50</option>
              <option value="100">100</option>
            </select>
          </label>
          <div className="modeSwitch" aria-label="Requirement view mode">
            <button
              aria-label="Table view"
              title="Table view"
              className={mode === "table" ? "active" : ""}
              onClick={() => setMode("table")}
            >
              ▦
            </button>
            <button
              aria-label="Document view"
              title="Document view"
              className={mode === "document" ? "active" : ""}
              onClick={() => setMode("document")}
            >
              ☷
            </button>
          </div>
        </div>
      </section>
      {showAdvanced && (
        <section className="advancedQuery">
          <label>
            Lifecycle state
            <select
              value={stateFilter}
              onChange={(e) => {
                setStateFilter(e.target.value);
                setPage(1);
              }}
            >
              <option value="">Any state</option>
              <option>Active</option>
              <option>Superseded</option>
              <option>Retired</option>
            </select>
          </label>
          <label>
            Owner
            <input
              value={owner}
              onChange={(e) => {
                setOwner(e.target.value);
                setPage(1);
              }}
              placeholder="username"
            />
          </label>
          <label>
            Source change request
            <input
              value={sourceScr}
              onChange={(e) => {
                setSourceScr(e.target.value);
                setPage(1);
              }}
              placeholder="Change request number or title"
            />
          </label>
          <label>
            Sort
            <select value={sort} onChange={(e) => setSort(e.target.value)}>
              <option value="identifier">Identifier</option>
              <option value="updated">Recently revised</option>
              <option value="verification">Verification method</option>
              <option value="state">Lifecycle state</option>
            </select>
          </label>
          <label className="advancedCheck">
            <input
              type="checkbox"
              checked={openComments}
              onChange={(e) => setOpenComments(e.target.checked)}
            />{" "}
            Open discussions only
          </label>
          <span>{data?.queryElapsedMs ?? 0} ms server query</span>
        </section>
      )}
      {!!filterChips.length && (
        <section
          className="filterChips"
          aria-label="Active requirement filters"
        >
          <span>Active filters</span>
          {filterChips.map((item) => (
            <button
              key={item.key}
              onClick={() => removeFilter(item)}
              aria-label={`Remove ${item.label} filter`}
            >
              {item.label}
              <i aria-hidden="true">×</i>
            </button>
          ))}
          <button className="clearAllFilters" onClick={clearFilters}>
            Clear all
          </button>
        </section>
      )}
      <ControlledArtifactExplorerLayout
        inspecting={Boolean(selected) || deepLinkMissing}
        resizableKey="requirements-explorer"
      >
        <aside className="specRail">
          <div className="railTitle">
            <b>Specifications</b>
            <span>{scopedSpecifications.length}</span>
          </div>
          <button
            className={!specificationId && !sectionId ? "active" : ""}
            onClick={() => {
              setSpecificationId("");
              setSectionId("");
              setPage(1);
            }}
          >
            <i>◫</i>
            <div>
              <b>All requirements</b>
              <small>{data?.totalCount.toLocaleString() ?? 0} visible</small>
            </div>
          </button>
          {scopedSpecifications.map((spec) => (
            <div className="specGroup" key={spec.id}>
              <button
                className={specificationId === spec.id ? "active" : ""}
                onClick={() => {
                  setSpecificationId(spec.id);
                  // A section belongs to one specification, so choosing a different document cannot leave the
                  // previous document's section applied — that combination matches nothing and reads as a bug.
                  setSectionId("");
                  setLevel("");
                  setPage(1);
                }}
              >
                <i>
                  {spec.level === "System"
                    ? "S"
                    : spec.level === "HighLevel"
                      ? "H"
                      : "L"}
                </i>
                <div>
                  <b>{spec.documentNumber}</b>
                  <small>
                    {spec.nodeCount.toLocaleString()} · {spec.title}
                  </small>
                </div>
              </button>
              {specificationId === spec.id && (
                <div className="sectionTree">
                  {/* Buttons, not labels. A heading that reports "40" and cannot be pressed tells a reader
                      there are forty requirements in Navigation and Guidance and gives them no way to see
                      which forty — the count is an invitation the control refused to accept. */}
                  {spec.sections.map((x) => (
                    <button
                      type="button"
                      key={x.id}
                      className={sectionId === x.id ? "active" : ""}
                      aria-pressed={sectionId === x.id}
                      onClick={() => {
                        setSectionId(sectionId === x.id ? "" : x.id);
                        setSpecificationId(spec.id);
                        setPage(1);
                      }}
                    >
                      <i />
                      {x.heading}
                      <small>{x.count}</small>
                    </button>
                  ))}
                </div>
              )}
            </div>
          ))}
          <details className="savedViews">
            <summary>
              <b>Saved views</b>
              <span>{data?.views.length ?? 0}</span>
            </summary>
            <div>
              {/*
                Owners can now tidy their own views. The product could create them and copy links to them and
                offered no way to rename, unshare or remove one, so repeated use left duplicates that had to be
                lived with. Non-owners see a shared view and no controls, which is the same authority the
                server enforces rather than a second opinion about it.
              */}
              {data?.views.map((view) => (
                <div className="savedViewRow" key={view.id}>
                  <button onClick={() => applyView(view)}>
                    <i>{view.isShared ? "◉" : "○"}</i>
                    <div>
                      <b>{view.name}</b>
                      <small>{view.isShared ? "Shared" : "Personal"}</small>
                    </div>
                  </button>
                  {view.owned && (
                    <div className="savedViewActions">
                      <button type="button" title="Rename this view" onClick={() => renameView(view)}>Rename</button>
                      <button type="button" title={view.isShared ? "Make personal" : "Share with authorized"} onClick={() => shareView(view, !view.isShared)}>
                        {view.isShared ? "Unshare" : "Share"}
                      </button>
                      <button type="button" title="Delete this view" onClick={() => deleteView(view)}>Delete</button>
                    </div>
                  )}
                </div>
              ))}
            </div>
          </details>
        </aside>
        <section className="reqResults">
          <div className="resultSummary">
            <div>
              <b>{data?.totalCount.toLocaleString() ?? 0} requirements</b>
              <span>
                {loading
                  ? "Refreshing controlled index…"
                  : `Page ${data?.page ?? 1} of ${data?.totalPages ?? 1} · exact current revisions`}
              </span>
            </div>
            <div className="confidence">
              <i /> Live counts · respects your access
            </div>
          </div>
          {!data && loading ? (
            <div className="reqLoadingState" role="status">
              <i />
              <b>Loading controlled requirements</b>
              <span>Applying your authorized project and release context…</span>
            </div>
          ) : data && !data.items.length ? (
            <div className="reqNoResults">
              <span aria-hidden="true">⌕</span>
              <h2>
                {filterChips.length
                  ? "No requirements match these filters"
                  : `No ${scope.toLowerCase()} requirements yet`}
              </h2>
              <p>
                {filterChips.length
                  ? "Remove one or more filters to broaden the exact controlled set."
                  : "Create a controlled change request to introduce the first requirement revision."}
              </p>
              <div>
                {filterChips.length ? (
                  <button onClick={clearFilters}>Clear all filters</button>
                ) : !release?.isReleased ? (
                  <button onClick={() => onProposeChange("")}>Open Changes</button>
                ) : null}
              </div>
            </div>
          ) : mode === "table" ? (
            <div className="reqTable">
              <div className="reqTableHead">
                <span>Identifier & statement</span>
                <span>Level</span>
                <span>Verification</span>
                <span>Coverage</span>
                <span>State</span>
                <span>Discussion</span>
              </div>
              {data?.items.map((item) => (
                <article
                  className={selected?.id === item.id ? "selected" : ""}
                  key={item.id}
                >
                  <a
                    className="requirementTarget"
                    href={rowRequirementHref(item)}
                    onClick={(event) => {
                      if (event.detail !== 0 && event.button === 0 && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey) {
                        event.preventDefault();
                        openRequirementRow(item);
                        void open(item);
                      }
                    }}
                  >
                    <b>{item.displayNumber}</b>
                    <p>{item.statement || "Retired requirement"}</p>
                    <div className="miniTags">
                      {parseTags(item.tagsJson)
                        .slice(0, 3)
                        .map((x) => (
                          <small key={x}>{x}</small>
                        ))}
                    </div>
                  </a>
                  <span>
                    {item.level === "HighLevel"
                      ? "HLR"
                      : item.level === "LowLevel"
                        ? "LLR"
                        : "System"}
                  </span>
                  <span>{item.verificationMethod}</span>
                  {/*
                    A state that only reads is a state nobody can act on. Covered is a plain label because
                    there is nothing to do about it; the other two open the trace panel, which is where the
                    procedures covering this revision — or the absence of them — actually are.
                  */}
                  {!item.coverageState ? (
                    <span />
                  ) : item.coverageState === "Covered" ? (
                    <span className="coverageState covered">{coverageLabel(item.coverageState)}</span>
                  ) : (
                    <button
                      type="button"
                      className={`coverageState ${item.coverageState.toLowerCase()}`}
                      title={
                        item.coverageState === "Suspect"
                          ? "Coverage exists but does not count yet. Open the verification trace."
                          : `No ${verificationArtifactNoun(item.level).toLowerCase()} covers this revision. Open the verification trace.`
                      }
                      onClick={() => {
                        openRequirementRow(item);
                        open(item);
                        setInspectorTab("trace");
                      }}
                    >
                      {coverageLabel(item.coverageState)}
                    </button>
                  )}
                  <i className={item.state.toLowerCase()}>{stateLabel(item.state)}</i>
                  <span className={item.openCommentCount ? "hasComments" : ""}>
                    ◌ {item.commentCount}
                    {item.openCommentCount > 0 && (
                      <em>{item.openCommentCount} open</em>
                    )}
                  </span>
                </article>
              ))}
            </div>
          ) : (
            <div className="documentMode">
              {data?.items.map((item) => (
                <article key={item.id}>
                  <a
                    className="requirementTarget"
                    href={rowRequirementHref(item)}
                    onClick={(event) => {
                      if (event.detail !== 0 && event.button === 0 && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey) {
                        event.preventDefault();
                        openRequirementRow(item);
                        void open(item);
                      }
                    }}
                  >
                    <div>
                      <b>{item.displayNumber}</b>
                      <span>
                        {item.level} · Rev {item.revision}
                      </span>
                    </div>
                    <p>{item.statement}</p>
                    <footer>
                      <span>Verification: {item.verificationMethod}</span>
                      <span>{item.commentCount} comments</span>
                      <span>{stateLabel(item.state)}</span>
                    </footer>
                  </a>
                </article>
              ))}
            </div>
          )}
          <div className="pager">
            <button
              disabled={(data?.page ?? 1) <= 1 || loading}
              onClick={() => setPage((x) => x - 1)}
            >
              ← Previous
            </button>
            <span>
              {(data?.totalCount ?? 0) > 0
                ? `${((data?.page ?? 1) - 1) * (data?.pageSize ?? pageSize) + 1}–${Math.min((data?.page ?? 1) * (data?.pageSize ?? pageSize), data?.totalCount ?? 0)} of ${data?.totalCount.toLocaleString() ?? 0}`
                : "0 requirements"}
            </span>
            <button
              disabled={(data?.page ?? 1) >= (data?.totalPages ?? 1) || loading}
              onClick={() => setPage((x) => x + 1)}
            >
              Next →
            </button>
          </div>
        </section>
        {deepLinkMissing ? (
          <aside className="requirementInspector" aria-label="Unavailable requirement revision">
            <div className="deepLinkUnavailable">
              <b>The exact requirement revision could not be opened</b>
              <span>
                The requirementRevisionId in this link does not belong to this requirement. No revision was
                substituted in its place.
              </span>
            </div>
          </aside>
        ) : selected && (
          <ControlledArtifactInspector
            artifactType={`${selected.level.toUpperCase()} REQUIREMENT`}
            displayNumber={selected.displayNumber}
            closeLabel="Close requirement inspector"
            onClose={() => {
              autoSelected.current = true;
              onCloseRequirement();
              setSelected(undefined);
              setDetail(undefined);
            }}
            tabs={[
              { id: "details", label: "Overview" },
              { id: "trace", label: <>Trace &amp; impact</> },
              { id: "history", label: "History" },
              { id: "discussion", label: <>Discussion <span>{comments.length}</span></> },
            ]}
            activeTab={inspectorTab}
            onTab={(tab) => setInspectorTab(tab as typeof inspectorTab)}
          >
            {inspectorTab === "details" && (
              <div className="inspectorBody">
                {release?.isReleased
                  ? <p className="changeBoundaryNote"><b>Read-only historical record — Build {release.version}</b><br/>Exit this workspace and select an in-work build to propose a change.</p>
                  : <><button ref={proposalTrigger} className="impactLaunch" onClick={() => setProposalTarget(selected)}>Propose controlled change →</button><p className="changeBoundaryNote">Choose a new Draft change request or add a Modify proposal to an existing eligible Draft. This authoritative revision remains unchanged.</p></>}
                <h3>Requirement statement</h3>
                <div className="richRequirement">{selected.statement}</div>
                <dl>
                  <div>
                    <dt>Verification</dt>
                    <dd>{selected.verificationMethod}</dd>
                  </div>
                  <div>
                    <dt>State</dt>
                    <dd>{stateLabel(selected.state)}</dd>
                  </div>
                  <div>
                    <dt>Source authority</dt>
                    <dd>
                      {/* Named after the record it opens, not after the page it is on. The controlled
                          identifier already says which kind of change request it is — SRCR, HLRCR or
                          LLRCR — so the label reads it off rather than guessing from the workspace. */}
                      <button onClick={() => onOpenScr(selected.sourceChangeRequestId, selected.sourceChangeRequestReleaseId)}>
                        Open {artifactAcronym(selected.sourceScr, "changeRequest")} →
                      </button>
                    </dd>
                  </div>
                </dl>
                <h3>Classification</h3>
                <div className="tagCloud">
                  {parseTags(selected.tagsJson).map((x) => (
                    <span key={x}>{x}</span>
                  ))}
                  {!parseTags(selected.tagsJson).length && (
                    <small>No additional tags</small>
                  )}
                </div>
                <h3>Specification placement</h3>
                {detail?.placements.map((x) => (
                  <div className="placement" key={x.id}>
                    <b>{x.documentNumber}</b>
                    <span>{x.section}</span>
                  </div>
                ))}
              </div>
            )}
            {inspectorTab === "trace" && (
              <div className="inspectorBody traceInspector">
                <div className="traceSummary">
                  <article><b>{impact?.parents.length ?? 0}</b><span>upstream</span></article>
                  <article><b>{impact?.children.length ?? 0}</b><span>downstream</span></article>
                  {/* coverageState, not the raw isSuspect flag: a link to a procedure that is being
                      rewritten is not confirmed coverage, and counting it here contradicted the row the
                      reader clicked to get in. */}
                  <article><b>{impact?.tests.filter((item) => item.coverageState === "Confirmed").length ?? 0}</b><span>confirmed tests</span></article>
                </div>
                {/* #880 §4.4 opens the thread on the exact revision the reader is looking at, so the address
                    that leaves here names that revision. `/traceability/{artifactId}` still resolves, for the
                    links written before the thread was rooted on an exact revision. */}
                <button className="openDigitalThread" onClick={() => onOpenTraceability(selected?.revisionId ?? selected?.id)}>
                  Open complete Digital Thread →
                </button>
                <h3>Active controlled changes</h3>
                {impact?.activeChanges.length ? impact.activeChanges.map((item) => (
                  <button className="activeChangeCard" key={item.id} onClick={() => onOpenScr(item.id)}>
                    <span><b>{item.displayNumber}</b><i>{stateLabel(item.state)}</i></span>
                    <p>{item.title}</p>
                    <small>{item.kind} · proposed revision {item.proposedRevision}</small>
                  </button>
                )) : <div className="traceEmpty"><b>No active change package</b><span>This requirement has no Draft, In Review, or Approved proposal awaiting baseline effectivity.</span></div>}
                <h3>Upstream requirements</h3>
                {impact?.parents.map((item) => <article className="traceRelation" key={item.id}><div className="traceRelationTarget">{impactRequirementLink(item)}<button type="button" onClick={() => focusImpactRequirement(item)}>Focus exact requirement</button></div><p>{item.statement}</p><small>{item.type} · {item.level}</small></article>)}
                {!impact?.parents.length && <div className="traceEmpty"><span>No upstream requirement is recorded.</span></div>}
                <h3>Downstream requirements</h3>
                {impact?.children.map((item) => <article className="traceRelation" key={item.id}><div className="traceRelationTarget">{impactRequirementLink(item)}<button type="button" onClick={() => focusImpactRequirement(item)}>Focus exact requirement</button></div><p>{item.statement}</p><small>{item.type} · {item.level}</small></article>)}
                {!impact?.children.length && <div className="traceEmpty"><span>No downstream requirement is recorded.</span></div>}
                <h3>Verification coverage</h3>
                {impact?.tests.map((item) => { const unsettled = item.coverageState !== "Confirmed"; const target = { artifactId: item.id, revisionId: item.artifactRevisionId ?? item.revisionId, artifactRevisionId: item.artifactRevisionId, artifactKind: item.artifactKind, displayNumber: item.displayNumber, level: item.level }; const noun = verificationArtifactNoun(item.level).toLowerCase(); return <article className={`traceRelation${unsettled ? " attention" : ""}`} key={item.artifactRevisionId ?? item.revisionId ?? item.id}><ExactArtifactLink className="linkedArtifactText" href={verificationArtifactHref?.(target)} onOpen={verificationArtifactHref?.(target) ? () => onOpenVerification(target) : undefined} title={verificationArtifactHref?.(target) ? "Open this exact verification artifact" : undefined}><b>{item.displayNumber}</b><p>{item.title}</p><small>{item.level} · {stateLabel(item.state)} · Open {noun} →</small></ExactArtifactLink><small>{unsettled ? "Suspect applicability — does not count as coverage" : "Confirmed applicability"}</small>{unsettled && <button type="button" onClick={() => onOpenVerification(target)}>Resolve in Verification →</button>}</article>; })}
                {!impact?.tests.length && <div className="traceEmpty attention"><span>No verification artifact currently covers this revision.</span></div>}
              </div>
            )}
            {inspectorTab === "history" && (
              <div className="inspectorBody">
                <div className="historyLead">
                  <div>
                    <b>{detail?.history.length ?? 0} immutable revisions</b>
                    <span>Complete controlled history</span>
                  </div>
                  <button
                    disabled={(detail?.history.length ?? 0) < 2}
                    onClick={compare}
                  >
                    Compare latest
                  </button>
                </div>
                {detail?.history.map((x) => (
                  <article className="revisionCard" key={x.id}>
                    <div>
                      <b>{x.displayNumber}</b>
                      <i>{stateLabel(x.state)}</i>
                    </div>
                    <p>{x.statement}</p>
                    <small>
                      {x.isHistorical ? `Historical version — Build ${x.originBuild}` : `Build ${x.originBuild}`} ·{" "}
                      {/* A control only where there is somewhere to go. A revision with no recorded source
                          change request reads as text rather than as a link that does nothing. */}
                      {x.sourceChangeRequestId
                        ? <button type="button" className="inlineArtifactLink" onClick={() => onOpenScr(x.sourceChangeRequestId, x.sourceChangeRequestReleaseId)}>{x.sourceScr}</button>
                        : <span>{x.sourceScr}</span>} ·{" "}
                      {new Date(x.createdAt).toLocaleDateString()}
                    </small>
                  </article>
                ))}
              </div>
            )}
            {inspectorTab === "discussion" && (
              <div className="discussionPane">
                {!release?.isReleased ? <form onSubmit={addComment} ref={commentForm}>
                  {commentDraft.offered && (
                    <DraftRestore
                      savedAt={commentDraft.offered.savedAt}
                      description="A comment you were writing was left unsent."
                      onRestore={commentDraft.apply}
                      onDiscard={commentDraft.discard}
                    />
                  )}
                  <textarea
                    name="body"
                    placeholder="Add an attributable comment. Use @username to mention someone."
                    required
                  />
                  <div className="commentFoot">
                    <AutosaveState status={commentDraft.status} savedAt={commentDraft.savedAt} where="this browser" />
                    <button>Add comment</button>
                  </div>
                </form> : <div className="traceEmpty"><span>Discussion is read-only in released Build {release.version}.</span></div>}
                {comments.map((c) => (
                  <article key={c.id} className={c.state.toLowerCase()}>
                    <div>
                      <b><PersonName userName={c.createdBy} /></b>
                      <span>{new Date(c.createdAt).toLocaleString()}</span>
                    </div>
                    <p>{c.body}</p>
                    {c.disposition && (
                      <small>Disposition: {c.disposition}</small>
                    )}
                    <footer>
                      <i>{stateLabel(c.state)}</i>
                      {c.state === "Open" && (
                        <button onClick={() => resolveComment(c.id)}>
                          Resolve / disposition
                        </button>
                      )}
                    </footer>
                  </article>
                ))}
              </div>
            )}
          </ControlledArtifactInspector>
        )}
      </ControlledArtifactExplorerLayout>
      {proposalTarget && (
        <div
          className="reqModal proposalModal"
          role="dialog"
          aria-modal="true"
          aria-labelledby="proposalModalTitle"
          onKeyDown={(event) => {
            if (event.key === "Escape" && !proposalSaving) {
              closeProposalChooser();
            }
          }}
        >
          <div>
            <button
              type="button"
              className="modalClose"
              aria-label="Close change proposal chooser"
              disabled={proposalSaving}
              onClick={closeProposalChooser}
            >
              ×
            </button>
            <p className="eyebrow">CONTROLLED CHANGE / REQUIREMENT {proposalTarget.displayNumber}</p>
            <h2 id="proposalModalTitle">Choose a change request</h2>
            <p>
              Start a new Draft, or add a Modify proposal to an eligible Draft. The authoritative requirement
              revision remains unchanged.
            </p>
            <button
              type="button"
              className="impactLaunch proposalNew"
              disabled={proposalSaving}
              onClick={() => {
                closeProposalChooser();
                onProposeChange(proposalTarget.id, proposalTarget.level);
              }}
            >
              Start a new Draft change request →
            </button>
            <label className="proposalSearch">
              <span>Find an existing Draft</span>
              <input
                type="search"
                ref={proposalSearchInput}
                value={proposalSearch}
                aria-label="Search existing Draft change requests"
                placeholder="Search by number or title"
                onChange={(event) => setProposalSearch(event.target.value)}
                disabled={proposalSaving}
              />
            </label>
            {proposalLoading && <p className="proposalStatus" role="status">Checking eligible Drafts…</p>}
            {proposalError && <p className="proposalError" role="alert">{proposalError}</p>}
            {!proposalLoading && !proposalError && !release && (
              <p className="proposalStatus">Select an in-work build before adding a proposal to an existing Draft.</p>
            )}
            {!proposalLoading && !proposalError && release && proposalOptions && (
              <>
                <p className="proposalSource">
                  Exact source: <b>{proposalOptions.requirement.displayNumber}</b> in Build {proposalOptions.targetRelease.version}
                </p>
                <div className="proposalOptions" aria-label="Existing Draft change requests">
                  {proposalOptions.drafts.length === 0 ? (
                    <p className="proposalStatus">No matching Drafts were found.</p>
                  ) : proposalOptions.drafts.map((draft) => (
                    <article key={draft.id} className={draft.eligible ? "proposalOption eligible" : "proposalOption"}>
                      <div>
                        <b>{draft.displayNumber}</b>
                        <span>{draft.title} · {draft.requirementCount} requirement{draft.requirementCount === 1 ? "" : "s"}</span>
                      </div>
                      <small>{draft.eligible ? "Eligible Draft" : draft.reason}</small>
                      <button
                        type="button"
                        disabled={(!draft.eligible && !draft.existingProposalId) || proposalSaving}
                        onClick={() => void addToExistingDraft(draft)}
                      >
                        {proposalSaving ? "Opening…" : draft.existingProposalId ? "Open existing proposal →" : "Add Modify proposal →"}
                      </button>
                    </article>
                  ))}
                </div>
              </>
            )}
          </div>
        </div>
      )}
      {showSave && (
        <div className="reqModal">
          <form onSubmit={saveView}>
            <p className="eyebrow">PERSONAL & SHARED WORKLISTS</p>
            <h2>Save current view</h2>
            <p>
              The exact filters and columns become a reusable, permission-aware
              worklist.
            </p>
            <label>
              View name
              <input name="name" required autoFocus />
            </label>
            <label className="check">
              <input type="checkbox" name="shared" /> Share with authorized
              Program members
            </label>
            <div className="modalActions">
              <button
                type="button"
                className="secondary"
                onClick={() => setShowSave(false)}
              >
                Cancel
              </button>
              <button>Save view</button>
            </div>
          </form>
        </div>
      )}
      {showSchema && (
        <div className="reqModal schemaModal">
          <div>
            <p className="eyebrow">PROGRAM CONFIGURATION</p>
            <h2>Artifact schemas</h2>
            <p>
              Versioned definitions describe fields and validation without
              changing the application database.
            </p>
            {data?.schemas.map((s) => (
              <article key={s.id}>
                <div>
                  <b>{s.name}</b>
                  <span>
                    {s.appliesTo} · v{s.version}
                  </span>
                </div>
                <p>{s.description}</p>
                <div>
                  {s.fields.map((f) => (
                    <small key={f.id}>
                      {f.label}
                      <i>
                        {f.type}
                        {f.isRequired ? " · required" : ""}
                      </i>
                    </small>
                  ))}
                </div>
              </article>
            ))}
            <div className="modalActions">
              <button onClick={() => setShowSchema(false)}>Done</button>
            </div>
          </div>
        </div>
      )}
      {redline && (
        <div className="reqModal redlineModal">
          <div>
            <button
              className="modalClose"
              onClick={() => setRedline(undefined)}
            >
              ×
            </button>
            <p className="eyebrow">
              CONTROLLED REDLINE / REV {redline.from} → {redline.to}
            </p>
            <h2>Revision comparison</h2>
            <h3>Statement</h3>
            <div className="redlineText">
              {redline.statement.map((x, i) => (
                <span className={x.kind} key={i}>
                  {x.text}{" "}
                </span>
              ))}
            </div>
            <h3>Rationale</h3>
            <div className="redlineText">
              {redline.rationale.map((x, i) => (
                <span className={x.kind} key={i}>
                  {x.text}{" "}
                </span>
              ))}
            </div>
            {redline.verificationChanged && (
              <p className="verificationDiff">
                Verification changed: <del>{redline.fromVerification}</del> →{" "}
                <ins>{redline.toVerification}</ins>
              </p>
            )}
          </div>
        </div>
      )}
    </main>
  );
}
