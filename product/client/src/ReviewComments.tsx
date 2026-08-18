import { useCallback, useEffect, useMemo, useState } from "react";
import { PersonName } from "./People";
import "./ReviewComments.css";

/**
 * Reviewer comments on a change request under review.
 *
 * These are working communication rather than controlled content, and the interface says so before anybody
 * reads a word: everything uncontrolled is drawn with a dashed rule, against the solid rules the record
 * itself uses. A reviewer should never have to wonder whether what they are typing becomes part of the
 * signed package.
 */

export type ReviewCommentAnchor = "ChangeCase" | "RequirementRevision";

export type ReviewComment = {
  id: string;
  authorId: string;
  anchor: ReviewCommentAnchor;
  requirementChangeId: string | null;
  body: string;
  state: "Draft" | "Published";
  decisionRecorded: boolean;
  createdAt: string;
  updatedAt: string;
  publishedAt: string | null;
  isMine: boolean;
};

type CycleComments = { id: string; sequence: number; state: string; comments: ReviewComment[] };

export type ReviewCommentStore = {
  current: ReviewComment[];
  earlier: CycleComments[];
  busy: boolean;
  failure: string;
  add: (anchor: ReviewCommentAnchor, requirementChangeId: string | null, body: string) => Promise<void>;
  revise: (id: string, body: string) => Promise<void>;
  remove: (id: string) => Promise<void>;
};

export function useReviewComments(api: string, changeRequestId: string, enabled: boolean): ReviewCommentStore {
  const [cycles, setCycles] = useState<CycleComments[]>([]);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState("");

  const reload = useCallback(async () => {
    if (!enabled || !changeRequestId) { setCycles([]); return; }
    try {
      const response = await fetch(`${api}/api/change-requests/${changeRequestId}/review-comments`);
      if (!response.ok) { setCycles([]); return; }
      const payload = await response.json() as { cycles: CycleComments[] };
      setCycles(payload.cycles ?? []);
    } catch {
      // A comment list that cannot be fetched must not take the record page down with it. The controlled
      // content is what the reader came for; this is commentary beside it.
      setCycles([]);
    }
  }, [api, changeRequestId, enabled]);

  useEffect(() => { void reload(); }, [reload]);

  const mutate = useCallback(async (run: () => Promise<Response>, whenFailed: string) => {
    setBusy(true); setFailure("");
    try {
      const response = await run();
      if (!response.ok) {
        const body = await response.json().catch(() => undefined) as { error?: string } | undefined;
        setFailure(body?.error ?? whenFailed);
        return;
      }
      await reload();
    } catch {
      setFailure(whenFailed);
    } finally {
      setBusy(false);
    }
  }, [reload]);

  const add = useCallback((anchor: ReviewCommentAnchor, requirementChangeId: string | null, body: string) =>
    mutate(() => fetch(`${api}/api/change-requests/${changeRequestId}/review-comments`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ anchor, requirementChangeId, body }),
    }), "The comment could not be saved. Nothing was recorded."),
    [api, changeRequestId, mutate]);

  const revise = useCallback((id: string, body: string) =>
    mutate(() => fetch(`${api}/api/change-requests/${changeRequestId}/review-comments/${id}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      // No anchor. Where a comment sits is settled when it is written, and a revise that could restate it
      // is a revise that could silently move somebody's remark to a different requirement.
      body: JSON.stringify({ body }),
    }), "The comment could not be updated. It is unchanged."),
    [api, changeRequestId, mutate]);

  const remove = useCallback((id: string) =>
    mutate(() => fetch(`${api}/api/change-requests/${changeRequestId}/review-comments/${id}`, { method: "DELETE" }),
      "The comment could not be removed."),
    [api, changeRequestId, mutate]);

  return useMemo(() => {
    const ordered = [...cycles].sort((a, b) => b.sequence - a.sequence);
    return {
      current: ordered[0]?.comments ?? [],
      // Older cycles are not deleted, only moved out of the way. Their comments were written about a
      // package that has since been revised, so showing them inline would put stale objections beside
      // current statements.
      earlier: ordered.slice(1).filter((cycle) => cycle.comments.length > 0),
      busy, failure, add, revise, remove,
    };
  }, [cycles, busy, failure, add, revise, remove]);
}

export function ReviewCommentBlock({ store, anchor, requirementChangeId, canComment, label }: {
  store: ReviewCommentStore;
  anchor: ReviewCommentAnchor;
  requirementChangeId?: string;
  canComment: boolean;
  label: string;
}) {
  const [drafting, setDrafting] = useState(false);
  const [editing, setEditing] = useState("");
  const [text, setText] = useState("");

  const mine = store.current.filter((comment) =>
    comment.anchor === anchor && (comment.requirementChangeId ?? null) === (requirementChangeId ?? null));
  if (!canComment && mine.length === 0) return null;

  const submit = async () => {
    if (!text.trim()) return;
    if (editing) await store.revise(editing, text);
    else await store.add(anchor, requirementChangeId ?? null, text);
    setText(""); setDrafting(false); setEditing("");
  };

  return (
    <div className="reviewComments">
      {mine.map((comment) => (
        <div className={`reviewComment${comment.isMine ? " own" : ""}`} key={comment.id}>
          <header>
            <b><PersonName userName={comment.authorId} /></b>
            {comment.state === "Draft"
              ? <span className="reviewCommentTag">Only you can see this until you decide</span>
              : !comment.decisionRecorded
                ? <span className="reviewCommentTag stranded">Drafted during this cycle · no decision recorded</span>
                : null}
          </header>
          <p>{comment.body}</p>
          {comment.isMine && comment.state === "Draft" && (
            <div className="reviewCommentActions">
              <button type="button" disabled={store.busy} onClick={() => { setEditing(comment.id); setText(comment.body); setDrafting(true); }}>Edit</button>
              <button type="button" disabled={store.busy} onClick={() => void store.remove(comment.id)}>Remove</button>
            </div>
          )}
        </div>
      ))}

      {canComment && !drafting && (
        <button type="button" className="reviewCommentAdd" onClick={() => { setDrafting(true); setEditing(""); setText(""); }}>
          + Add a comment on {label}
        </button>
      )}

      {canComment && drafting && (
        <div className="reviewCommentDraft">
          <textarea
            value={text}
            autoFocus
            placeholder={`What should the author change about ${label}?`}
            onChange={(event) => setText(event.target.value)}
          />
          <div className="reviewCommentDraftBar">
            <small>Only you can see this until you decide</small>
            <span>
              <button type="button" disabled={store.busy} onClick={() => { setDrafting(false); setEditing(""); setText(""); }}>Discard</button>
              <button type="button" className="primary" disabled={store.busy || !text.trim()} onClick={() => void submit()}>Save comment</button>
            </span>
          </div>
        </div>
      )}

      {store.failure && <p className="reviewCommentFailure">{store.failure}</p>}
    </div>
  );
}

/** Comments from cycles that have been superseded. Kept, but out of the way. */
export function EarlierCycleComments({ store }: { store: ReviewCommentStore }) {
  if (store.earlier.length === 0) return null;
  return (
    <div className="earlierComments">
      {store.earlier.map((cycle) => (
        <details key={cycle.id}>
          <summary><b>Cycle {cycle.sequence + 1}</b><span>{cycle.comments.length} comment{cycle.comments.length === 1 ? "" : "s"}</span></summary>
          {cycle.comments.map((comment) => (
            <div className="reviewComment" key={comment.id}>
              <header><b><PersonName userName={comment.authorId} /></b></header>
              <p>{comment.body}</p>
            </div>
          ))}
        </details>
      ))}
    </div>
  );
}
