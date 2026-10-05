import { useEffect, useState } from "react";
import { apiRequest } from "./apiClient";

type OriginalNotice = { identifier: string; eventType: string; stage: string; cycle: number; revision: number;
  sourceId: string; sourceFamily: string; snapshotHash: string; originalObligationActive: boolean; explanation: string; currentWorkPath: string };

/** The notice URL survives login and required password changes without accepting a caller return URL. */
export function NotificationLanding({ api, id, onSignOut }: { api: string; id: string; onSignOut: () => void }) {
  const [notice, setNotice] = useState<OriginalNotice>();
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    let current = true;
    void apiRequest<OriginalNotice>(`${api}/api/notifications/${id}/context`).then(value => { if (current) setNotice(value); })
      .catch(() => { if (current) setFailed(true); });
    return () => { current = false; };
  }, [api, id]);
  const openCurrentWork = async () => {
    try {
      const result = await apiRequest<{ path: string }>(`${api}/api/notifications/${id}/current`);
      if (!/^\/open\/(requirement|scr|swcr|managed-document|test-change-request)\/[0-9a-f-]{36}$/i.test(result.path)) throw new Error("Unavailable destination");
      location.assign(result.path);
    } catch { setFailed(true); }
  };
  return <main className="commandCenter"><header><h1>Original notification request</h1><button onClick={onSignOut}>Sign out</button></header>
    {failed ? <p role="alert">This notification is unavailable to your current account and access. It does not grant access to the original record.</p>
      : !notice ? <p role="status">Checking the original request and your current access…</p>
      : <section><h2>{notice.identifier}</h2><p>{notice.stage}</p><dl><dt>Original cycle or round</dt><dd>{notice.cycle || "Not applicable"}</dd>
        <dt>Original revision</dt><dd>{notice.revision || "Not applicable"}</dd><dt>Original task or step</dt><dd>{notice.sourceId}</dd></dl>
        <p>{notice.explanation}</p><p>This page identifies the original notice. Opening current work may show a later cycle or assignment; review that current context before acting.</p>
        <button onClick={() => { void openCurrentWork(); }}>Open current work deliberately</button></section>}
    <p><a href="/">Open AeroLink workspace</a></p></main>;
}
