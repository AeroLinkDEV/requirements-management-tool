import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { FormEvent } from "react";
import { AutosaveState, DraftRestore } from "./DraftNotice";
import { useFormDraft } from "./autosave";
import "./ReleasePlanningCenter.css";

type Release = {
  id: string;
  version: string;
  isReleased: boolean;
  releasedAt?: string;
  predecessorReleaseId?: string;
};
type Baseline = {
  id: string;
  releaseId: string;
  predecessorBaselineId?: string;
  displayNumber: string;
  name: string;
  state: string;
  requirementsMaterializedAt?: string;
  selectionCount: number;
};
type Campaign = {
  id: string;
  releaseId: string;
  baselineId: string;
  state: string;
  name: string;
};
type ChangeCount = { releaseId: string; state: string; count: number };
type Planning = {
  releases: Release[];
  baselines: Baseline[];
  campaigns: Campaign[];
  changes: ChangeCount[];
};
type Props = {
  api: string;
  projectId: string;
  productName: string;
  activeReleaseId: string;
  onBack: () => void;
  onSelectRelease: (id: string) => void;
  onOpenBaselines: () => void;
  onOpenCampaign: () => void;
  onChanged: () => Promise<void>;
};
const versionParts = (value: string) =>
  value
    .split(/[^0-9]+/)
    .filter(Boolean)
    .map(Number);
const compareVersions = (a: Release, b: Release) => {
  const ap = versionParts(a.version),
    bp = versionParts(b.version);
  for (let i = 0; i < Math.max(ap.length, bp.length); i++) {
    const d = (ap[i] ?? 0) - (bp[i] ?? 0);
    if (d) return d;
  }
  return a.version.localeCompare(b.version);
};
const suggestNext = (value: string) => {
  const parts = value.split("."),
    last = Number(parts.at(-1));
  return Number.isFinite(last)
    ? [...parts.slice(0, -1), String(last + 1)].join(".")
    : `${value}.1`;
};

export default function ReleasePlanningCenter({
  api,
  projectId,
  productName,
  activeReleaseId,
  onBack,
  onSelectRelease,
  onOpenBaselines,
  onOpenCampaign,
  onChanged,
}: Props) {
  const [data, setData] = useState<Planning>(),
    [creating, setCreating] = useState(false),
    [campaignBaseline, setCampaignBaseline] = useState<Baseline>(),
    [error, setError] = useState(""),
    [loading, setLoading] = useState(true),
    [busy, setBusy] = useState(false);
  const load = useCallback(async () => {
    setLoading(true);
    try {
      const response = await fetch(
        `${api}/api/release-planning?projectId=${projectId}`,
      );
      if (!response.ok) throw new Error("Release planning could not be loaded.");
      setData(await response.json());
      setError("");
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "Release planning could not be loaded.");
    } finally {
      setLoading(false);
    }
  }, [api, projectId]);
  useEffect(() => {
    load();
  }, [load]);
  const releases = useMemo(
    () => [...(data?.releases ?? [])].sort(compareVersions),
    [data],
  );
  const latest = releases.at(-1),
    latestReleased = [...releases].reverse().find((x) => x.isReleased);
  const inWorkRelease = releases.find((x) => !x.isReleased);
  const count = (releaseId: string, state?: string) =>
    (data?.changes ?? [])
      .filter((x) => x.releaseId === releaseId && (!state || x.state === state))
      .reduce((n, x) => n + x.count, 0);
  const releaseForm = useRef<HTMLFormElement>(null);
  const campaignForm = useRef<HTMLFormElement>(null);
  const releaseDraft = useFormDraft(releaseForm, `aerolink:new-release:${projectId}`);
  const campaignDraft = useFormDraft(campaignForm, `aerolink:new-campaign:${projectId}`);

  const createRelease = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    const form = new FormData(e.currentTarget);
    setBusy(true);
    setError("");
    const response = await fetch(`${api}/api/releases`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        projectId,
        version: form.get("version"),
        predecessorReleaseId: form.get("predecessorReleaseId") || null,
      }),
    });
    if (!response.ok) {
      setError(
        (await response.json()).error || "Release could not be created.",
      );
      setBusy(false);
      return;
    }
    const created = await response.json();
    releaseDraft.clear();
    await onChanged();
    await load();
    onSelectRelease(created.id);
    setCreating(false);
    setBusy(false);
  };
  const createCampaign = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!campaignBaseline) return;
    const form = new FormData(e.currentTarget);
    setBusy(true);
    setError("");
    const response = await fetch(`${api}/api/release-campaigns`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        projectId,
        releaseId: campaignBaseline.releaseId,
        baselineId: campaignBaseline.id,
        name: form.get("name"),
      }),
    });
    if (!response.ok) {
      setError(
        (await response.json()).error || "Campaign could not be created.",
      );
      setBusy(false);
      return;
    }
    campaignDraft.clear();
    onSelectRelease(campaignBaseline.releaseId);
    await load();
    setCampaignBaseline(undefined);
    setBusy(false);
    onOpenCampaign();
  };
  return (
    <main className="planningPage">
      <header>
        <div>
          <button className="back" onClick={onBack}>
            ← Command Center
          </button>
          <p className="eyebrow">PRODUCT EVOLUTION / CONFIGURATION CONTROL</p>
          <h1>Release Planning</h1>
          <p>
            Create each in-work version from an exact released predecessor,
            approve its changes, and release only through formal gates.
          </p>
        </div>
        <button
          className="primary"
          disabled={Boolean(inWorkRelease)}
          onClick={() => setCreating(true)}
          title={
            inWorkRelease
              ? `Release ${inWorkRelease.version} before planning its successor.`
              : "Plan the next product version"
          }
        >
          {inWorkRelease
            ? `Complete ${inWorkRelease.version} before next`
            : "+ Plan successor release"}
        </button>
      </header>
      {error && <div className="workspaceError">{error}</div>}
      {loading && !data && <section className="planningState" aria-live="polite"><i/><div><b>Loading controlled product history</b><span>Resolving versions, baselines, and campaigns…</span></div></section>}
      <section className="continuity">
        <div>
          <span>CONTROLLED PRODUCT LINE</span>
          <h2>{productName}</h2>
          <p>
            Released baselines remain immutable. Every successor carries their
            exact history forward.
          </p>
        </div>
        <div className="continuityRule">
          {releases.slice(-4).map((item, index, visible) => <span className={item.isReleased ? "released" : "inwork"} key={item.id}><b>{item.version}</b>{index < visible.length - 1 && <i/>}</span>)}
          {!releases.length && <b>First release</b>}
          <small>{inWorkRelease ? `${inWorkRelease.version} remains in work until its release gates are complete.` : `The next suggested version is ${suggestNext(latest?.version ?? "1.0")}.`}</small>
        </div>
      </section>
      <section className="releaseTimeline">
        {releases.map((release, index) => {
          const baselines =
              data?.baselines.filter((x) => x.releaseId === release.id) ?? [],
            campaign = data?.campaigns.find((x) => x.releaseId === release.id),
            active = release.id === activeReleaseId;
          return (
            <article
              className={`${release.isReleased ? "released" : "inwork"} ${active ? "active" : ""}`}
              key={release.id}
            >
              <div className="versionRail">
                <span>{index + 1}</span>
                <i />
              </div>
              <div className="releaseCard">
                <div className="releaseTitle">
                  <div>
                    <small>
                      {release.isReleased
                        ? "RELEASED PRODUCT VERSION"
                        : `IN-WORK TARGET VERSION${release.predecessorReleaseId ? ` · FROM ${releases.find((x) => x.id === release.predecessorReleaseId)?.version ?? "CONTROLLED PREDECESSOR"}` : ""}`}
                    </small>
                    <h2>
                      {productName} {release.version}
                    </h2>
                  </div>
                  <span>{release.isReleased ? "Locked" : "Editable"}</span>
                </div>
                <div className="releaseFacts">
                  <div>
                    <b>{count(release.id)}</b>
                    <small>change requests</small>
                  </div>
                  <div>
                    <b>
                      {count(release.id, "Approved") +
                        count(release.id, "SelectedForBaseline")}
                    </b>
                    <small>approved / selected</small>
                  </div>
                  <div>
                    <b>{baselines.length}</b>
                    <small>baseline manifests</small>
                  </div>
                  <div>
                    <b>{campaign?.state ?? "Not started"}</b>
                    <small>release campaign</small>
                  </div>
                </div>
                <div className="releaseActions">
                  <button onClick={() => onSelectRelease(release.id)}>
                    {active ? "Active workspace" : "Work in this version"}
                  </button>
                  {!release.isReleased && (
                    <button
                      onClick={() => {
                        onSelectRelease(release.id);
                        onOpenBaselines();
                      }}
                    >
                      Assemble baseline
                    </button>
                  )}
                  {campaign && (
                    <button
                      onClick={() => {
                        onSelectRelease(release.id);
                        onOpenCampaign();
                      }}
                    >
                      Open campaign
                    </button>
                  )}
                  {!release.isReleased &&
                    !campaign &&
                    baselines.map((x) => (
                      <button
                        key={x.id}
                        disabled={x.state === "Released"}
                        onClick={() => setCampaignBaseline(x)}
                      >
                        Create campaign from {x.displayNumber}
                      </button>
                    ))}
                </div>
              </div>
            </article>
          );
        })}
      </section>
      <section className="authority">
        <div>
          <b>Release authority stays with your team</b>
          <p>
            AeroLink never creates future baselines or approvals automatically.
            It calculates readiness and blocks unsafe progression; assigned
            humans approve exact change revisions and the release campaign.
          </p>
        </div>
        {[
          "Approve every intended change",
          "Select exact revisions into a candidate",
          "Freeze and materialize controlled documents",
          "Attach build, tests, evidence, and dispositions",
          "Collect unanimous electronic release approval",
          "Release the baseline, then plan the next version",
        ].map((x, i) => (
          <span key={x}>
            <i>{i + 1}</i>
            {x}
          </span>
        ))}
      </section>
      {creating && (
        <div className="planningModal">
          <form onSubmit={createRelease} ref={releaseForm}>
            <p className="eyebrow">NEW IN-WORK VERSION</p>
            <h2>Plan successor release</h2>
            {releaseDraft.offered && (
              <DraftRestore savedAt={releaseDraft.offered.savedAt}
                description="An unfinished release plan was left in this browser. Nothing was created."
                onRestore={releaseDraft.apply} onDiscard={releaseDraft.discard} />
            )}
            <AutosaveState status={releaseDraft.status} savedAt={releaseDraft.savedAt} where="this browser" />
            <label>
              New version
              <input
                name="version"
                defaultValue={suggestNext(latest?.version ?? "1.0")}
                required
              />
            </label>
            <label>
              Released predecessor
              <select
                name="predecessorReleaseId"
                defaultValue={latestReleased?.id ?? ""}
                required
              >
                <option value="">Select released predecessor…</option>
                {releases
                  .filter((x) => x.isReleased)
                  .map((x) => (
                    <option key={x.id} value={x.id}>
                      {productName} {x.version}
                    </option>
                  ))}
              </select>
            </label>
            <p>
              This creates an empty release workspace only. No baseline is
              created or approved for you.
            </p>
            <div>
              <button type="button" onClick={() => setCreating(false)}>
                Cancel
              </button>
              <button className="primary" disabled={busy}>
                {busy ? "Creating…" : "Create in-work release"}
              </button>
            </div>
          </form>
        </div>
      )}
      {campaignBaseline && (
        <div className="planningModal">
          <form onSubmit={createCampaign} ref={campaignForm}>
            <p className="eyebrow">FORMAL RELEASE CONTROL</p>
            <h2>Create release campaign</h2>
            {campaignDraft.offered && (
              <DraftRestore savedAt={campaignDraft.offered.savedAt}
                description="An unfinished campaign was left in this browser. Nothing was created."
                onRestore={campaignDraft.apply} onDiscard={campaignDraft.discard} />
            )}
            <AutosaveState status={campaignDraft.status} savedAt={campaignDraft.savedAt} where="this browser" />
            <p>
              {campaignBaseline.displayNumber} · {campaignBaseline.name}
            </p>
            <label>
              Campaign name
              <input
                name="name"
                defaultValue={`${productName} ${releases.find((x) => x.id === campaignBaseline.releaseId)?.version} Release Campaign`}
                required
              />
            </label>
            <div>
              <button
                type="button"
                onClick={() => setCampaignBaseline(undefined)}
              >
                Cancel
              </button>
              <button className="primary" disabled={busy}>
                {busy ? "Creating…" : "Create campaign"}
              </button>
            </div>
          </form>
        </div>
      )}
    </main>
  );
}
