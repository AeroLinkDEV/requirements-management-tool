import { useCallback, useEffect, useRef, useState } from "react";
import { PersonName } from "./People";
import { stateLabel } from "./presentation";
import type { FormEvent } from "react";
import "./EnterpriseControlCenter.css";
import "./EnterpriseControlOverrides.css";
import EnterpriseLifecycleAssurance from "./EnterpriseLifecycleAssurance";
import {
  ApiError,
  apiRequest,
  operationError,
  recordClientOperationFailure,
} from "./apiClient";

type Requirement = {
  id: string;
  displayNumber: string;
  revisionId: string;
  statement: string;
  level: string;
};
type Attachment = {
  id: string;
  logicalId: string;
  version: number;
  revisionId?: string;
  label: string;
  description: string;
  originalFileName: string;
  contentType: string;
  size: number;
  sha256: string;
  state: string;
  uploadedBy: string;
  uploadedAt: string;
  integrityVerifiedAt?: string;
};
type Job = {
  id: string;
  jobType: string;
  state: string;
  itemCount: number;
  succeededCount: number;
  failedCount: number;
  progressPercent: number;
  attempt: number;
  idempotencyKey: string;
  lastError?: string;
  createdBy: string;
  createdAt: string;
  resultJson: string;
  claimedBy?: string;
  claimedAt?: string;
  leaseExpiresAt?: string;
  maximumAttempts: number;
  errorHistory: { attempt: number; occurredAt: string; error: string }[];
};
type Session = {
  id: string;
  artifactId: string;
  artifactType: string;
  userName: string;
  openedAt: string;
  updatedAt: string;
  version: number;
};
type Conflict = {
  id: string;
  artifactId: string;
  localSessionId: string;
  competingSessionId: string;
  createdBy: string;
  createdAt: string;
};
type View = {
  id: string;
  name: string;
  queryJson: string;
  columnsJson: string;
  isShared: boolean;
  owned: boolean;
};
/**
 * Queued and claimed are both 'Running' underneath, and an operator needs them apart: a queued job is waiting
 * for a worker, a claimed one has one. A job whose lease has lapsed is neither — it is waiting to be recovered,
 * and saying 'Running' about it was the reason a crashed job looked healthy.
 */
const jobStateLabel = (job: Job) => {
  if (job.state !== "Running")
    return job.state === "Preview" ? "Queued" : stateLabel(job.state);
  if (
    job.leaseExpiresAt &&
    new Date(job.leaseExpiresAt).getTime() <= Date.now()
  )
    return "Recovering";
  return job.claimedBy ? "Claimed" : "Starting";
};

const exportExpired = (job: Job) => {
  if (job.jobType !== "BackgroundRepositoryExport") return false;
  try {
    const result = JSON.parse(job.resultJson);
    return typeof result.ExpiresAt === "string" && Date.parse(result.ExpiresAt) <= Date.now();
  } catch {
    return false;
  }
};

type Overview = {
  generatedAt: string;
  repository: {
    artifacts: number;
    revisions: number;
    attachments: number;
    attachmentVersions: number;
    attachmentBytes: number;
    missingFiles: number;
    views: number;
  };
  jobs: Job[];
  sessions: Session[];
  conflicts: Conflict[];
  views: View[];
  checkpoint?: {
    id: string;
    state: string;
    manifestHash: string;
    detail: string;
    createdAt: string;
    createdBy: string;
  };
};
type Redline = {
  from: number;
  to: number;
  statement: { kind: string; text: string }[];
  rationale: { kind: string; text: string }[];
  verificationChanged: boolean;
  fromVerification: string;
  toVerification: string;
};
type Performance = {
  totalRequirements: number;
  scaleTarget: number;
  allPassed: boolean;
  samples: { name: string; targetMs: number; p95Ms: number; passed: boolean }[];
};
type NotificationMode = "Disabled" | "Capture" | "ControlledTest" | "Live";
type NotificationSettings = {
  version: number; mode: NotificationMode; host: string; port: number; sender: string;
  displayName: string; baseUrl: string; userNameConfigured: boolean; credentialConfigured: boolean;
};
type NotificationGeneration = {
  id: string; deliveryId: string; notificationId: string; contextIdentifier: string; mode: NotificationMode;
  state: string; version: number; messageId: string; bodyHash: string; intendedDestination: string;
  effectiveDestination: string; attempts: number; dueAt?: string; deadlineAt?: string; safeCode?: string; createdAt: string;
};
type NotificationAttempt = {
  id: string; startedAt: string; completedAt?: string; outcome: string; phase: string;
  smtpStatus?: number; safeCode?: string; transportDisposed: boolean; quiescence: "confirmed" | "unproven";
};
type NotificationOperations = {
  generatedAt: string;
  smtp: { configured: boolean; hostConfigured: boolean; port?: number; portValid: boolean; useStartTls: boolean; credentialsConfigured: boolean; fromConfigured: boolean };
  links: { configured: boolean; valid: boolean; baseUrl?: string };
  totals: { pending: number; sent: number; failed: number; suppressed: number };
  deliveries: { id: string; recipient: string; address: string; state: string; attempts: number; detail?: string; createdAt: string; completedAt?: string }[];
  installation: { id: string; label: string; database: string; version: string | number };
  settings: NotificationSettings;
  locks: { relay: boolean; sender: boolean; baseUrl: boolean; credentials: boolean; externalModes: boolean; diagnosticTarget: boolean };
  policy: { maximumMode: NotificationMode; diagnosticTarget: "protected" | "not-configured"; sendAuthority: "current" | "blocked" };
  health: { state: string; count: number; oldestAt?: string; action: string }[];
  generations: NotificationGeneration[]; page: number; total: number; pageSize: number;
};
type NotificationFamily = "Settings" | "TransportTest" | "Activate" | "Resume" | "Readmit" | "Replay" | "Reissue" | "Suppress";
type NotificationIntent = {
  operationKey: string; family: NotificationFamily; expectedVersion: number;
  projectId?: string; mode?: NotificationMode; deliveryId?: string; generationId?: string; expectedGenerationVersion?: number;
  acknowledgeDuplicateRisk?: boolean; host?: string; port?: number; sender?: string; displayName?: string;
  baseUrl?: string; userName?: string; clearCredential?: boolean; requiresCredential?: boolean;
};
type NotificationReceipt = {
  id: string; operationKey: string; family: NotificationFamily; createdAt: string;
  result: { state: string; notificationId?: string; generationId?: string; version?: number };
};
const notificationModes: NotificationMode[] = ["Disabled", "Capture", "ControlledTest", "Live"];
const notificationFamilies: NotificationFamily[] = ["Settings", "TransportTest", "Activate", "Resume", "Readmit", "Replay", "Reissue", "Suppress"];
// Only these non-secret semantic fields enter storage; credentials remain in memory.
const readNotificationIntent = (key: string): NotificationIntent | undefined => {
  const storedKey = Object.keys(localStorage).filter(candidate => candidate.startsWith(`${key}:`)).sort()[0];
  const raw = storedKey ? localStorage.getItem(storedKey) : null;
  if (!raw) return undefined;
  const value = JSON.parse(raw) as NotificationIntent;
  if (!value || !/^[0-9a-f-]{36}$/i.test(value.operationKey) || !notificationFamilies.includes(value.family)
    || !Number.isInteger(value.expectedVersion) || Object.keys(value).some(field => ![
      "operationKey", "family", "expectedVersion", "projectId", "mode", "deliveryId", "generationId", "expectedGenerationVersion",
      "acknowledgeDuplicateRisk", "host", "port", "sender", "displayName", "baseUrl", "userName", "clearCredential", "requiresCredential",
    ].includes(field))) throw new Error("Stored notification operation is invalid. Preserve it for administrator recovery.");
  return value;
};
type Tab =
  | "command"
  | "content"
  | "redlines"
  | "queries"
  | "jobs"
  | "configuration"
  | "assurance"
  | "qualification"
  | "notifications";

/**
 * Saved-view contracts are stored as arbitrary JSON, and this tab renders shared views created by anyone.
 * One malformed record took the whole tab down, which is a poor trade for a summary line — a view that
 * cannot be described is still a view somebody needs to open or delete.
 */
const safeParse = <T,>(json: string | undefined, fallback: T): T => {
  try {
    const value = JSON.parse(json ?? "");
    return value ?? fallback;
  } catch {
    return fallback;
  }
};

/** A saved view is a requirements query, so its stable link is the Requirements route that applies it. */
const viewDiscipline = (queryJson: string): "system" | "software" => {
  const level = String(
    safeParse<{ level?: string }>(queryJson, {}).level ?? "",
  );
  return level === "Software" || level === "HighLevel" || level === "LowLevel"
    ? "software"
    : "system";
};

export default function EnterpriseControlCenter({
  api,
  projectId,
  viewLink,
  onBack,
}: {
  api: string;
  projectId: string;
  viewLink: (id: string, discipline: "system" | "software") => string;
  onBack: () => void;
}) {
  const [tab, setTab] = useState<Tab>("command"),
    [overview, setOverview] = useState<Overview>(),
    [requirements, setRequirements] = useState<Requirement[]>([]),
    [selectedId, setSelectedId] = useState(""),
    [attachments, setAttachments] = useState<Attachment[]>([]),
    [history, setHistory] = useState<
      { id: string; revision: number; displayNumber: string }[]
    >([]),
    [redline, setRedline] = useState<Redline>(),
    [performance, setPerformance] = useState<Performance>(),
    [notificationOperations, setNotificationOperations] = useState<NotificationOperations>(),
    [message, setMessage] = useState(""),
    [error, setError] = useState(""),
    [busy, setBusy] = useState(false);
  const [notificationPage, setNotificationPage] = useState(1);
  const [notificationDraft, setNotificationDraft] = useState<NotificationSettings>();
  const [notificationUserName, setNotificationUserName] = useState("");
  const [clearNotificationUserName, setClearNotificationUserName] = useState(false);
  const [notificationCredential, setNotificationCredential] = useState("");
  const [clearNotificationCredential, setClearNotificationCredential] = useState(false);
  const [notificationStorageKey, setNotificationStorageKey] = useState("");
  const [notificationIntent, setNotificationIntent] = useState<NotificationIntent>();
  const [notificationReceipt, setNotificationReceipt] = useState<NotificationReceipt>();
  const [notificationRejected, setNotificationRejected] = useState(false);
  const [notificationNotCommitted, setNotificationNotCommitted] = useState(false);
  const [notificationAttempts, setNotificationAttempts] = useState<{ generationId: string; attempts: NotificationAttempt[] }>();
  const [duplicateRiskAcknowledged, setDuplicateRiskAcknowledged] = useState(false);
  const notificationInFlight = useRef(false);
  const notificationReadSequence = useRef(0);
  const selected = requirements.find((x) => x.id === selectedId);
  const mutate = async <T,>(
    operation: string,
    fallback: string,
    work: () => Promise<T>,
  ) => {
    if (busy) return null;
    setBusy(true);
    setError("");
    setMessage("");
    try {
      return await work();
    } catch (error) {
      recordClientOperationFailure(operation, error);
      setError(operationError(error, fallback));
      return null;
    } finally {
      setBusy(false);
    }
  };
  const load = useCallback(async () => {
    try {
      const [o, w, p] = await Promise.all([
        fetch(`${api}/api/enterprise-hardening/overview?projectId=${projectId}`),
        fetch(
          `${api}/api/enterprise-requirements/workspace?projectId=${projectId}&page=1&pageSize=100&sort=updated`,
        ),
        fetch(
          `${api}/api/enterprise-requirements/performance?projectId=${projectId}`,
        ),
      ]);
      if (o.ok) setOverview(await o.json());
      if (w.ok) {
        const data = await w.json();
        setRequirements(data.items);
        setSelectedId((x) => x || data.items[0]?.id || "");
      }
      if (p.ok) setPerformance(await p.json());
    } catch {
      setError("System operations could not be loaded. Refresh to try again.");
    }
  }, [api, projectId]);
  const loadNotificationOperations = useCallback(async () => {
    const sequence = ++notificationReadSequence.current;
    try {
      const response = await fetch(`${api}/api/operations/notifications?page=${notificationPage}&pageSize=25`);
      if (sequence !== notificationReadSequence.current) return;
      if (response.ok) {
        const operations = await response.json() as NotificationOperations;
        if (sequence === notificationReadSequence.current) {
          setNotificationOperations(operations);
          return operations;
        }
        return;
      }
      setNotificationOperations(undefined);
      setError(response.status === 403
        ? "Notification delivery operations are available only to global administrators."
        : "Notification delivery operations could not be loaded.");
    } catch {
      if (sequence !== notificationReadSequence.current) return;
      setNotificationOperations(undefined);
      setError("Notification delivery operations could not be loaded. Refresh to try again.");
    }
  }, [api, notificationPage]);
  useEffect(() => {
    if (notificationOperations && !notificationDraft) setNotificationDraft({ ...notificationOperations.settings });
  }, [notificationOperations, notificationDraft]);
  useEffect(() => {
    if (!notificationOperations?.installation.id) return;
    let active = true;
    const initialize = async () => {
      try {
        const actor = await apiRequest<{ id: string }>(`${api}/api/auth/me`);
        if (!actor.id || !active) return;
        const key = `aerolink-notification-operation:${api}:${notificationOperations.installation.id}:${actor.id}`;
        const intent = readNotificationIntent(key);
        if (active) { setNotificationStorageKey(key); setNotificationIntent(intent); }
      } catch {
        if (active) setError("Notification operation recovery storage is unavailable. No new operation can be sent.");
      }
    };
    void initialize();
    return () => { active = false; };
  }, [api, notificationOperations?.installation.id]);
  const acceptNotificationReceipt = async (receipt: NotificationReceipt, intent: NotificationIntent) => {
    if (!receipt || receipt.operationKey !== intent.operationKey || receipt.family !== intent.family || !receipt.id || !receipt.result?.state)
      throw new Error("The operation receipt could not be confirmed. Recover the original operation before proceeding.");
    setNotificationReceipt(receipt);
    localStorage.removeItem(`${notificationStorageKey}:${intent.operationKey}`);
    setNotificationIntent(readNotificationIntent(notificationStorageKey));
    setNotificationCredential(""); setNotificationUserName(""); setClearNotificationUserName(false); setClearNotificationCredential(false);
    setNotificationRejected(false); setNotificationNotCommitted(false);
    setDuplicateRiskAcknowledged(false);
    setMessage(`Recorded ${receipt.family}: ${stateLabel(receipt.result.state)}. This receipt does not confirm inbox arrival.`);
    const current = await loadNotificationOperations();
    if (current) setNotificationDraft({ ...current.settings });
  };
  const findNotificationReceipt = async (intent: NotificationIntent) => {
    try {
      const receipt = await apiRequest<NotificationReceipt>(`${api}/api/operations/notifications/operations/${intent.operationKey}?family=${intent.family}`);
      await acceptNotificationReceipt(receipt, intent);
      return true;
    } catch (failure) {
      if (failure instanceof ApiError && failure.status === 404) { setNotificationNotCommitted(true); return false; }
      throw failure;
    }
  };
  const runNotificationIntent = async (intent: NotificationIntent, recover: boolean, retry: boolean) => {
    if (notificationInFlight.current || !notificationStorageKey) return;
    notificationInFlight.current = true;
    let requestStarted = false;
    setBusy(true); setError(""); setMessage("");
    try {
      if (recover && await findNotificationReceipt(intent)) return;
      if (recover && !retry) {
        setMessage("No committed receipt was found. Retry the original operation with its existing identity; this does not request another send.");
        return;
      }
      if (intent.requiresCredential && !notificationCredential) {
        setError("Re-enter the original SMTP credential to retry these same settings. The credential was kept only in memory.");
        return;
      }
      if (!recover) {
        const existing = readNotificationIntent(notificationStorageKey);
        if (existing) {
          setNotificationIntent(existing);
          setMessage("Another tab has an unresolved operation. Recover that original intent first.");
          return;
        }
        // Each key has its own durable entry, so simultaneous tabs cannot overwrite recovery identities.
        localStorage.setItem(`${notificationStorageKey}:${intent.operationKey}`, JSON.stringify(intent));
        setNotificationIntent(intent); setNotificationReceipt(undefined);
        setNotificationRejected(false); setNotificationNotCommitted(false);
      }
      const { requiresCredential, ...payload } = intent;
      const endpoint = intent.family === "Settings" ? "settings" : intent.family === "TransportTest" ? "transport-test" : "commands";
      requestStarted = true;
      const receipt = await apiRequest<NotificationReceipt>(`${api}/api/operations/notifications/${endpoint}`, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ...payload, ...(requiresCredential ? { credential: notificationCredential } : {}) }),
      });
      await acceptNotificationReceipt(receipt, intent);
    } catch (failure) {
      recordClientOperationFailure("operations.notifications.command", failure);
      if (!recover && !requestStarted) {
        setError("The original intent could not be preserved in recovery storage. No notification command was sent.");
        return;
      }
      const refused = failure instanceof ApiError && [400, 403, 409, 422].includes(failure.status);
      setNotificationRejected(refused);
      setError(refused ? "The operation was refused. Check its original receipt before correcting or discarding this intent."
        : "The outcome could not be confirmed. Recover the original operation before requesting another action.");
    } finally { notificationInFlight.current = false; setBusy(false); }
  };
  const newNotificationIntent = (fields: Omit<NotificationIntent, "operationKey" | "expectedVersion"> & { expectedVersion?: number }) => {
    if (!notificationOperations || notificationIntent || !notificationStorageKey || busy) return;
    void runNotificationIntent({ ...fields, operationKey: crypto.randomUUID(), expectedVersion: fields.expectedVersion ?? notificationOperations.settings.version }, false, false);
  };
  const queueTransportTest = () => newNotificationIntent({ family: "TransportTest", projectId });
  const saveNotificationSettings = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!notificationDraft) return;
    newNotificationIntent({ family: "Settings", mode: notificationDraft.mode, host: notificationDraft.host, port: notificationDraft.port,
      sender: notificationDraft.sender, displayName: notificationDraft.displayName, baseUrl: notificationDraft.baseUrl,
      expectedVersion: notificationDraft.version, userName: notificationOperations?.locks.credentials ? undefined : clearNotificationUserName ? "" : notificationUserName || undefined,
      clearCredential: !notificationOperations?.locks.credentials && clearNotificationCredential,
      requiresCredential: !notificationOperations?.locks.credentials && !!notificationCredential });
  };
  const notificationCommand = (family: NotificationFamily, generation?: NotificationGeneration) => {
    newNotificationIntent({ family, mode: family === "Activate" || family === "Resume" ? notificationOperations?.settings.mode : undefined,
      generationId: generation?.id, expectedGenerationVersion: generation?.version,
      acknowledgeDuplicateRisk: family === "Replay" ? duplicateRiskAcknowledged : undefined });
  };
  const loadNotificationAttempts = async (generationId: string) => {
    try {
      const result = await apiRequest<{ attempts: NotificationAttempt[] }>(`${api}/api/operations/notifications/generations/${generationId}/attempts`);
      setNotificationAttempts({ generationId, attempts: result.attempts });
    } catch { setError("Attempt evidence could not be loaded. Refresh to try again."); }
  };
  useEffect(() => {
    load();
    const timer = setInterval(load, 2500);
    return () => clearInterval(timer);
  }, [load]);
  useEffect(() => {
    if (tab !== "notifications") return;
    loadNotificationOperations();
    const timer = setInterval(loadNotificationOperations, 2500);
    return () => clearInterval(timer);
  }, [loadNotificationOperations, tab]);
  const loadArtifact = useCallback(async () => {
    if (!selectedId) return;
    const [a, d] = await Promise.all([
      fetch(
        `${api}/api/enterprise-hardening/attachments?projectId=${projectId}&artifactType=Requirement&artifactId=${selectedId}`,
      ),
      fetch(`${api}/api/enterprise-requirements/${selectedId}`),
    ]);
    if (a.ok) setAttachments(await a.json());
    if (d.ok) {
      const detail = await d.json();
      setHistory(detail.history);
      if (detail.history.length >= 2) {
        const [to, from] = detail.history;
        const r = await fetch(
          `${api}/api/enterprise-requirements/${selectedId}/redline?fromRevisionId=${from.id}&toRevisionId=${to.id}`,
        );
        if (r.ok) setRedline(await r.json());
        else setRedline(undefined);
      } else setRedline(undefined);
    }
  }, [api, projectId, selectedId]);
  useEffect(() => {
    loadArtifact();
  }, [loadArtifact]);
  const upload = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!selected) return;
    const target = e.currentTarget;
    const body = new FormData(target);
    body.set("projectId", projectId);
    body.set("artifactType", "Requirement");
    body.set("artifactId", selected.id);
    body.set("revisionId", selected.revisionId);
    const stored = await mutate(
      "enterprise.attachment.upload",
      "Upload failed.",
      () =>
        apiRequest<{ id: string }>(
          `${api}/api/enterprise-hardening/attachments?${new URLSearchParams({
            projectId,
            artifactType: "Requirement",
            artifactId: selected.id,
          }).toString()}`,
          { method: "POST", body },
        ),
    );
    if (stored === null) return;
    target.reset();
    setMessage("Controlled attachment version stored and checksummed.");
    await loadArtifact();
    await load();
  };
  const versionFile = async (item: Attachment, file: File) => {
    // A superseding upload stays within its chain's exact revision identity, so a chain bound to an
    // earlier revision cannot take the currently selected one. Say so before the server refuses it.
    if (item.revisionId && item.revisionId !== selected?.revisionId) {
      setMessage(
        `${item.label} is bound to an earlier revision of this requirement and cannot be versioned onto the selected one. Attach evidence for the selected revision as a new file instead, so the new chain records that exact revision.`,
      );
      return;
    }
    const body = new FormData();
    body.set("projectId", projectId);
    body.set("artifactType", "Requirement");
    body.set("artifactId", selectedId);
    body.set("revisionId", selected?.revisionId || "");
    body.set("logicalId", item.logicalId);
    body.set("label", item.label);
    body.set("description", item.description);
    body.set("file", file);
    const stored = await mutate(
      "enterprise.attachment.version",
      "The new attachment version was not stored.",
      () =>
        apiRequest<{ id: string }>(
          `${api}/api/enterprise-hardening/attachments?${new URLSearchParams({
            projectId,
            artifactType: "Requirement",
            artifactId: selectedId,
          }).toString()}`,
          { method: "POST", body },
        ),
    );
    if (stored === null) return;
    setMessage(`${item.label} advanced to the next controlled version.`);
    await loadArtifact();
    await load();
  };
  const verify = async (id: string) => {
    const result = await mutate(
      "enterprise.attachment.verify",
      "Integrity verification could not be completed.",
      () =>
        apiRequest<{ valid: boolean }>(
          `${api}/api/enterprise-hardening/attachments/${id}/verify`,
          { method: "POST" },
        ),
    );
    if (result === null) return;
    setMessage(
      result.valid
        ? "Integrity verified against the stored SHA-256 digest."
        : "Integrity verification failed.",
    );
    await loadArtifact();
    await load();
  };
  const saveView = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    const target = e.currentTarget;
    const f = new FormData(target);
    const query = {
      search: f.get("search"),
      level: f.get("level"),
      verification: f.get("verification"),
      state: f.get("state"),
      owner: f.get("owner"),
      openComments: f.has("openComments"),
      sort: f.get("sort"),
    };
    const saved = await mutate(
      "enterprise.view.save",
      "The reusable view was not saved.",
      () =>
        apiRequest<{ id: string }>(`${api}/api/enterprise-requirements/views`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            projectId,
            name: f.get("name"),
            queryJson: JSON.stringify(query),
            columnsJson: JSON.stringify([
              "identifier",
              "statement",
              "level",
              "verification",
              "state",
              "comments",
            ]),
            isShared: f.has("shared"),
          }),
        }),
    );
    if (saved === null) return;
    setMessage("Reusable permission-aware query saved.");
    target.reset();
    await load();
  };
  const share = async (id: string, queryJson: string) => {
    const url = `${location.origin}${viewLink(id, viewDiscipline(queryJson))}`;
    try {
      await navigator.clipboard.writeText(url);
      setMessage(`Stable view link copied: ${url}`);
    } catch (error) {
      recordClientOperationFailure("enterprise.view.copy-link", error);
      setError(
        "This browser blocked clipboard access. The stable link was not copied.",
      );
    }
  };
  const createJob = async (jobType: string) => {
    const key = `${jobType.toLowerCase()}-${projectId}-${new Date().toISOString().slice(0, 16)}`;
    const job = await mutate(
      "enterprise.job.create",
      "The durable job was not accepted.",
      () =>
        apiRequest<{ id: string }>(`${api}/api/enterprise-hardening/jobs`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            projectId,
            jobType,
            requestJson: JSON.stringify({
              scope: "authorized-current-and-history",
            }),
            idempotencyKey: key,
          }),
        }),
    );
    if (job === null) return;
    setMessage("Durable job accepted. Progress will update automatically.");
    await load();
  };
  const jobAction = async (id: string, action: string) => {
    const completed = await mutate(
      `enterprise.job.${action}`,
      `The job could not be ${action === "retry" ? "retried" : "cancelled"}.`,
      () =>
        apiRequest(`${api}/api/enterprise-hardening/jobs/${id}/${action}`, {
          method: "POST",
        }),
    );
    if (completed === null) return;
    setMessage(
      `The job was ${action === "retry" ? "queued for retry" : "cancelled"}.`,
    );
    await load();
  };
  const checkpoint = async () => {
    const value = await mutate(
      "enterprise.integrity.checkpoint",
      "The integrity checkpoint could not be recorded.",
      () =>
        apiRequest<{ state: string; manifestHash: string }>(
          `${api}/api/enterprise-hardening/integrity-checkpoints`,
          {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ projectId }),
          },
        ),
    );
    if (value === null) return;
    setMessage(
      `Integrity checkpoint ${stateLabel(value.state)}: ${value.manifestHash.slice(0, 16)}…`,
    );
    await load();
  };
  const collection = (
    name: string,
    value: number,
    detail: string,
    tone = "",
  ) => (
    <article key={name} className={`enterpriseMetric ${tone}`}>
      <span>{name}</span>
      <b>{value.toLocaleString()}</b>
      <small>{detail}</small>
    </article>
  );
  return (
    <main className="enterprisePage">
      <header>
        <div>
          <button className="back" onClick={onBack}>
            ← Command Center
          </button>
          <p className="eyebrow">
            ADMINISTRATION / CONTROLLED OPERATIONS
          </p>
          <h1>System Operations</h1>
          <p>
            Content integrity, change clarity, reusable intelligence, durable
            processing, controlled configuration, and qualification evidence.
          </p>
        </div>
        <div className="enterpriseSeal">
          <i>◆</i>
          <div>
            <b>CONTROL DEPTH</b>
            <span>8 capabilities · one authority model</span>
          </div>
        </div>
      </header>
      {error && (
        <div className="workspaceError" role="alert" aria-live="assertive">
          {error}
          <button onClick={() => setError("")}>×</button>
        </div>
      )}
      {message && (
        <div className="enterpriseMessage" role="status" aria-live="polite">
          ✓ {message}
          <button onClick={() => setMessage("")}>×</button>
        </div>
      )}
      <nav className="enterpriseTabs">
        {(
          [
            ["command", "Operations"],
            ["content", "Content vault"],
            ["redlines", "Redlines"],
            ["queries", "Query builder"],
            ["jobs", "Job engine"],
            ["configuration", "Product line"],
            ["assurance", "Assurance"],
            ["qualification", "Qualification"],
            ["notifications", "Notifications"],
          ] as [Tab, string][]
        ).map((x) => (
          <button
            className={tab === x[0] ? "active" : ""}
            onClick={() => setTab(x[0])}
            key={x[0]}
          >
            {x[1]}
          </button>
        ))}
      </nav>
      {tab === "configuration" && (
        <section className="enterpriseBody">
          <EnterpriseLifecycleAssurance
            api={api}
            projectId={projectId}
            mode="configuration"
          />
        </section>
      )}
      {tab === "assurance" && (
        <section className="enterpriseBody">
          <EnterpriseLifecycleAssurance
            api={api}
            projectId={projectId}
            mode="assurance"
          />
        </section>
      )}
      {tab === "notifications" && (
        <section className="enterpriseBody">
          <div className="enterpriseHero">
            <div><p>INSTALLATION OPERATIONS / EMAIL OUTBOX</p>
              <h2>{notificationOperations?.smtp.configured ? "SMTP transport configured" : "SMTP transport not configured"}</h2>
              <span>Operational evidence only. Mail bodies and recipient addresses remain protected. SMTP acceptance does not confirm inbox arrival.</span>
            </div>
            <button onClick={queueTransportTest} disabled={busy || !!notificationIntent || !notificationStorageKey || !notificationOperations || (notificationOperations.settings.mode !== "Capture" && notificationOperations.policy.diagnosticTarget === "not-configured")}>Send my transport test</button>
          </div>
          {notificationOperations ? <>
            <p><b>{notificationOperations.installation.label}</b> · database {notificationOperations.installation.database} · version {notificationOperations.installation.version} · mode <b>{notificationOperations.settings.mode}</b> · settings revision {notificationOperations.settings.version} · send authority <b>{notificationOperations.policy.sendAuthority}</b></p>
            {notificationIntent && <section className="jobHero" aria-label="Unconfirmed notification operation">
              <div><h3>Recover the original operation</h3><p>{notificationIntent.family} · {notificationIntent.operationKey}</p>
                <p>The original intent is preserved. Check its receipt before any new action. A retry uses the same identity and settings revision.</p>
                {notificationIntent.requiresCredential && <label>Original SMTP credential for retry<input type="password" autoComplete="new-password" value={notificationCredential} onChange={event => setNotificationCredential(event.target.value)} /></label>}
              </div>
              <button disabled={busy} onClick={() => void runNotificationIntent(notificationIntent, true, false)}>Check original receipt</button>
              <button disabled={busy} onClick={() => void runNotificationIntent(notificationIntent, true, true)}>Retry original operation</button>
              {notificationRejected && notificationNotCommitted && <button disabled={busy} onClick={() => {
                try { localStorage.removeItem(`${notificationStorageKey}:${notificationIntent.operationKey}`); setNotificationIntent(readNotificationIntent(notificationStorageKey)); setNotificationDraft({ ...notificationOperations.settings }); setNotificationCredential(""); setNotificationRejected(false); setNotificationNotCommitted(false); setError(""); }
                catch { setError("The refused intent could not be cleared from recovery storage."); }
              }}>Discard refused operation</button>}
            </section>}
            {notificationReceipt && <section className="activeSessions" aria-label="Notification operation receipt">
              <h3>Operation receipt</h3><p>{notificationReceipt.family} · {stateLabel(notificationReceipt.result.state)} · {new Date(notificationReceipt.createdAt).toLocaleString()}</p>
              <p>Operation {notificationReceipt.operationKey} · receipt {notificationReceipt.id}</p>
              {notificationReceipt.result.generationId && <p>Generation {notificationReceipt.result.generationId}</p>}
              <p>This records the command outcome. Read attempt evidence for transport results; inbox arrival requires separate confirmation.</p>
            </section>}
            <div className="enterpriseMetrics">{notificationOperations.health.map(health => collection(stateLabel(health.state), health.count,
              health.oldestAt ? `Oldest ${new Date(health.oldestAt).toLocaleString()}` : "No outstanding entries",
              health.count && ["ConfigBlocked", "AcceptanceUnknown", "RetryExhausted", "HeldAdmission", "LegacyUnbound", "Failed"].includes(health.state) ? "warn" : ""))}</div>
            <div className="enterpriseGrid">
              <section><div className="sectionTitle"><div><h3>Notification settings</h3><p>Saving settings never activates or readmits queued work. Installation-owned fields are locked.</p></div></div>
                {notificationDraft && <form className="queryBuilder" onSubmit={saveNotificationSettings}>
                  <fieldset disabled={busy || !!notificationIntent || !notificationStorageKey}>
                    {notificationDraft.version !== notificationOperations.settings.version && <p role="status">The saved settings changed. Reload the saved revision before submitting corrections.</p>}
                    <div>
                      <label>Delivery mode<select value={notificationDraft.mode} onChange={event => setNotificationDraft({ ...notificationDraft, mode: event.target.value as NotificationMode })}>
                        {notificationModes.map(mode => <option key={mode} value={mode} disabled={notificationModes.indexOf(mode) > notificationModes.indexOf(notificationOperations.policy.maximumMode) || (notificationOperations.locks.externalModes && ["ControlledTest", "Live"].includes(mode))}>{mode}</option>)}
                      </select></label>
                      <label>SMTP host{notificationOperations.locks.relay ? <span>Installation-owned</span> : <input value={notificationDraft.host} onChange={event => setNotificationDraft({ ...notificationDraft, host: event.target.value })} />}</label>
                      <label>SMTP port{notificationOperations.locks.relay ? <span>Installation-owned</span> : <input type="number" min="1" max="65535" value={notificationDraft.port} onChange={event => setNotificationDraft({ ...notificationDraft, port: Number(event.target.value) })} />}</label>
                      <label>Sender address{notificationOperations.locks.sender ? <span>Installation-owned</span> : <input value={notificationDraft.sender} onChange={event => setNotificationDraft({ ...notificationDraft, sender: event.target.value })} />}</label>
                      <label>Sender display name{notificationOperations.locks.sender ? <span>Installation-owned</span> : <input value={notificationDraft.displayName} onChange={event => setNotificationDraft({ ...notificationDraft, displayName: event.target.value })} />}</label>
                      <label>Mail-link origin{notificationOperations.locks.baseUrl ? <span>Installation-owned</span> : <input value={notificationDraft.baseUrl} onChange={event => setNotificationDraft({ ...notificationDraft, baseUrl: event.target.value })} />}</label>
                      {notificationOperations.locks.credentials ? <p>Credentials: installation-owned</p> : <>
                        <label>SMTP username<input autoComplete="off" disabled={clearNotificationUserName} value={notificationUserName} placeholder={notificationOperations.settings.userNameConfigured ? "Configured; leave blank to keep" : "Not configured"} onChange={event => setNotificationUserName(event.target.value)} /></label>
                        <label><input type="checkbox" checked={clearNotificationUserName} onChange={event => { setClearNotificationUserName(event.target.checked); setNotificationUserName(""); }} /> Clear saved SMTP username</label>
                        <label>New SMTP credential<input type="password" autoComplete="new-password" disabled={clearNotificationCredential} value={notificationCredential} placeholder={notificationOperations.settings.credentialConfigured ? "Configured; leave blank to keep" : "Not configured"} onChange={event => setNotificationCredential(event.target.value)} /></label>
                        <label><input type="checkbox" checked={clearNotificationCredential} onChange={event => { setClearNotificationCredential(event.target.checked); setNotificationCredential(""); }} /> Clear saved SMTP credential</label>
                      </>}
                    </div>
                    <p>STARTTLS and certificate validation are mandatory for external transport. Diagnostic destination: {notificationOperations.policy.diagnosticTarget}. No recipient can be entered here.</p>
                    <button type="submit">Save settings only</button>
                    {" "}<button type="button" onClick={() => { setNotificationDraft({ ...notificationOperations.settings }); setNotificationUserName(""); setClearNotificationUserName(false); setNotificationCredential(""); setClearNotificationCredential(false); }}>Reload saved settings</button>
                  </fieldset>
                </form>}
                <p>Saved mode: <b>{notificationOperations.settings.mode}</b>. Review the saved revision before explicitly activating it.</p>
                <button disabled={busy || !!notificationIntent || !notificationStorageKey || notificationOperations.settings.mode === "Disabled" || !notificationOperations.smtp.configured} onClick={() => notificationCommand("Activate")}>Activate saved mode</button>{" "}
                <button disabled={busy || !!notificationIntent || !notificationStorageKey || notificationOperations.settings.mode === "Disabled" || !notificationOperations.smtp.configured} onClick={() => notificationCommand("Resume")}>Resume current admission</button>
              </section>
              <section><div className="sectionTitle"><div><h3>Queue health and recovery</h3><p>Actions follow server authority and preserved attempt evidence.</p></div></div>
                {notificationOperations.health.map(health => <article className="signalRow" key={health.state}><i className={health.count ? "attention" : "ok"}>{health.count ? "!" : "✓"}</i><div><b>{stateLabel(health.state)} · {health.count}</b><span>{health.action}</span></div></article>)}
                <p>AcceptanceUnknown requires reconciliation and trusted worker quiescence. Lease expiry alone does not prove a stopped SMTP connection.</p>
                <label><input type="checkbox" checked={duplicateRiskAcknowledged} onChange={event => setDuplicateRiskAcknowledged(event.target.checked)} /> I acknowledge that replaying unknown acceptance may produce a duplicate email.</label>
                <p>Quiescence is established by the server; it cannot be declared by this checkbox.</p>
                <p>Historical Sent entries are SMTP submission evidence. Credentials and message content are never read back.</p>
              </section>
            </div>
            {notificationOperations.deliveries.some(delivery => delivery.state === "HeldAdmission" || delivery.state === "LegacyUnbound") && <section className="activeSessions" aria-label="Held notification deliveries">
              <h3>Held delivery backlog</h3><p>Each held delivery requires explicit selection. Ambiguous historical notices remain held.</p>
              {notificationOperations.deliveries.filter(delivery => delivery.state === "HeldAdmission" || delivery.state === "LegacyUnbound").map(delivery => <div className="signalRow" key={delivery.id}><div>
                <b>{stateLabel(delivery.state)} · delivery {delivery.id}</b><span>{delivery.recipient} · {delivery.address} · {delivery.detail}</span>
                {delivery.state === "HeldAdmission" && <button disabled={busy || !!notificationIntent || !notificationStorageKey} onClick={() => newNotificationIntent({ family: "Readmit", deliveryId: delivery.id })}>Readmit selected held delivery</button>}
              </div></div>)}
            </section>}
            <section className="activeSessions" aria-label="Notification generation history">
              <div className="sectionTitle"><div><h3>Recent delivery state</h3><p>Server page {notificationOperations.page} · {notificationOperations.total} generations · at most {notificationOperations.pageSize} per page</p></div></div>
              {notificationOperations.generations.length ? notificationOperations.generations.map(generation => <div key={generation.id}>
                <div className="signalRow"><i className={["SmtpAccepted", "Captured", "TestAccepted"].includes(generation.state) ? "ok" : "attention"}>{["SmtpAccepted", "Captured", "TestAccepted"].includes(generation.state) ? "✓" : "!"}</i>
                  <div><b>{stateLabel(generation.state)} · {generation.contextIdentifier}</b>
                    <span>{generation.mode} · intended destination {generation.intendedDestination} · effective destination {generation.effectiveDestination} · {generation.attempts} attempts{generation.safeCode ? ` · ${generation.safeCode}` : ""}</span>
                    <span>Generation {generation.id} · version {generation.version} · created {new Date(generation.createdAt).toLocaleString()}</span>
                    {generation.dueAt && <span>Due {new Date(generation.dueAt).toLocaleString()}{generation.deadlineAt ? ` · deadline ${new Date(generation.deadlineAt).toLocaleString()}` : ""}</span>}
                    <details><summary>Message identity</summary><p style={{ overflowWrap: "anywhere" }}>Message-ID {generation.messageId}<br />Body hash {generation.bodyHash}</p></details>
                    <div>
                      <button disabled={busy} onClick={() => void loadNotificationAttempts(generation.id)}>View attempts</button>{" "}
                      {["RetryExhausted", "ConfigBlocked", "HeldAdmission"].includes(generation.state) && <button disabled={busy || !!notificationIntent || !notificationStorageKey} onClick={() => notificationCommand("Readmit", generation)}>Readmit generation</button>}{" "}
                      {generation.state === "AcceptanceUnknown" && <button disabled={busy || !!notificationIntent || !notificationStorageKey || !duplicateRiskAcknowledged} onClick={() => notificationCommand("Replay", generation)}>Request replay after quiescence</button>}{" "}
                      {["SmtpAccepted", "Captured", "TestAccepted", "Suppressed", "PermanentFailed"].includes(generation.state) && <button disabled={busy || !!notificationIntent || !notificationStorageKey} onClick={() => notificationCommand("Reissue", generation)}>Request new generation</button>}{" "}
                      {!["SmtpAccepted", "Captured", "TestAccepted", "Suppressed"].includes(generation.state) && <button disabled={busy || !!notificationIntent || !notificationStorageKey} onClick={() => notificationCommand("Suppress", generation)}>Suppress generation</button>}
                    </div>
                  </div>
                </div>
                {notificationAttempts?.generationId === generation.id && <section aria-label={`Attempts for ${generation.contextIdentifier}`}>
                  {notificationAttempts.attempts.length ? notificationAttempts.attempts.map(attempt => <div className="signalRow" key={attempt.id}><div><b>{attempt.outcome} · {attempt.phase}</b><span>{new Date(attempt.startedAt).toLocaleString()} · SMTP {attempt.smtpStatus ?? "no status"} · {attempt.safeCode || "no failure code"} · transport {attempt.transportDisposed ? "disposed" : "not confirmed disposed"} · quiescence {attempt.quiescence}</span></div></div>) : <p>No physical attempts recorded.</p>}
                </section>}
              </div>) : <div className="emptyEnterprise"><b>No email generations yet</b><p>A transport test creates a diagnostic intent under the saved installation policy.</p></div>}
              <p><button disabled={busy || notificationOperations.page <= 1} onClick={() => { setNotificationAttempts(undefined); setNotificationPage(notificationOperations.page - 1); }}>Previous page</button>{" "}
                <button disabled={busy || notificationOperations.page * notificationOperations.pageSize >= notificationOperations.total} onClick={() => { setNotificationAttempts(undefined); setNotificationPage(notificationOperations.page + 1); }}>Next page</button></p>
            </section>
          </> : <div className="emptyEnterprise"><b>Loading notification operations</b><p>Refresh if configuration evidence is unavailable.</p></div>}
        </section>
      )}
      {tab === "command" && overview && (
        <section className="enterpriseBody">
          <div className="enterpriseHero">
            <div>
              <p>Repository assurance posture</p>
              <h2>
                {overview.repository.missingFiles === 0 &&
                overview.conflicts.length === 0
                  ? "Controlled and observable"
                  : "Attention required"}
              </h2>
              <span>
                Generated {new Date(overview.generatedAt).toLocaleTimeString()}{" "}
                · permission-scoped live evidence
              </span>
            </div>
            <b>
              {overview.checkpoint?.state || "NOT CHECKED"}
              <small>Latest integrity checkpoint</small>
            </b>
          </div>
          <div className="enterpriseMetrics">
            {collection(
              "Requirements",
              overview.repository.artifacts,
              "stable artifact identities",
            )}
            {collection(
              "Immutable revisions",
              overview.repository.revisions,
              "complete controlled history",
            )}
            {collection(
              "Controlled files",
              overview.repository.attachments,
              `${overview.repository.attachmentVersions} retained versions`,
            )}
            {collection(
              "Durable jobs",
              overview.jobs.length,
              `${overview.jobs.filter((x) => x.state === "Failed").length} failed`,
            )}
            {collection(
              "Active editors",
              overview.sessions.length,
              "live edit sessions",
            )}
            {collection(
              "Open conflicts",
              overview.conflicts.length,
              "no silent overwrites",
              overview.conflicts.length ? "warn" : "",
            )}
          </div>
          <div className="enterpriseGrid">
            <section>
              <div className="sectionTitle">
                <div>
                  <h3>Operational signals</h3>
                  <p>Exceptions that can weaken assurance</p>
                </div>
                <i>LIVE</i>
              </div>
              {[
                [
                  "Attachment storage",
                  overview.repository.missingFiles === 0,
                  `${overview.repository.missingFiles} missing objects`,
                ],
                [
                  "Background processing",
                  !overview.jobs.some((x) => x.state === "Failed"),
                  `${overview.jobs.filter((x) => x.state === "Failed").length} failed jobs`,
                ],
                [
                  "Concurrent authoring",
                  overview.conflicts.length === 0,
                  `${overview.conflicts.length} unresolved conflicts`,
                ],
                [
                  "Performance qualification",
                  !!performance?.allPassed,
                  `${performance?.samples.length || 0} measured gates`,
                ],
              ].map((x) => (
                <article className="signalRow" key={String(x[0])}>
                  <i className={x[1] ? "ok" : "attention"}>
                    {x[1] ? "✓" : "!"}
                  </i>
                  <div>
                    <b>{x[0]}</b>
                    <span>{x[2]}</span>
                  </div>
                </article>
              ))}
            </section>
            <section>
              <div className="sectionTitle">
                <div>
                  <h3>Latest checkpoint</h3>
                  <p>Auditable repository-state manifest</p>
                </div>
              </div>
              {overview.checkpoint ? (
                <div className="checkpoint">
                  <i>{overview.checkpoint.state === "Healthy" ? "✓" : "!"}</i>
                  <h3>{overview.checkpoint.state}</h3>
                  <p>{overview.checkpoint.detail}</p>
                  <code>{overview.checkpoint.manifestHash}</code>
                  <small>
                    {overview.checkpoint.createdBy} ·{" "}
                    {new Date(overview.checkpoint.createdAt).toLocaleString()}
                  </small>
                </div>
              ) : (
                <div className="emptyEnterprise">
                  <b>No checkpoint yet</b>
                  <p>Run the first integrity checkpoint from Qualification.</p>
                </div>
              )}
            </section>
          </div>
        </section>
      )}
      {tab === "content" && (
        <section className="enterpriseBody">
          <div className="artifactPicker">
            <div>
              <p className="eyebrow">CONTROLLED ARTIFACT</p>
              <h2>Attachment & evidence vault</h2>
            </div>
            <select
              value={selectedId}
              onChange={(e) => setSelectedId(e.target.value)}
            >
              {requirements.map((x) => (
                <option value={x.id} key={x.id}>
                  {x.displayNumber} · {x.statement.slice(0, 70)}
                </option>
              ))}
            </select>
          </div>
          <div className="vaultLayout">
            <form className="vaultUpload" onSubmit={upload}>
              <span>⬆</span>
              <h3>Store controlled content</h3>
              <p>
                Every upload is streamed to protected local storage, hashed,
                attributed, and attached to an exact requirement revision.
              </p>
              <label>
                Document label
                <input
                  name="label"
                  placeholder="Interface control diagram"
                  required
                />
              </label>
              <label>
                Description
                <textarea
                  name="description"
                  placeholder="Purpose, source, and applicability"
                />
              </label>
              <label className="fileDrop">
                Select file
                <input type="file" name="file" required />
              </label>
              <button disabled={busy}>
                {busy ? "Checksumming…" : "Upload controlled version"}
              </button>
            </form>
            <section className="vaultFiles">
              <div className="sectionTitle">
                <div>
                  <h3>{selected?.displayNumber || "Requirement"} files</h3>
                  <p>Active and superseded versions remain retrievable</p>
                </div>
                <b>{attachments.length}</b>
              </div>
              {attachments.length ? (
                attachments.map((x) => (
                  <article className={x.state.toLowerCase()} key={x.id}>
                    <div className="fileIcon">
                      {x.originalFileName.split(".").at(-1)?.toUpperCase()}
                    </div>
                    <div>
                      <b>
                        {x.label} <i>v{x.version}</i>
                      </b>
                      <p>
                        {x.originalFileName} · {(x.size / 1024).toFixed(1)} KB
                      </p>
                      <code>SHA-256 {x.sha256.slice(0, 24)}…</code>
                      <small>
                        {stateLabel(x.state)} ·{" "}
                        <PersonName userName={x.uploadedBy} /> ·{" "}
                        {new Date(x.uploadedAt).toLocaleDateString()}
                      </small>
                    </div>
                    <div className="fileActions">
                      <a
                        href={`${api}/api/enterprise-hardening/attachments/${x.id}/download`}
                      >
                        Download
                      </a>
                      <button onClick={() => verify(x.id)}>
                        {x.integrityVerifiedAt
                          ? "Verified ✓"
                          : "Verify integrity"}
                      </button>
                      {x.state === "Active" &&
                        (!x.revisionId || x.revisionId === selected?.revisionId ? (
                          <label>
                            New version
                            <input
                              type="file"
                              onChange={(e) => {
                                const f = e.target.files?.[0];
                                if (f) versionFile(x, f);
                              }}
                            />
                          </label>
                        ) : (
                          <small>This file is bound to an earlier revision of the requirement.</small>
                        ))}
                    </div>
                  </article>
                ))
              ) : (
                <div className="emptyEnterprise">
                  <b>No controlled files</b>
                  <p>
                    Add the first diagram, rationale, supplier source, or
                    verification artifact.
                  </p>
                </div>
              )}
            </section>
          </div>
        </section>
      )}
      {tab === "redlines" && (
        <section className="enterpriseBody">
          <div className="artifactPicker">
            <div>
              <p className="eyebrow">EXACT REVISION COMPARISON</p>
              <h2>Visual change intelligence</h2>
            </div>
            <select
              value={selectedId}
              onChange={(e) => setSelectedId(e.target.value)}
            >
              {requirements.map((x) => (
                <option value={x.id} key={x.id}>
                  {x.displayNumber}
                </option>
              ))}
            </select>
          </div>
          {redline ? (
            <div className="redlineBoard">
              <header>
                <div>
                  <span>FROM</span>
                  <b>
                    {selected?.displayNumber.split(".")[0]}.
                    {String(redline.from).padStart(2, "0")}
                  </b>
                </div>
                <i>→</i>
                <div>
                  <span>TO</span>
                  <b>
                    {selected?.displayNumber.split(".")[0]}.
                    {String(redline.to).padStart(2, "0")}
                  </b>
                </div>
              </header>
              <section>
                <h3>Requirement statement</h3>
                <p>
                  {redline.statement.map((x, i) => (
                    <span className={x.kind} key={i}>
                      {x.text}{" "}
                    </span>
                  ))}
                </p>
              </section>
              <section>
                <h3>Rationale</h3>
                <p>
                  {redline.rationale.map((x, i) => (
                    <span className={x.kind} key={i}>
                      {x.text}{" "}
                    </span>
                  ))}
                </p>
              </section>
              <footer>
                <div>
                  <span>Verification method</span>
                  <b>
                    {redline.verificationChanged ? (
                      <>
                        <del>{redline.fromVerification}</del> →{" "}
                        <ins>{redline.toVerification}</ins>
                      </>
                    ) : (
                      redline.toVerification
                    )}
                  </b>
                </div>
                <div>
                  <span>Attachment versions</span>
                  <b>
                    {attachments.filter((x) => x.state === "Active").length}{" "}
                    current ·{" "}
                    {attachments.filter((x) => x.state !== "Active").length}{" "}
                    historical
                  </b>
                </div>
                <div>
                  <span>Revision history</span>
                  <b>{history.length} immutable snapshots</b>
                </div>
              </footer>
            </div>
          ) : (
            <div className="emptyEnterprise wide">
              <b>No earlier revision to compare</b>
              <p>
                Select a requirement with at least two controlled revisions.
              </p>
            </div>
          )}
        </section>
      )}
      {tab === "queries" && (
        <section className="enterpriseBody">
          <div className="queryLayout">
            <form className="queryBuilder" onSubmit={saveView}>
              <p className="eyebrow">STRUCTURED QUERY</p>
              <h2>Build an engineering worklist</h2>
              <p>
                Combine artifact fields, lifecycle state, ownership, and
                collaboration signals. Saved views always reapply current
                permissions.
              </p>
              <div>
                <label>
                  Name
                  <input
                    name="name"
                    placeholder="Current release verification gaps"
                    required
                  />
                </label>
                <label>
                  Contains
                  <input
                    name="search"
                    placeholder="Requirement text or identifier"
                  />
                </label>
                <label>
                  Level
                  <select name="level">
                    <option value="">Any level</option>
                    <option>System</option>
                    <option value="HighLevel">HLR</option>
                    <option value="LowLevel">LLR</option>
                  </select>
                </label>
                <label>
                  Verification
                  <select name="verification">
                    <option value="">Any method</option>
                    <option>Test</option>
                    <option>Analysis</option>
                    <option>Inspection</option>
                    <option>Demonstration</option>
                  </select>
                </label>
                <label>
                  Lifecycle state
                  <select name="state">
                    <option value="">Any state</option>
                    <option>Active</option>
                    <option>Superseded</option>
                    <option>Retired</option>
                  </select>
                </label>
                <label>
                  Owner
                  <input name="owner" placeholder="Account username" />
                </label>
                <label>
                  Sort
                  <select name="sort">
                    <option value="identifier">Identifier</option>
                    <option value="updated">Recently revised</option>
                    <option value="verification">Verification</option>
                    <option value="state">Lifecycle state</option>
                  </select>
                </label>
              </div>
              <label className="check">
                <input type="checkbox" name="openComments" /> Only requirements
                with open discussions
              </label>
              <label className="check">
                <input type="checkbox" name="shared" /> Share with authorized
                Program members
              </label>
              <button>Save permission-aware view</button>
            </form>
            <section className="savedQueryList">
              <div className="sectionTitle">
                <div>
                  <h3>Reusable views</h3>
                  <p>Personal and Program worklists</p>
                </div>
                <b>{overview?.views.length || 0}</b>
              </div>
              {overview?.views.map((x) => (
                <article key={x.id}>
                  <i>{x.isShared ? "◉" : "○"}</i>
                  <div>
                    <b>{x.name}</b>
                    <p>
                      {Object.entries(
                        safeParse<Record<string, unknown>>(x.queryJson, {}),
                      )
                        .filter(([, v]) => v)
                        .map(([k, v]) => `${k}: ${v}`)
                        .join(" · ") || "All authorized requirements"}
                    </p>
                    <small>
                      {x.isShared ? "Shared Program view" : "Personal view"} ·{" "}
                      {safeParse<unknown[]>(x.columnsJson, []).length} columns
                    </small>
                  </div>
                  <button onClick={() => share(x.id, x.queryJson)}>
                    Copy stable link
                  </button>
                </article>
              ))}
            </section>
          </div>
        </section>
      )}
      {tab === "jobs" && (
        <section className="enterpriseBody">
          <div className="jobHero">
            <div>
              <p className="eyebrow">RESUMABLE BACKGROUND PROCESSING</p>
              <h2>Large work never blocks the engineer</h2>
              <p>
                Idempotent operations retain request, actor, attempts, progress,
                outcome, and errors.
              </p>
            </div>
            <button
              disabled={busy}
              onClick={() => createJob("RepositoryExport")}
            >
              Generate controlled export
            </button>
          </div>
          <section className="jobTable">
            <div className="jobHead">
              <span>OPERATION</span>
              <span>PROGRESS</span>
              <span>ATTEMPT</span>
              <span>OUTCOME</span>
              <span>ACTIONS</span>
            </div>
            {overview?.jobs.map((x) => (
              <article key={x.id}>
                <div>
                  <b>{x.jobType === "BackgroundIntegrityScan" ? "Legacy health snapshot" : x.jobType.replace("Background", "")}</b>
                  <small>
                    <PersonName userName={x.createdBy} /> ·{" "}
                    {new Date(x.createdAt).toLocaleString()}
                  </small>
                  <code>{x.idempotencyKey}</code>
                </div>
                <div>
                  <span>{x.progressPercent}%</span>
                  <i>
                    <b style={{ width: `${x.progressPercent}%` }} />
                  </i>
                </div>
                <strong>
                  {x.attempt}
                  <small>of {x.maximumAttempts}</small>
                </strong>
                <em className={x.state.toLowerCase()}>
                  {jobStateLabel(x)}
                  {x.state === "Running" && x.claimedBy ? (
                    <small>{x.claimedBy}</small>
                  ) : null}
                  {x.errorHistory && x.errorHistory.length > 1 ? (
                    <small
                      title={x.errorHistory
                        .map((h) => "Attempt " + h.attempt + ": " + h.error)
                        .join("\n")}
                    >
                      {x.errorHistory.length} recorded failures
                    </small>
                  ) : null}
                </em>
                <div>
                  {x.state === "Failed" && (
                    <button onClick={() => jobAction(x.id, "retry")}>
                      Retry
                    </button>
                  )}
                  {["Preview", "Running"].includes(x.state) && (
                    <button onClick={() => jobAction(x.id, "cancel")}>
                      Cancel
                    </button>
                  )}
                  {x.state === "Completed" && exportExpired(x) ? (
                    <small>Export expired. Generate a new export.</small>
                  ) : x.state === "Completed" && x.jobType.includes("Export") ? (
                    <a
                      href={`${api}/api/enterprise-hardening/jobs/${x.id}/download`}
                    >
                      Download
                    </a>
                  ) : x.state === "Completed" ? (
                    <small>{x.succeededCount.toLocaleString()} items</small>
                  ) : null}
                </div>
              </article>
            ))}
          </section>
        </section>
      )}
      {tab === "qualification" && (
        <section className="enterpriseBody">
          <div className="qualificationHero">
            <div>
              <p className="eyebrow">PUBLISHED ENGINEERING EVIDENCE</p>
              <h2>Scale, integrity and operational evidence</h2>
              <p>
                Local measurements are retained as engineering evidence—not a
                production guarantee, and not a tool-qualification or
                certification claim.
              </p>
            </div>
            <button disabled={busy} onClick={checkpoint}>
              Run integrity checkpoint
            </button>
          </div>
          <div className="qualificationGrid">
            <section>
              <h3>Live performance gates</h3>
              {performance?.samples.map((x) => (
                <article key={x.name}>
                  <i className={x.passed ? "ok" : "attention"}>
                    {x.passed ? "✓" : "!"}
                  </i>
                  <div>
                    <b>{x.name.replaceAll("_", " ")}</b>
                    <span>p95 {x.p95Ms} ms</span>
                  </div>
                  <small>target ≤ {x.targetMs} ms</small>
                </article>
              ))}
            </section>
            <section>
              <h3>Qualification ladder</h3>
              {[
                [
                  "10,000 requirements",
                  "Completed",
                  "Persistence and query qualification",
                ],
                [
                  "50,000 requirements",
                  "Ready",
                  "Materialized medium-profile gate",
                ],
                [
                  "Concurrent browsers",
                  "Next run",
                  "Mixed search, authoring, job, and dashboard load",
                ],
                [
                  "150 users",
                  "Production target",
                  "Requires production-like infrastructure",
                ],
              ].map((x, i) => (
                <article key={x[0]}>
                  <i className={i === 0 ? "ok" : i === 1 ? "ready" : ""}>
                    {i === 0 ? "✓" : i + 1}
                  </i>
                  <div>
                    <b>{x[0]}</b>
                    <span>{x[2]}</span>
                  </div>
                  <small>{x[1]}</small>
                </article>
              ))}
            </section>
            <section className="integrityEvidence">
              <h3>Latest integrity evidence</h3>
              {overview?.checkpoint ? (
                <>
                  <strong className={overview.checkpoint.state.toLowerCase()}>
                    {overview.checkpoint.state}
                  </strong>
                  <code>{overview.checkpoint.manifestHash}</code>
                  <p>{overview.checkpoint.detail}</p>
                  <small>
                    {overview.checkpoint.createdBy} ·{" "}
                    {new Date(overview.checkpoint.createdAt).toLocaleString()}
                  </small>
                </>
              ) : (
                <p>Run the first checkpoint to capture a manifest.</p>
              )}
            </section>
          </div>
        </section>
      )}
    </main>
  );
}
