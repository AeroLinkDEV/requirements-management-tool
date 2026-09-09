import "./UpstreamChangeRequestPicker.css";
import { useEffect, useState } from "react";

export type UpstreamDraftLink = { upstreamChangeRequestId: string; rationale: string };
export type UpstreamCandidate = {
  id: string; displayNumber: string; title: string; state: string; build: string;
  earlierBuild: boolean; assessmentDerived: boolean; selectable: boolean; selectionRefusal?: string | null;
};
export type DerivedUpstreamEdge = {
  upstreamChangeRequestId: string; upstreamDisplayNumber: string;
  upstreamBuildId: string; upstreamBuildVersion: string; assessmentId: string; assessmentLinkId: string;
};
type Result = {
  isTopOfLadder: boolean; upstreamAnswerComplete?: boolean; candidates: UpstreamCandidate[];
  derivedEdges?: DerivedUpstreamEdge[]; totalPages?: number; totalCount?: number;
};

export function useUpstreamCandidates(endpoint: string | undefined) {
  const [search, setSearchValue] = useState("");
  const [includeEarlierBuilds, setEarlierValue] = useState(false);
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Result>();
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);
  const [known, setKnown] = useState<Record<string, UpstreamCandidate>>({});
  const [retry, setRetry] = useState(0);
  useEffect(() => { setPage(1); setKnown({}); setData(undefined); }, [endpoint]);
  useEffect(() => {
    if (!endpoint) return;
    const controller = new AbortController();
    setLoading(true); setError("");
    const query = new URLSearchParams({ search, includeEarlierBuilds: String(includeEarlierBuilds), page: String(page), limit: "25" });
    fetch(`${endpoint}${endpoint.includes("?") ? "&" : "?"}${query}`, { signal: controller.signal })
      .then(async response => {
        if (!response.ok) throw new Error("Unable to load upstream change requests. Retry before selecting a parent.");
        return await response.json() as Result;
      }).then(result => {
        if (controller.signal.aborted) return;
        setData(result);
        setKnown(previous => ({ ...previous, ...Object.fromEntries(result.candidates.map(item => [item.id, item])) }));
      }).catch((reason: unknown) => {
        if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : "Unable to load upstream change requests.");
      }).finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [endpoint, search, includeEarlierBuilds, page, retry]);
  return { data, known, error, loading, search, includeEarlierBuilds, page, setPage, reload: () => setRetry(value => value + 1),
    setSearch: (value: string) => { setSearchValue(value); setPage(1); },
    setIncludeEarlierBuilds: (value: boolean) => { setEarlierValue(value); setPage(1); } };
}

export default function UpstreamChangeRequestPicker({ candidates, links, onChange, noUpstreamRationale, onNoUpstreamRationale, currentBuild, stored = [] }: {
  candidates: ReturnType<typeof useUpstreamCandidates>; links: UpstreamDraftLink[];
  onChange: (links: UpstreamDraftLink[]) => void; noUpstreamRationale: string | null;
  onNoUpstreamRationale: (value: string | null) => void; currentBuild: string;
  stored?: { upstreamChangeRequestId: string; upstreamDisplayNumber: string; upstreamBuildVersion?: string | null }[];
}) {
  const { data, known, loading, error, search, setSearch, includeEarlierBuilds, setIncludeEarlierBuilds, page, setPage } = candidates;
  return <div className="upstreamAnswerEditor" aria-label="Upstream change requests">
    <h3>Upstream change requests</h3>
    <p>Choose approved exact direct-parent revisions, or explain why none applies.</p>
    {error && <p role="alert">{error} <button type="button" onClick={candidates.reload}>Retry</button></p>}
    {loading && <p role="status">Loading upstream change requests…</p>}
    {data && !data.isTopOfLadder && <>
      <label><input type="checkbox" checked={includeEarlierBuilds} onChange={event => setIncludeEarlierBuilds(event.target.checked)} /> Include earlier builds</label>
      <label>Find a direct parent<input value={search} onChange={event => setSearch(event.target.value)} placeholder="Search number or title" /></label>
      <div className="upstreamCandidateList">
        {data.candidates.filter(candidate => !candidate.assessmentDerived && !links.some(link => link.upstreamChangeRequestId === candidate.id)).map(candidate =>
          <button type="button" key={candidate.id} disabled={loading || !!error} onClick={() => {
            if (candidate.state === "Deferred" || !candidate.selectable) {
              window.alert(candidate.selectionRefusal ?? (candidate.state === "Deferred"
                ? `${candidate.displayNumber} is deferred and cannot be linked. Reassign this CR to the current build before linking it. The exact revision must also be approved.`
                : `${candidate.displayNumber} cannot be linked. An exact approved revision is required.`));
              return;
            }
            if (noUpstreamRationale && !window.confirm("Replace the authored no-upstream answer with a named upstream link?")) return;
            onNoUpstreamRationale(null);
            onChange([...links, { upstreamChangeRequestId: candidate.id, rationale: "" }]);
          }}>{candidate.displayNumber} · {candidate.title} · {candidate.state === "SelectedForBaseline" ? "Allocated" : candidate.state} (current build {currentBuild} → upstream build {candidate.build}{candidate.earlierBuild ? ", earlier build" : ""})</button>)}
      </div>
      {!loading && !error && data.candidates.length === 0 && <p>No matching upstream change requests.</p>}
      <nav aria-label="Upstream candidate pages">
        <button type="button" disabled={loading || page <= 1} onClick={() => setPage(page - 1)}>Previous</button>
        <span>Page {page} of {Math.max(1, data.totalPages ?? 1)} · {data.totalCount ?? data.candidates.length} results</span>
        <button type="button" disabled={loading || page >= (data.totalPages ?? 1)} onClick={() => setPage(page + 1)}>Next</button>
      </nav>
    </>}
    {(data?.derivedEdges ?? []).map(edge => <p key={edge.assessmentLinkId}><b>Assessment-derived upstream (read-only)</b> {edge.upstreamDisplayNumber} · build {edge.upstreamBuildVersion} · assessment {edge.assessmentId}</p>)}
    {links.map(link => {
      const candidate = known[link.upstreamChangeRequestId];
      const saved = stored.find(item => item.upstreamChangeRequestId === link.upstreamChangeRequestId);
      const number = candidate?.displayNumber ?? saved?.upstreamDisplayNumber ?? link.upstreamChangeRequestId;
      return <div className="upstreamDraftRow" key={link.upstreamChangeRequestId}>
        <b>{number}<small>current build {currentBuild} → upstream build {candidate?.build ?? saved?.upstreamBuildVersion ?? "unknown"}</small></b>
        <input aria-label={`Rationale for ${number}`} value={link.rationale} placeholder="Why is this exact change request upstream?"
          onChange={event => onChange(links.map(item => item === link ? { ...item, rationale: event.target.value } : item))} />
        <button type="button" onClick={() => onChange(links.filter(item => item !== link))}>Remove</button>
      </div>;
    })}
    {data && !data.isTopOfLadder && !data.derivedEdges?.length && links.length === 0 &&
      <label>No upstream change-request rationale<textarea value={noUpstreamRationale ?? ""} onChange={event => onNoUpstreamRationale(event.target.value || null)} placeholder="Explain why no direct upstream change request applies." /></label>}
    {data?.isTopOfLadder && <p>This level is at the top of the configured ladder; its upstream answer is derived.</p>}
  </div>;
}
