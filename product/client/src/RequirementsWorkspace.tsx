import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { FormEvent } from "react";
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
  sourceScrId: string;
  createdAt: string;
  richText: string;
  attributesJson: string;
  tagsJson: string;
  commentCount: number;
  openCommentCount: number;
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
  sourceScrId: string;
  sourceScr: string;
  createdAt: string;
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
  initialViewId?: string;
  initialArtifactId?: string;
  onScopeChange: (scope: "System" | "Software") => void;
  onBack: () => void;
  onOpenScr: (id: string) => void;
  onProposeChange: (requirementId: string) => void;
  onOpenRequirement: (id: string) => void;
  onOpenTraceability: () => void;
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
  initialViewId,
  initialArtifactId,
  onScopeChange,
  onBack,
  onOpenScr,
  onProposeChange,
  onOpenRequirement,
  onOpenTraceability,
}: Props) {
  const appliedInitialView = useRef(false);
  const autoSelected = useRef(false);
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
    [sort, setSort] = useState("identifier"),
    [showAdvanced, setShowAdvanced] = useState(false),
    [specificationId, setSpecificationId] = useState(""),
    [page, setPage] = useState(1),
    [pageSize, setPageSize] = useState(25),
    [mode, setMode] = useState<"table" | "document">("table"),
    [selected, setSelected] = useState<Requirement>(),
    [detail, setDetail] = useState<Detail>(),
    [impact, setImpact] = useState<Impact>(),
    [comments, setComments] = useState<Comment[]>([]),
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
    }>();
  useEffect(() => {
    autoSelected.current = false;
    setLevel(scope);
    setSpecificationId("");
    setPage(1);
    setSelected(undefined);
  }, [scope]);
  const params = useMemo(() => {
    const p = new URLSearchParams({
      projectId,
      page: String(page),
      pageSize: String(pageSize),
      sort,
    });
    if (search) p.set("search", search);
    if (level) p.set("level", level);
    if (verification) p.set("verification", verification);
    if (tag) p.set("tag", tag);
    if (stateFilter) p.set("state", stateFilter);
    if (owner) p.set("owner", owner);
    if (sourceScr) p.set("sourceScr", sourceScr);
    if (openComments) p.set("openComments", "true");
    if (specificationId) p.set("specificationId", specificationId);
    return p;
  }, [
    projectId,
    page,
    pageSize,
    search,
    level,
    verification,
    tag,
    stateFilter,
    owner,
    sourceScr,
    openComments,
    sort,
    specificationId,
  ]);
  const load = useCallback(async () => {
    setLoading(true);
    try {
      const response = await fetch(
        `${api}/api/enterprise-requirements/workspace?${params}`,
      );
      if (response.ok) {
        setData(await response.json());
        setError("");
      } else
        setError(
          (await response.json()).error ||
            "Requirements workspace could not be loaded.",
        );
    } catch {
      setError(
        "Requirements could not be loaded. Check the AeroLink service and try again.",
      );
    } finally {
      setLoading(false);
    }
  }, [api, params]);
  useEffect(() => {
    const timer = setTimeout(load, 180);
    return () => clearTimeout(timer);
  }, [load]);
  const loadComments = async (artifactId: string) => {
    const response = await fetch(
      `${api}/api/enterprise-requirements/${artifactId}/comments`,
    );
    if (response.ok) setComments(await response.json());
  };
  const open = useCallback(async (item: Requirement) => {
    setSelected(item);
    setInspectorTab("details");
    const [a, b, c] = await Promise.all([
      fetch(`${api}/api/enterprise-requirements/${item.id}`),
      fetch(`${api}/api/enterprise-requirements/${item.id}/comments`),
      fetch(`${api}/api/enterprise-requirements/${item.id}/impact`),
    ]);
    if (a.ok) setDetail(await a.json());
    if (b.ok) setComments(await b.json());
    if (c.ok) setImpact(await c.json());
  }, [api]);
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
    (async () => {
      const [detailResponse, commentsResponse, impactResponse] = await Promise.all([
        fetch(`${api}/api/enterprise-requirements/${initialArtifactId}`),
        fetch(
          `${api}/api/enterprise-requirements/${initialArtifactId}/comments`,
        ),
        fetch(`${api}/api/enterprise-requirements/${initialArtifactId}/impact`),
      ]);
      if (!detailResponse.ok) return;
      const value: Detail = await detailResponse.json();
      const latest = value.history[0];
      if (!latest || cancelled) return;
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
        sourceScrId: latest.sourceScrId,
        createdAt: latest.createdAt,
        richText: latest.statement,
        attributesJson: latest.attributesJson,
        tagsJson: latest.tagsJson,
        commentCount: 0,
        openCommentCount: 0,
      };
      setDetail(value);
      setSelected(item);
      setInspectorTab("details");
      if (commentsResponse.ok) setComments(await commentsResponse.json());
      if (impactResponse.ok) setImpact(await impactResponse.json());
    })();
    return () => {
      cancelled = true;
    };
  }, [api, initialArtifactId, selected?.id]);
  const clearFilters = () => {
    setSearch("");
    setLevel(scope);
    setVerification("");
    setTag("");
    setStateFilter("");
    setOwner("");
    setSourceScr("");
    setOpenComments(false);
    setSort("identifier");
    setSpecificationId("");
    setPage(1);
  };
  const applyView = (view: SavedView) => {
    try {
      const q = JSON.parse(view.queryJson);
      setSearch(q.search || "");
      setLevel(q.level || "");
      setVerification(q.verification || "");
      setTag(q.tag || "");
      setStateFilter(q.state || "");
      setOwner(q.owner || "");
      setSourceScr(q.sourceScr || "");
      setOpenComments(!!q.openComments);
      setSort(q.sort || "identifier");
      setSpecificationId(q.specificationId || "");
      setPage(1);
    } catch {
      setError("Saved view configuration is invalid.");
    }
  };
  useEffect(() => {
    if (!appliedInitialView.current && initialViewId && data?.views.length) {
      const view = data.views.find((x) => x.id === initialViewId);
      if (view) {
        appliedInitialView.current = true;
        applyView(view);
      }
    }
  }, [data?.views, initialViewId]);
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
  return (
    <main className="reqWorkspace">
      <header className="reqHeader">
        <div>
          <button className="back" onClick={onBack}>
            ← Command Center
          </button>
          <p className="eyebrow">
            CONTROLLED REQUIREMENTS / READ-ONLY EXPLORER
          </p>
          <h1>{scope} Requirements Explorer</h1>
          <p>
            Read, trace, compare, and understand authoritative requirements.
            Controlled content changes are created only through SCRs and SWCRs.
          </p>
        </div>
        <div className="reqHeaderActions">
          <div
            className="pageScopeSwitch"
            role="group"
            aria-label="Requirement scope"
          >
            <button
              className={scope === "System" ? "active" : ""}
              aria-pressed={scope === "System"}
              onClick={() => onScopeChange("System")}
            >
              System
            </button>
            <button
              className={scope === "Software" ? "active" : ""}
              aria-pressed={scope === "Software"}
              onClick={() => onScopeChange("Software")}
            >
              Software
            </button>
          </div>
          <span className="explorerBoundary">
            <i aria-hidden="true">◈</i>
            <span><b>Authoritative view</b><small>Requirement content is read-only here</small></span>
          </span>
          <details className="pageActionsMenu">
            <summary>Workspace tools</summary>
            <div>
              <button onClick={() => setShowSchema(true)}>⚙ Schemas</button>
              <button onClick={() => setShowSave(true)}>☆ Save view</button>
            </div>
          </details>
        </div>
      </header>
      {error && <div className="workspaceError">{error}</div>}
      <section className="reqCommand">
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
              <option value="HighLevel">Software HLR</option>
              <option value="LowLevel">Software LLR</option>
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
            Source SCR/SWCR
            <input
              value={sourceScr}
              onChange={(e) => {
                setSourceScr(e.target.value);
                setPage(1);
              }}
              placeholder="SCR number or title"
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
      <div className={`reqLayout ${selected ? "inspecting" : ""}`}>
        <aside className="specRail">
          <div className="railTitle">
            <b>Specifications</b>
            <span>{data?.specifications.length ?? 0}</span>
          </div>
          <button
            className={!specificationId ? "active" : ""}
            onClick={() => {
              setSpecificationId("");
              setPage(1);
            }}
          >
            <i>◫</i>
            <div>
              <b>All requirements</b>
              <small>{data?.totalCount.toLocaleString() ?? 0} visible</small>
            </div>
          </button>
          {data?.specifications.map((spec) => (
            <div className="specGroup" key={spec.id}>
              <button
                className={specificationId === spec.id ? "active" : ""}
                onClick={() => {
                  setSpecificationId(spec.id);
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
                  {spec.sections.map((x) => (
                    <span key={x.id}>
                      <i />
                      {x.heading}
                      <small>{x.count}</small>
                    </span>
                  ))}
                </div>
              )}
            </div>
          ))}
          <details className="savedViews" open>
            <summary>
              <b>Saved views</b>
              <span>{data?.views.length ?? 0}</span>
            </summary>
            <div>
              {data?.views.map((view) => (
                <button onClick={() => applyView(view)} key={view.id}>
                  <i>{view.isShared ? "◉" : "○"}</i>
                  <div>
                    <b>{view.name}</b>
                    <small>{view.isShared ? "Shared" : "Personal"}</small>
                  </div>
                </button>
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
              <i /> Permission-aware · Live index
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
                ) : (
                  <button onClick={() => onProposeChange("")}>Open Changes</button>
                )}
              </div>
            </div>
          ) : mode === "table" ? (
            <div className="reqTable">
              <div className="reqTableHead">
                <span>Identifier & statement</span>
                <span>Level</span>
                <span>Verification</span>
                <span>State</span>
                <span>Discussion</span>
              </div>
              {data?.items.map((item) => (
                <article
                  className={selected?.id === item.id ? "selected" : ""}
                  key={item.id}
                >
                  <button
                    onClick={() => {
                      onOpenRequirement(item.id);
                      open(item);
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
                  </button>
                  <span>
                    {item.level === "HighLevel"
                      ? "HLR"
                      : item.level === "LowLevel"
                        ? "LLR"
                        : "System"}
                  </span>
                  <span>{item.verificationMethod}</span>
                  <i className={item.state.toLowerCase()}>{item.state}</i>
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
                  <button
                    onClick={() => {
                      onOpenRequirement(item.id);
                      open(item);
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
                      <span>{item.state}</span>
                    </footer>
                  </button>
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
        {selected && (
          <aside className="requirementInspector">
            <div className="inspectorTop">
              <div>
                <span>{selected.level.toUpperCase()} REQUIREMENT</span>
                <h2>{selected.displayNumber}</h2>
                <p>Controlled current revision</p>
              </div>
              <button
                aria-label="Close requirement inspector"
                onClick={() => {
                  setSelected(undefined);
                  setDetail(undefined);
                }}
              >
                ×
              </button>
            </div>
            <div className="inspectorTabs">
              <button
                className={inspectorTab === "details" ? "active" : ""}
                onClick={() => setInspectorTab("details")}
              >
                Overview
              </button>
              <button
                className={inspectorTab === "trace" ? "active" : ""}
                onClick={() => setInspectorTab("trace")}
              >
                Trace &amp; impact
              </button>
              <button
                className={inspectorTab === "history" ? "active" : ""}
                onClick={() => setInspectorTab("history")}
              >
                History
              </button>
              <button
                className={inspectorTab === "discussion" ? "active" : ""}
                onClick={() => setInspectorTab("discussion")}
              >
                Discussion <span>{comments.length}</span>
              </button>
            </div>
            {inspectorTab === "details" && (
              <div className="inspectorBody">
                <div className="controlledBanner">
                  <b>✓ Controlled revision</b>
                  <span>Content changes require an SCR/SWCR</span>
                </div>
                <button
                  className="impactLaunch"
                  onClick={() => onProposeChange(selected.id)}
                >
                  Propose controlled change →
                </button>
                <p className="changeBoundaryNote">
                  Opens a new Draft SCR/SWCR in Changes. This authoritative revision remains unchanged.
                </p>
                <h3>Requirement statement</h3>
                <div className="richRequirement">{selected.statement}</div>
                <h3>Digital thread</h3>
                <button className="threadPreview" onClick={onOpenTraceability}>
                  <span>
                    <i>CR</i>
                    <b>Source</b>
                    <small>Change authority</small>
                  </span>
                  <em>›</em>
                  <span>
                    <i>RQ</i>
                    <b>{detail?.traceCount ?? "—"} links</b>
                    <small>Requirement</small>
                  </span>
                  <em>›</em>
                  <span>
                    <i>EV</i>
                    <b>{detail?.testCoverageCount ?? "—"} tests</b>
                    <small>Evidence</small>
                  </span>
                </button>
                <div
                  className={`impactSignal ${selected.openCommentCount > 0 ? "attention" : "clear"}`}
                >
                  <b>
                    {selected.openCommentCount > 0
                      ? `${selected.openCommentCount} open decision${selected.openCommentCount === 1 ? "" : "s"} need attention`
                      : "No open discussion decisions"}
                  </b>
                  <p>
                    {selected.openCommentCount > 0
                      ? "Review attributable discussion before progressing this requirement."
                      : "The selected revision has no unresolved discussion recorded."}
                  </p>
                </div>
                <dl>
                  <div>
                    <dt>Verification</dt>
                    <dd>{selected.verificationMethod}</dd>
                  </div>
                  <div>
                    <dt>State</dt>
                    <dd>{selected.state}</dd>
                  </div>
                  <div>
                    <dt>Source authority</dt>
                    <dd>
                      <button onClick={() => onOpenScr(selected.sourceScrId)}>
                        Open SCR →
                      </button>
                    </dd>
                  </div>
                  <div>
                    <dt>Trace links</dt>
                    <dd>{detail?.traceCount ?? "—"}</dd>
                  </div>
                  <div>
                    <dt>Test coverage</dt>
                    <dd>{detail?.testCoverageCount ?? "—"} procedures</dd>
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
                  <article><b>{impact?.tests.length ?? 0}</b><span>tests</span></article>
                </div>
                <button className="openDigitalThread" onClick={onOpenTraceability}>
                  Open complete Digital Thread →
                </button>
                <h3>Active controlled changes</h3>
                {impact?.activeChanges.length ? impact.activeChanges.map((item) => (
                  <button className="activeChangeCard" key={item.id} onClick={() => onOpenScr(item.id)}>
                    <span><b>{item.displayNumber}</b><i>{item.state}</i></span>
                    <p>{item.title}</p>
                    <small>{item.kind} · proposed revision {item.proposedRevision}</small>
                  </button>
                )) : <div className="traceEmpty"><b>No active change package</b><span>This requirement has no Draft, In Review, or Approved proposal awaiting baseline effectivity.</span></div>}
                <h3>Upstream requirements</h3>
                {impact?.parents.map((item) => <article className="traceRelation" key={item.id}><b>{item.displayNumber}</b><p>{item.statement}</p><small>{item.type} · {item.level}</small></article>)}
                {!impact?.parents.length && <div className="traceEmpty"><span>No upstream requirement is recorded.</span></div>}
                <h3>Downstream requirements</h3>
                {impact?.children.map((item) => <article className="traceRelation" key={item.id}><b>{item.displayNumber}</b><p>{item.statement}</p><small>{item.type} · {item.level}</small></article>)}
                {!impact?.children.length && <div className="traceEmpty"><span>No downstream requirement is recorded.</span></div>}
                <h3>Verification coverage</h3>
                {impact?.tests.map((item) => <article className="traceRelation" key={item.id}><b>{item.displayNumber}</b><p>{item.title}</p><small>{item.level} · {item.state}</small></article>)}
                {!impact?.tests.length && <div className="traceEmpty attention"><span>No verification procedure currently covers this revision.</span></div>}
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
                      <i>{x.state}</i>
                    </div>
                    <p>{x.statement}</p>
                    <small>
                      {x.sourceScr} ·{" "}
                      {new Date(x.createdAt).toLocaleDateString()}
                    </small>
                  </article>
                ))}
              </div>
            )}
            {inspectorTab === "discussion" && (
              <div className="discussionPane">
                <form onSubmit={addComment}>
                  <textarea
                    name="body"
                    placeholder="Add an attributable comment. Use @username to mention someone."
                    required
                  />
                  <button>Add comment</button>
                </form>
                {comments.map((c) => (
                  <article key={c.id} className={c.state.toLowerCase()}>
                    <div>
                      <b>{c.createdBy}</b>
                      <span>{new Date(c.createdAt).toLocaleString()}</span>
                    </div>
                    <p>{c.body}</p>
                    {c.disposition && (
                      <small>Disposition: {c.disposition}</small>
                    )}
                    <footer>
                      <i>{c.state}</i>
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
          </aside>
        )}
      </div>
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
