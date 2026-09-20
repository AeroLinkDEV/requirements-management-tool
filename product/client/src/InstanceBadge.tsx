import { useEffect, useState } from "react";
import { API_ORIGIN } from "./apiOrigin";
import "./InstanceBadge.css";

/**
 * Which AeroLink am I looking at?
 *
 * The mistake this exists to prevent is a specific one, and an easy one: adding a Change Request on
 * 127.0.0.1 at work and assuming it is therefore in the HOME database. Two installations, identical to the
 * pixel, holding different controlled records.
 *
 * So the badge is persistent and quiet. The label (and, for HOME production, the truthful currency
 * word) is the whole signal at a glance; the source revision, database name and snapshot age live in
 * a native disclosure under the summary, where an operator can reach them with keyboard or touch,
 * and nobody has to read them the rest of the time. A hover title remains as a convenience but is
 * never the only access. Deployment diagnostics do not belong in a product surface, but "which
 * installation is this" is not a diagnostic — it is the context every record on the screen belongs to.
 *
 * Canonical status is never inferred here. The API reports what the installation declared, and an
 * installation that declared nothing gets a modest label rather than a flattering one.
 */

type InstanceIdentity = {
  sourceShortSha?: string;
  mode?: string;
  mainCurrency?: { state: string; checkedAtUtc?: string | null; remoteSha?: string | null } | null;
  instance?: {
    label?: string;
    classification?: string;
    snapshot?: {
      sourceLabel?: string | null;
      sourceSha?: string | null;
      createdAtUtc?: string | null;
      activatedAtUtc?: string | null;
    } | null;
  };
  database?: { name?: string | null };
};

const snapshotAge = (createdAtUtc?: string | null) => {
  if (!createdAtUtc) return undefined;
  const created = Date.parse(createdAtUtc);
  if (Number.isNaN(created)) return undefined;
  const days = Math.floor((Date.now() - created) / 86_400_000);
  if (days <= 0) return "taken today";
  return days === 1 ? "1 day old" : `${days} days old`;
};

export default function InstanceBadge() {
  const [identity, setIdentity] = useState<InstanceIdentity | null>(null);

  useEffect(() => {
    let cancelled = false;
    let homeProduction = false;
    let identityEstablished = false;
    let timer: ReturnType<typeof setTimeout>;
    let controller: AbortController;
    // Refresh the passive runtime observation, never GitHub or the deployment controller. Serialize
    // requests and bound a hung read so old success cannot remain on a long-lived page indefinitely.
    const refresh = async () => {
      controller = new AbortController();
      const timeout = setTimeout(() => controller.abort(), 10_000);
      try {
        const response = await fetch(`${API_ORIGIN}/health/identity`, { signal: controller.signal, cache: "no-store" });
        if (!response.ok) throw new Error("Runtime status unavailable");
        const value = await response.json() as InstanceIdentity;
        homeProduction = value.mode === "HOME-PRODUCTION" && value.instance?.classification === "HomeCanonical";
        identityEstablished = true;
        if (!cancelled) setIdentity(value);
      } catch {
        if (!cancelled) setIdentity(previous => previous ? { ...previous,
          mainCurrency: { ...previous.mainCurrency, state: "Unverified" } } : null);
      } finally {
        clearTimeout(timeout);
        if (!cancelled && (!identityEstablished || homeProduction)) timer = setTimeout(() => { void refresh(); }, 60_000);
      }
    };
    void refresh();
    return () => { cancelled = true; clearTimeout(timer); controller?.abort(); };
  }, []);

  if (!identity) return null;

  const label = identity.instance?.label ?? "AEROLINK";
  const classification = identity.instance?.classification ?? "Undeclared";
  const showCurrency = classification === "HomeCanonical" && identity.mode === "HOME-PRODUCTION";
  const currency = identity.mainCurrency;
  const currencyLabel = currency?.state === "Current" ? "Current main"
    : currency?.state === "UpdateAvailable" ? "Main update available" : "Main unverified";
  const checked = currency?.checkedAtUtc ? Date.parse(currency.checkedAtUtc) : NaN;
  const checkAge = Number.isFinite(checked) && checked <= Date.now()
    ? `checked ${Math.floor((Date.now() - checked) / 60_000)}m ago` : "not checked";
  const snapshot = identity.instance?.snapshot ?? null;
  const age = snapshotAge(snapshot?.createdAtUtc);

  // Routine surfaces name the installation, not its deployment classification. The owner asked for one
  // specific thing (#925 P2): the declared HOME CANONICAL label reads as the plain installation name it
  // identifies. That is handled by the single explicit rule below — same declared label, same declared
  // classification, spaced or not — because a general suffix-stripping algorithm guesses at other
  // operators' labels and can erase meaningful distinctions like a Demo declaration. Every label the
  // rule does not name is shown verbatim, and the disclosure keeps the whole declared label,
  // classification, source, database, mode and snapshot facts, so nothing is reclassified, renamed, or
  // inferred — the badge just stops shouting the operator word.
  const plainLabelRules: ReadonlyArray<{ declaredLabel: string; classification: string; plain: string }> = [
    { declaredLabel: "HOME CANONICAL", classification: "HomeCanonical", plain: "HOME" },
  ];
  const normalize = (value: string) => value.replace(/\s+/gu, " ").trim().toUpperCase();
  const matched = plainLabelRules.find(rule =>
    rule.classification === classification.trim() && normalize(rule.declaredLabel) === normalize(label));
  const visibleLabel = matched ? matched.plain : label;

  // Supplied facts only. Fields the server does not send (including the loopback redactions in
  // RuntimeIdentity.cs) stay absent everywhere — never inferred, never fetched from another endpoint.
  const details: ReadonlyArray<[string, string]> = [
    ["Instance", `${label} (${classification})`],
    identity.database?.name ? ["Database", identity.database.name] : null,
    identity.sourceShortSha ? ["Source", identity.sourceShortSha] : null,
    identity.mode ? ["Mode", identity.mode] : null,
    showCurrency ? ["Main currency", `${currencyLabel}; ${checkAge}`] : null,
    showCurrency && currency?.checkedAtUtc ? ["Last check", currency.checkedAtUtc] : null,
    showCurrency && currency?.remoteSha ? ["Last observed remote main", currency.remoteSha] : null,
    snapshot ? ["Snapshot", `from ${snapshot.sourceLabel ?? "another installation"}${age ? `, ${age}` : ""}`] : null,
  ].filter((row): row is [string, string] => row !== null);
  const detail = details.map(([key, value]) => `${key}: ${value}`).join("\n");

  // The closed chip is the quiet summary: declared label and, for HOME production, the truthful
  // currency word. Source, check, declaration and snapshot facts live in the native disclosure so
  // keyboard and touch reach what a hover tooltip alone used to carry (#1048 H04). Opening it grows
  // the header or sidebar in flow; it never issues a request beyond the passive identity read above.
  return (
    <details
      className={`instanceBadge instanceBadge--${classification.toLowerCase()}`}
      title={detail}
      data-testid="instance-badge"
      data-classification={classification}
    >
      <summary data-testid="instance-summary">
        <span data-testid="instance-label">{visibleLabel}</span>
        {showCurrency ? <span className="instanceBadgeCurrency" data-testid="main-currency">{currencyLabel}</span> : null}
        {snapshot ? <em className="instanceBadgeSnapshot">snapshot{age ? ` ${age}` : ""}</em> : null}
      </summary>
      <div className="instanceBadgePanel" data-testid="instance-details">
        {details.map(([key, value]) => <div key={key}><b>{key}</b><span>{value}</span></div>)}
      </div>
    </details>
  );
}
