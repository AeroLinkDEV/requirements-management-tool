import { useCallback, useEffect, useState } from "react";
import { PersonName } from "./People";
import "./ReviewComments.css";

/**
 * Reviewer comments on a document revision.
 *
 * The same grammar as the change-request side and the same dashed-rule convention, over a different
 * subject. One difference is visible to the reviewer: a managed document is a checked-in DOCX with no
 * structure this system can address, so instead of anchoring to a record they name the section in their own
 * words. That label is a hint rather than a link, and the field says so.
 */
type DocumentComment = {
  id: string;
  authorId: string;
  sectionLabel: string;
  body: string;
  state: "Draft" | "Published";
  decisionRecorded: boolean;
  createdAt: string;
  isMine: boolean;
};

export function DocumentReviewComments({ api, revisionId, canComment }: {
  api: string;
  revisionId: string;
  canComment: boolean;
}) {
  const [comments, setComments] = useState<DocumentComment[]>([]);
  const [drafting, setDrafting] = useState(false);
  const [body, setBody] = useState("");
  const [section, setSection] = useState("");
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState("");

  const reload = useCallback(async () => {
    if (!revisionId) { setComments([]); return; }
    try {
      const response = await fetch(`${api}/api/managed-documents/revisions/${revisionId}/review-comments`);
      if (!response.ok) { setComments([]); return; }
      const payload = await response.json() as { comments: DocumentComment[] };
      setComments(payload.comments ?? []);
    } catch {
      // Commentary must never take the record page down with it.
      setComments([]);
    }
  }, [api, revisionId]);

  useEffect(() => { void reload(); }, [reload]);

  const save = async () => {
    if (!body.trim()) return;
    setBusy(true); setFailure("");
    try {
      const response = await fetch(`${api}/api/managed-documents/revisions/${revisionId}/review-comments`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ body, sectionLabel: section }),
      });
      if (!response.ok) {
        const detail = await response.json().catch(() => undefined) as { error?: string } | undefined;
        setFailure(detail?.error ?? "The comment could not be saved. Nothing was recorded.");
        return;
      }
      setBody(""); setSection(""); setDrafting(false);
      await reload();
    } catch {
      setFailure("The comment could not be saved. Nothing was recorded.");
    } finally {
      setBusy(false);
    }
  };

  const remove = async (id: string) => {
    setBusy(true);
    try {
      await fetch(`${api}/api/managed-documents/revisions/${revisionId}/review-comments/${id}`, { method: "DELETE" });
      await reload();
    } finally { setBusy(false); }
  };

  if (!canComment && comments.length === 0) return null;

  return (
    <div className="reviewComments">
      {comments.map((comment) => (
        <div className={`reviewComment${comment.isMine ? " own" : ""}`} key={comment.id}>
          <header>
            <b><PersonName userName={comment.authorId} /></b>
            {comment.sectionLabel && <span>{comment.sectionLabel}</span>}
            {comment.state === "Draft"
              ? <span className="reviewCommentTag">Only you can see this until you decide</span>
              : !comment.decisionRecorded
                ? <span className="reviewCommentTag stranded">Drafted during this review · no decision recorded</span>
                : null}
          </header>
          <p>{comment.body}</p>
          {comment.isMine && comment.state === "Draft" && (
            <div className="reviewCommentActions">
              <button type="button" disabled={busy} onClick={() => void remove(comment.id)}>Remove</button>
            </div>
          )}
        </div>
      ))}

      {canComment && !drafting && (
        <button type="button" className="reviewCommentAdd" onClick={() => setDrafting(true)}>
          + Add a comment on this revision
        </button>
      )}

      {canComment && drafting && (
        <div className="reviewCommentDraft">
          <input
            className="reviewCommentSection"
            value={section}
            placeholder="Where in the document? e.g. 3.2 Flight plan synchronisation"
            onChange={(event) => setSection(event.target.value)}
          />
          <textarea
            value={body}
            autoFocus
            placeholder="What should the author change?"
            onChange={(event) => setBody(event.target.value)}
          />
          <div className="reviewCommentDraftBar">
            <small>Only you can see this until you decide</small>
            <span>
              <button type="button" disabled={busy} onClick={() => { setDrafting(false); setBody(""); setSection(""); }}>Discard</button>
              <button type="button" className="primary" disabled={busy || !body.trim()} onClick={() => void save()}>Save comment</button>
            </span>
          </div>
        </div>
      )}

      {failure && <p className="reviewCommentFailure">{failure}</p>}
    </div>
  );
}
