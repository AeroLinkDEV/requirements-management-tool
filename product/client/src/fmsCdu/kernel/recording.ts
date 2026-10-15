import { ContentStore, sha256, type ContentRef, type StoredContent } from "./contentStore";
import type { KernelAction } from "./actions";
import type { KernelRecord, PendingSubmission } from "./kernel";
import { createSession, type InitialObservations, type RecordingSession, type SessionSetup } from "./session";
import { memoryUserDatabaseStore, type UserScope } from "../userDatabase";
import { FRAME_UNITS } from "./time";

type Asset = { readonly path: string; readonly sha256: string; readonly bytes: number };
type BuildIdentity = { readonly schema: "aerolink.fms-build.v1"; readonly kind: "built" | "development";
  readonly commit: string | null; readonly dirty: boolean | null; readonly assets: readonly Asset[]; readonly bundleHash: string | null };
export type RecordingIdentity = {
  readonly build: BuildIdentity | null;
  readonly engine: { readonly userAgent: string; readonly platform: string; readonly fullVersionList: unknown; readonly architecture: unknown; readonly platformVersion: unknown };
  readonly qualified: boolean;
  readonly reason: string | null;
};
export type Reproduction = KernelRecord & {
  readonly schema: "aerolink.fms-recording.v1";
  readonly initial: { readonly version: 1; readonly setup: SessionSetup; readonly observations: InitialObservations;
    readonly userScope: UserScope | null; readonly users: readonly { readonly scope: UserScope; readonly content: ContentRef | null }[] };
  readonly identity: RecordingIdentity;
  readonly builtins: readonly { readonly bundleHash: string; readonly path: string; readonly sha256: string; readonly bytes: number }[];
  readonly contents: readonly StoredContent[];
  readonly digests: readonly string[];
  readonly lastBoundary: number;
  readonly capture: { readonly kind: "boundary" | "halted-open-action" | "faulted"; readonly exact: boolean; readonly label: string };
  /** I2 owns full hidden state. An empty slot is explicitly not a restorable snapshot. */
  readonly snapshots: readonly { readonly atFrame: number; readonly ref: ContentRef }[];
  /** Actual native inputs received before S_k, still unresolved/unexecuted. Null on an open-ACTION export. */
  readonly pendingInputs: readonly { readonly submissionSeq: number; readonly action: unknown; readonly source: PendingSubmission["source"] }[] | null;
};

/** Actual served emitted bytes, verified off the simulation path. Development never impersonates a built bundle. */
export async function currentRecordingIdentity(): Promise<RecordingIdentity> {
  const ua = navigator as Navigator & { userAgentData?: { getHighEntropyValues(keys: string[]): Promise<Record<string, unknown>> } };
  let high: Record<string, unknown> | null = null;
  try { high = await ua.userAgentData?.getHighEntropyValues(["fullVersionList", "architecture", "platformVersion"]) ?? null; }
  catch { /* Missing permission/version fields are disclosed as unknown, never invented. */ }
  const engine = { userAgent: navigator.userAgent, platform: navigator.platform,
    fullVersionList: high?.fullVersionList ?? null, architecture: high?.architecture ?? null, platformVersion: high?.platformVersion ?? null };
  let build: BuildIdentity | null = null;
  try {
    const response = await fetch(new URL("/fms-recording-build.json", globalThis.location.href), { cache: "no-store" });
    if (!response.ok) throw new Error("missing build identity");
    build = await response.json() as BuildIdentity;
    if (build.schema !== "aerolink.fms-build.v1") throw new Error("unsupported build identity");
    if (build.kind !== "built") return { build, engine, qualified: false, reason: "development build: unqualified" };
    // A build made outside a git checkout records no commit; it still runs, but it cannot qualify a recording.
    if (typeof build.commit !== "string" || !build.commit) return { build, engine, qualified: false, reason: "build commit unknown" };
    const { schema, kind, commit, dirty, assets, bundleHash } = build;
    if (!Array.isArray(assets) || !assets.length || !/^[0-9a-f]{64}$/.test(bundleHash ?? "")) throw new Error("invalid build identity");
    if (await sha256(new TextEncoder().encode(JSON.stringify({ schema, kind, commit, dirty, assets }))) !== bundleHash) throw new Error("corrupt build identity");
    const currentModule = new URL(import.meta.url).pathname.replace(/^\//, "");
    if (!assets.some(asset => asset.path === currentModule)) throw new Error("executing recording module is not in the build identity");
    let previous = "";
    for (const asset of assets) {
      if (!asset.path || asset.path <= previous || asset.path.startsWith("/") || asset.path.split("/").includes("..")) throw new Error("invalid build asset path");
      previous = asset.path;
      const item = await fetch(new URL(`/${asset.path}`, globalThis.location.href), { cache: "no-store" });
      if (!item.ok) throw new Error(`missing build asset ${asset.path}`);
      const bytes = new Uint8Array(await item.arrayBuffer());
      if (bytes.length !== asset.bytes || await sha256(bytes) !== asset.sha256) throw new Error(`corrupt build asset ${asset.path}`);
    }
    if (!Array.isArray(engine.fullVersionList) || !engine.fullVersionList.length) return { build, engine, qualified: false, reason: "full engine version unavailable" };
    return { build, engine, qualified: true, reason: null };
  } catch (error) { return { build, engine, qualified: false, reason: error instanceof Error ? error.message : String(error) }; }
}

let pageIdentity: Promise<RecordingIdentity> | null = null;
/**
 * This page's identity, resolved on the first export that needs it. Session construction, Restart and profile or
 * scenario changes do no fetch or hashing. Only a definitive answer is kept for the page (a verified built identity,
 * or a well-formed development manifest): the served bytes cannot change under a loaded page. Any other answer (a
 * failed fetch, an HTTP error, a missing asset, a mismatch) is returned but not kept, so the next export asks again
 * and one transient failure does not leave every later export unqualified.
 */
export function pageRecordingIdentity(): Promise<RecordingIdentity> {
  if (pageIdentity) return pageIdentity;
  const attempt: Promise<RecordingIdentity> = currentRecordingIdentity().then(identity => {
    if (!identity.qualified && identity.build?.kind !== "development" && pageIdentity === attempt) pageIdentity = null;
    return identity;
  });
  pageIdentity = attempt;
  return attempt;
}

type ContentPayload = { readonly kind: string; readonly unit: number; readonly op?: string; readonly name?: string; readonly airports?: readonly string[]; readonly content: ContentRef };
const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);

type SpecialNumber = { readonly path: readonly string[]; readonly value: "-0" | "NaN" | "+Infinity" | "-Infinity" };
/** JSON alone loses -0 and non-finite input values. Paths bind these bits without a magic object-value collision. */
export function serializeRecording(record: Reproduction): string {
  const numbers: SpecialNumber[] = [];
  const visit = (value: unknown, path: string[]): unknown => {
    if (typeof value === "number" && (Object.is(value, -0) || !Number.isFinite(value))) {
      numbers.push({ path, value: Object.is(value, -0) ? "-0" : Number.isNaN(value) ? "NaN" : value > 0 ? "+Infinity" : "-Infinity" });
      return null;
    }
    if (Array.isArray(value)) return value.map((item, index) => visit(item, [...path, String(index)]));
    if (value && typeof value === "object") return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, visit(item, [...path, key])]));
    return value;
  };
  return JSON.stringify({ schema: "aerolink.fms-recording-file.v1", recording: visit(record, []), numbers });
}

export function parseRecording(text: string): Reproduction {
  const file = JSON.parse(text) as { schema?: string; recording?: Reproduction; numbers?: SpecialNumber[] };
  if (file.schema !== "aerolink.fms-recording-file.v1" || !file.recording || !Array.isArray(file.numbers)) throw new Error("unsupported recording file");
  const seen = new Set<string>();
  for (const entry of file.numbers) {
    if (!Array.isArray(entry.path) || !entry.path.length || entry.path.some(key => typeof key !== "string" || ["__proto__", "prototype", "constructor"].includes(key))) throw new Error("invalid numeric input path");
    const id = JSON.stringify(entry.path);
    if (seen.has(id)) throw new Error("duplicate numeric input path");
    seen.add(id);
    let parent: Record<string, unknown> = file.recording as unknown as Record<string, unknown>;
    for (const key of entry.path.slice(0, -1)) {
      if (!parent || typeof parent !== "object" || !Object.hasOwn(parent, key)) throw new Error("missing numeric input path");
      parent = parent[key] as Record<string, unknown>;
    }
    const key = entry.path.at(-1)!;
    if (!parent || !Object.hasOwn(parent, key) || parent[key] !== null) throw new Error("invalid numeric input placeholder");
    switch (entry.value) {
      case "-0": parent[key] = -0; break;
      case "NaN": parent[key] = NaN; break;
      case "+Infinity": parent[key] = Infinity; break;
      case "-Infinity": parent[key] = -Infinity; break;
      default: throw new Error("invalid numeric input encoding");
    }
  }
  return file.recording;
}

/** Records exact import bytes beside their receipt without changing synchronous input execution or its outcome. */
export class SessionRecording {
  private readonly store = new ContentStore();
  private readonly imports = new Map<number, Uint8Array>();
  private readonly identity: () => Promise<RecordingIdentity>;
  private readonly session: RecordingSession;
  private readonly cancellations = new Set<() => void>();
  private generation = 0;
  /**
   * `identity` is asked for each time an export is encoded, never at session construction. The bench passes
   * pageRecordingIdentity, which keeps a definitive answer for the page and retries anything else.
   */
  constructor(session: RecordingSession, identity: () => Promise<RecordingIdentity>) { this.session = session; this.identity = identity; }

  retainImport(submissionSeq: number, bytes: Uint8Array) { this.imports.set(submissionSeq, new Uint8Array(bytes)); }
  dispose() { this.generation += 1; for (const cancel of this.cancellations) cancel(); this.cancellations.clear(); }

  /**
   * The callback observes S_k synchronously and returns; digest text, hashing and JSON IO run later, off the frame path.
   * Only a halt or a fault passes no S_k, so only they export at once (labelled). A finished run's bench still passes S_k.
   */
  requestExport(): Promise<Reproduction> {
    const kernel = this.session.kernel;
    const generation = this.generation;
    if (kernel.halted || kernel.faulted) return this.encode(kernel.record(), kernel.lastSuccessfulBoundary, kernel.faulted ? "faulted" : "halted-open-action", generation, null);
    return new Promise((resolve, reject) => {
      const cancel = () => { unsubscribe(); reject(new Error("recording session ended")); };
      const unsubscribe = kernel.onNextBoundary(() => {
        this.cancellations.delete(cancel);
        try {
          const record = kernel.boundaryRecord();
          const pending = kernel.boundaryPendingInputs();
          // Defer even the first asynchronous hashing call until the enclosing advance() has finished its frames.
          queueMicrotask(() => { void this.encode(record, record.frame, "boundary", generation, pending).then(resolve, reject); });
        } catch (error) { reject(error); }
      });
      this.cancellations.add(cancel);
    });
  }

  private async encode(record: KernelRecord, lastBoundary: number, kind: Reproduction["capture"]["kind"], generation: number,
    pending: readonly PendingSubmission[] | null): Promise<Reproduction> {
    const initial = this.session;
    // The kernel's digest log is append-only: D_0..D_(lastBoundary-1) read now equal what they were at the boundary.
    const digests = initial.kernel.frameDigestsTo(lastBoundary);
    const users = await Promise.all(initial.users.map(async user => ({ scope: user.scope,
      content: user.text === null ? null : await this.store.putText(user.text) })));
    const encodeAction = async (action: KernelAction, submissionSeq: number): Promise<unknown> => {
      if (action.kind === "config.userdb" || (action.kind === "config.navdb" && action.op === "loadArinc424")
        || (action.kind === "config.magvar" && action.op === "load")) {
        const bytes = this.imports.get(submissionSeq);
        if (!bytes) throw new Error(`exact import bytes unavailable at submission ${submissionSeq}`);
        const content = await this.store.put(bytes);
        return action.kind === "config.navdb" ? { kind: action.kind, unit: action.unit, op: action.op, name: action.name, airports: action.airports, content }
          : action.kind === "config.magvar" ? { kind: action.kind, unit: action.unit, op: action.op, content } : { kind: action.kind, unit: action.unit, content };
      }
      return action;
    };
    const journal = await Promise.all(record.journal.map(async entry => ({ ...entry, payload: await encodeAction(entry.payload as KernelAction, entry.submissionSeq) })));
    const pendingInputs = pending ? await Promise.all(pending.map(async entry => ({ ...entry, action: await encodeAction(entry.action, entry.submissionSeq) }))) : null;
    const identity = await this.identity();
    if (generation !== this.generation) throw new Error("recording session ended");
    const boundary = kind === "boundary";
    return { ...record, journal, schema: "aerolink.fms-recording.v1", initial: { version: 1, setup: initial.setup,
      observations: initial.initial, userScope: initial.persistentUserScope, users }, identity,
      builtins: identity.build?.bundleHash ? identity.build.assets.map(asset => ({ bundleHash: identity.build!.bundleHash!, ...asset })) : [],
      contents: this.store.export(), digests, lastBoundary,
      capture: { kind, exact: boundary, label: boundary ? "S_k; empty snapshot slot (I2 required for restore)" : "Open ACTION export; only the prior closed prefix can be verified, not exact current state" }, snapshots: [], pendingInputs };
  }
}

async function decodeAction(value: unknown, store: ContentStore): Promise<unknown> {
    const payload = value as ContentPayload;
    if (!payload || typeof payload !== "object" || !("content" in payload)) return value;
    // Match the actual File.text/TextDecoder import path, including replacement of malformed UTF-8; retain raw bytes.
    const text = new TextDecoder().decode(await store.resolve(payload.content));
    let action: KernelAction;
    if (payload.kind === "config.userdb") action = { kind: "config.userdb", unit: payload.unit as 1 | 2, text };
    else if (payload.kind === "config.navdb" && payload.op === "loadArinc424") action = { kind: "config.navdb", unit: payload.unit as 1 | 2, op: "loadArinc424", name: payload.name!, airports: [...payload.airports!], text };
    else if (payload.kind === "config.magvar" && payload.op === "load") action = { kind: "config.magvar", unit: payload.unit as 1 | 2, op: "load", package: JSON.parse(text) };
    else throw new Error("unsupported content action");
    return action;
}

export type VerificationResult = { readonly status: "verified"; readonly frame: number; readonly exact: boolean };
/** Fresh memory-only reproduction. The worker's own actual identity is required, never the recording's assertion. */
export async function verifyRecording(record: Reproduction, current: RecordingIdentity): Promise<VerificationResult> {
  if (record.schema !== "aerolink.fms-recording.v1" || record.initial.version !== 1 || record.v !== 1) throw new Error("unsupported recording version");
  if (!record.identity.qualified || !current.qualified) throw new Error(`unqualified environment: ${current.reason ?? record.identity.reason}`);
  if (!same(record.identity, current)) throw new Error("recording build or engine identity differs");
  if (!Number.isSafeInteger(record.lastBoundary) || record.lastBoundary < 0 || record.digests.length !== record.lastBoundary
    || record.digests.some(value => !/^[0-9a-f]{16}$/.test(value))) throw new Error("invalid recording boundary or digests");
  if (!same(record.builtins, current.build!.assets.map(asset => ({ bundleHash: current.build!.bundleHash!, ...asset })))) throw new Error("built-in content identity differs");
  if (record.snapshots.length) throw new Error("snapshots require I2");
  const store = await ContentStore.from(record.contents), users = memoryUserDatabaseStore();
  const initialUsers = await Promise.all(record.initial.users.map(async user => ({ scope: user.scope, text: user.content ? await store.text(user.content) : null })));
  for (const user of initialUsers) { if (user.text !== null) users.write(user.scope, user.text); }
  const target = record.lastBoundary;
  const fresh = createSession(record.initial.setup, { verification: true, beforeInitialAction: target === 0,
    ...(record.initial.userScope ? { userDatabase: { store: users, scope: record.initial.userScope } } : {}) });
  if (!same(fresh.initial, record.initial.observations)) throw new Error("initial state differs");
  if (!same(fresh.users, initialUsers)) throw new Error("initial user-store scopes or content differ");
  const journal = await Promise.all(record.journal.map(async entry => ({ ...entry, payload: await decodeAction(entry.payload, store) }))), kernel = fresh.kernel;
  const pending = record.pendingInputs === null ? null : await Promise.all(record.pendingInputs.map(async entry => ({ ...entry, action: await decodeAction(entry.action, store) as KernelAction })));
  if (record.capture.exact && !pending) throw new Error("missing boundary pending-input context");
  const entries = [...journal.filter(entry => entry.origin === "native" && entry.frame < target),
    ...record.control.filter(entry => entry.instant < target * FRAME_UNITS)].sort((a, b) => a.recordOrdinal - b.recordOrdinal);
  const consumed = new Set<number>();
  // Initial ios.place (before the scenario's t=0 poll) is made by the SAME session factory, not made twice.
  for (const existing of kernel.journal) consumed.add(existing.recordOrdinal);
  const apply = (entry: (typeof entries)[number]) => {
    if (consumed.has(entry.recordOrdinal)) return;
    if ("origin" in entry) kernel.submit(entry.payload as KernelAction, entry.source);
    else if (entry.kind === "rate") kernel.control({ kind: "rate", rate: entry.rate });
    else if (entry.kind === "halt" || entry.kind === "resume" || entry.kind === "reset") kernel.control({ kind: entry.kind });
    else if (entry.kind !== "execution-error") throw new Error(`unsupported control ${entry.kind}`);
    // execution-error is an observation of an actual throw, never an invented mutation to reproduce it.
    consumed.add(entry.recordOrdinal);
  };
  const at = (entry: (typeof entries)[number]) => "frame" in entry ? entry.frame : entry.instant / FRAME_UNITS;
  // Indexed by frame once, in recordOrdinal order, so a long recording costs O(entries), not O(frames x entries).
  const byFrame = new Map<number, (typeof entries)[number][]>();
  for (const entry of entries) { const list = byFrame.get(at(entry)); if (list) list.push(entry); else byFrame.set(at(entry), [entry]); }
  const firstTranslated = new Map<number, number>();
  for (const entry of journal) if (entry.origin === "v1-translated" && !firstTranslated.has(entry.frame)) firstTranslated.set(entry.frame, entry.recordOrdinal);
  const leading = (frame: number) => {
    const translated = firstTranslated.get(frame);
    if (translated !== undefined) for (const entry of byFrame.get(frame) ?? []) if (entry.recordOrdinal < translated) apply(entry);
  };
  while (kernel.frame < target) {
    for (const entry of byFrame.get(kernel.frame) ?? []) apply(entry);
    const before = kernel.frame;
    try {
      if (kernel.frame + 1 === target) kernel.verifyTo(target, { atNextBoundary: () => {
        for (const entry of pending ?? []) {
          const receipt = kernel.submit(entry.action, entry.source);
          if (receipt.submissionSeq !== entry.submissionSeq) throw new Error("pending submission sequence differs");
        }
      } });
      else if (!kernel.advance(1, { atNextBoundary: () => leading(kernel.frame) })) throw new Error("verification cannot reach boundary while halted");
    } catch (error) {
      // T17's actual composite throw moves the clock but skips this poll. It is not replayed by injecting an error.
      const observed = kernel.controlRecords.at(-1);
      const expected = record.control.find(entry => entry.kind === "execution-error" && entry.instant === before * FRAME_UNITS);
      if (!kernel.faulted || !expected || !same(observed, expected)) throw error;
    }
    const frame = kernel.frame - 1;
    if (kernel.frameDigest(frame) !== record.digests[frame]) throw new Error(`digest mismatch at frame ${frame}`);
  }
  if (target === 0) kernel.verifyTo(0);
  const expectedJournal = journal.filter(entry => entry.frame < target), expectedControl = record.control.filter(entry => entry.instant < target * FRAME_UNITS);
  const actual = kernel.boundaryRecord();
  if (record.capture.exact && !same(kernel.boundaryPendingInputs(), pending)) throw new Error("pending input replay differs");
  if (!same(actual.journal, expectedJournal) || !same(actual.control, expectedControl)) throw new Error("journal or outcome replay differs");
  if (record.capture.exact && (!same(actual.counters, record.counters) || actual.frame !== record.frame || !same(actual.liveQueue, record.liveQueue))) throw new Error("terminal ordering state differs");
  return { status: "verified", frame: target, exact: record.capture.exact };
}
