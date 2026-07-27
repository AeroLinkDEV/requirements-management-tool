import { useEffect, useState } from "react";
import "./DocumentActions.css";

/**
 * Generating the document for what you are looking at, from where you are looking at it.
 *
 * Document generation lived only on the Digital Thread, which is the wrong place to look for it: somebody
 * reading the system requirements for 1.6 wants the system requirements document for 1.6, and had to leave the
 * requirements to go and find it. So this renders next to the requirements themselves as well.
 *
 * Which document you get is decided by the build, not by a choice the reader has to make and could get wrong:
 *
 *   a released build   the approved, controlled document that was generated when it was frozen
 *   an in-work build   a draft at the revision the released document will carry, stamped DRAFT
 *
 * The two are genuinely different things — one is a controlled record with a content hash, the other is
 * generated on the spot from the released baseline plus approved changes and is deliberately never persisted
 * (see DraftDocumentGenerator). Offering them as one button labelled "generate" would hide that difference at
 * the exact moment it matters, so the label always says which one this is.
 */
export type DocumentTypeName = "Sysrd" | "SwrdHighLevel" | "SwrdLowLevel";

export type DocumentTarget = { type: DocumentTypeName; label: string };

type ApprovedDocument = {
  id: string;
  type: string;
  displayNumber: string;
  release: string;
};

type Props = {
  api: string;
  projectId: string;
  release: { id: string; version: string; isReleased: boolean };
  targets: DocumentTarget[];
  /** Rendered above the actions. Omitted where the surrounding surface already says what these are. */
  heading?: string;
};

/** The requirement documents a discipline and level correspond to. Empty level means the discipline's whole set. */
export const targetsFor = (scope: "System" | "Software", level?: string): DocumentTarget[] => {
  if (scope === "System") return [{ type: "Sysrd", label: "System Requirements Document" }];
  const high: DocumentTarget = { type: "SwrdHighLevel", label: "Software Requirements Document — High-Level" };
  const low: DocumentTarget = { type: "SwrdLowLevel", label: "Software Requirements Document — Low-Level" };
  if (level === "HighLevel") return [high];
  if (level === "LowLevel") return [low];
  return [high, low];
};

export default function DocumentActions({ api, projectId, release, targets, heading }: Props) {
  const [approved, setApproved] = useState<ApprovedDocument[]>([]);
  const [loaded, setLoaded] = useState(false);

  // Only asked for when it could be the answer. An in-work build has no approved document by definition, and
  // fetching the project's whole document list to decide not to use it is a request made to be discarded.
  useEffect(() => {
    if (!release.isReleased) {
      setApproved([]);
      setLoaded(true);
      return;
    }
    let cancelled = false;
    setLoaded(false);
    fetch(`${api}/api/documents?projectId=${projectId}`)
      .then((response) => (response.ok ? (response.json() as Promise<ApprovedDocument[]>) : []))
      .then((rows) => {
        if (cancelled) return;
        setApproved(Array.isArray(rows) ? rows : []);
        setLoaded(true);
      })
      .catch(() => {
        if (!cancelled) setLoaded(true);
      });
    return () => {
      cancelled = true;
    };
  }, [api, projectId, release.id, release.isReleased]);

  return (
    // Drafts are marked as provisional on the surface itself, not only in their labels. A draft that looked
    // like a controlled record would be the one dangerous thing on the page.
    <section
      className="documentOutputs"
      data-kind={release.isReleased ? "approved" : "draft"}
      aria-label={heading ?? "Generate documents"}
    >
      {heading && <h3>{heading}</h3>}
      {targets.map((target) => {
        // Matched on the version, because the document list reports the build it belongs to by version rather
        // than by identifier.
        const record = approved.find((item) => item.type === target.type && item.release === release.version);
        const draft = !release.isReleased;
        return (
          <div className="documentOutput" key={target.type}>
            <div>
              <b>{target.label}</b>
              <small>
                {draft
                  ? `Draft for ${release.version} — released content plus every approved change, stamped DRAFT`
                  : record
                    ? `Approved ${record.displayNumber} — controlled, with a content hash`
                    : `No approved document was generated for ${release.version}`}
              </small>
            </div>
            {draft ? (
              <div className="documentLinks">
                <a href={`${api}/api/releases/${release.id}/draft-document?type=${target.type}&format=docx`}>
                  Draft DOCX
                </a>
                <a href={`${api}/api/releases/${release.id}/draft-document?type=${target.type}&format=pdf`}>
                  Draft PDF
                </a>
              </div>
            ) : record ? (
              <div className="documentLinks">
                <a href={`${api}/api/documents/${record.id}/download?format=docx`}>Approved DOCX</a>
                <a href={`${api}/api/documents/${record.id}/download?format=pdf`}>Approved PDF</a>
              </div>
            ) : (
              // Says why there is nothing rather than showing a link that would 404. A released build with no
              // document is a real state: the build shipped without that document being generated.
              <span className="documentUnavailable">{loaded ? "Not available" : "Checking…"}</span>
            )}
          </div>
        );
      })}
    </section>
  );
}
