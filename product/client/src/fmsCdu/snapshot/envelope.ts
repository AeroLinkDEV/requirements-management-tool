import { encodeValue } from "./codec";
import { sha256 } from "./integrity";
import type { Accepted, Refused, RestoreRefusalCode, SnapshotRefusal } from "./refusal";

/**
 * The `aerolink.fms.snapshot` envelope, format version 1 (#1516; #1502 D7 9.4, 9.5). A snapshot is captured
 * synchronously at S_k as state bytes (the codec), then sealed asynchronously: the header is fixed and SHA-256 is taken
 * over the header and the state together, so the coverage label and configuration identity are covered by it.
 *
 * Header field order is fixed by this version, never by the order of the object handed in: every record is rebuilt in
 * its declared order and any field the version does not declare refuses.
 */

export const SNAPSHOT_FORMAT = "aerolink.fms.snapshot";
export const SNAPSHOT_FORMAT_VERSION = 1;

/** What the state sections cover. Restore refuses anything but `complete` (`COVERAGE_PARTIAL`). */
export const SNAPSHOT_COVERAGE = ["partial (kernel-only)", "partial (kernel + group A)", "complete"] as const;
export type SnapshotCoverage = (typeof SNAPSHOT_COVERAGE)[number];

/** The refusals of the version policy applied when a snapshot is restored (I2e). */
export type VersionPolicyCode = Extract<RestoreRefusalCode,
  "SCHEMA_NEWER" | "SCHEMA_OLDER_NO_MIGRATION" | "INTEGRITY" | "UNIT_UNSUPPORTED" | "CONFIG_MISMATCH" | "CONTENT_MISSING" | "COVERAGE_PARTIAL">;

export type UnitSchemaId = { readonly unitId: string; readonly schema: string; readonly schemaVersion: number };

/** A unit's configuration, recorded so restore can rebuild it and check it equal (it is never serialized as state). */
export type UnitConfigurationIdentity = {
  readonly unitId: string;
  readonly profileId: string;
  readonly profileVersion: string;
  readonly equipment: readonly string[];
  readonly seeds: readonly number[];
  /** SHA-256 of the frozen parameters. */
  readonly frozenParametersHash: string;
  /** The unit's real-time-clock offsets, carried here until the unit schemas own them. */
  readonly rtcOffsetsMs: readonly number[];
};

export const CONTENT_KINDS = ["navigationDatabaseCycle", "userDatabase", "magneticDatabase"] as const;
export type ContentRef = { readonly kind: (typeof CONTENT_KINDS)[number]; readonly contentHash: string };

/**
 * The qualified execution environment (D7 9.5). It names software, versions and hashes only: no host name, user name or
 * file path. The header encoder refuses undeclared fields and path separators, but that is structural only: keeping
 * host and user names out is an obligation of the record's producer (the kernel part of I2a), whose values must come
 * from a fixed allow-list of sources (runtime version, platform and architecture, browser brands), never from host or
 * user information. D7 9.5's database and configuration content hashes are carried in the header's `contentRefs` and
 * `configuration`, outside this record, and are covered by the same SHA-256.
 */
export type EnvironmentRecord = {
  readonly bundleHash: string;
  readonly runtimeKind: "browser" | "node";
  readonly runtimeName: string;
  readonly runtimeVersion: string;
  /** The JavaScript engine build string. */
  readonly engine: string;
  readonly os: string;
  readonly platform: string;
  readonly arch: string;
};

/** The header before sealing. */
export type SnapshotHeaderBody = {
  readonly format: typeof SNAPSHOT_FORMAT;
  readonly formatVersion: typeof SNAPSHOT_FORMAT_VERSION;
  readonly runId: string;
  /** k: the snapshot is the state at S_k. */
  readonly frame: number;
  readonly coverage: SnapshotCoverage;
  readonly units: readonly UnitSchemaId[];
  readonly configuration: readonly UnitConfigurationIdentity[];
  readonly contentRefs: readonly ContentRef[];
  readonly environment: EnvironmentRecord;
};

export type SnapshotHeader = SnapshotHeaderBody & { readonly sha256: string };

export type SealedSnapshot = {
  readonly header: SnapshotHeader;
  /** The sealed header's bytes: the body's fields, then `sha256`. */
  readonly headerBytes: Uint8Array;
  readonly state: Uint8Array;
};

// ------------------------------------------------------------------ the fixed field order of version 1

type FieldType =
  | "string" | "count" | "number"
  | { readonly literal: string | number }
  | { readonly oneOf: readonly string[] }
  | { readonly list: FieldType }
  | { readonly record: readonly (readonly [string, FieldType])[]; readonly noPaths?: boolean };

const UNIT_SCHEMA: FieldType = { record: [["unitId", "string"], ["schema", "string"], ["schemaVersion", "count"]] };
const CONFIGURATION: FieldType = {
  record: [
    ["unitId", "string"], ["profileId", "string"], ["profileVersion", "string"], ["equipment", { list: "string" }],
    ["seeds", { list: "number" }], ["frozenParametersHash", "string"], ["rtcOffsetsMs", { list: "number" }],
  ],
};
const CONTENT_REF: FieldType = { record: [["kind", { oneOf: CONTENT_KINDS }], ["contentHash", "string"]] };
const ENVIRONMENT: FieldType = {
  noPaths: true,
  record: [
    ["bundleHash", "string"], ["runtimeKind", { oneOf: ["browser", "node"] }], ["runtimeName", "string"], ["runtimeVersion", "string"],
    ["engine", "string"], ["os", "string"], ["platform", "string"], ["arch", "string"],
  ],
};
const BODY_FIELDS: readonly (readonly [string, FieldType])[] = [
  ["format", { literal: SNAPSHOT_FORMAT }], ["formatVersion", { literal: SNAPSHOT_FORMAT_VERSION }], ["runId", "string"], ["frame", "count"],
  ["coverage", { oneOf: SNAPSHOT_COVERAGE }], ["units", { list: UNIT_SCHEMA }], ["configuration", { list: CONFIGURATION }],
  ["contentRefs", { list: CONTENT_REF }], ["environment", ENVIRONMENT],
];
class HeaderRefusal {
  readonly refusal: SnapshotRefusal;
  constructor(refusal: SnapshotRefusal) { this.refusal = refusal; }
}

const refuse = (code: SnapshotRefusal["code"], detail: string): never => { throw new HeaderRefusal({ code, detail }); };

/** A copy of `value` in the declared order, or a refusal; never a partial or reordered record. */
function canonical(value: unknown, type: FieldType, where: string, noPaths = false): unknown {
  if (type === "string") {
    if (typeof value !== "string") refuse("UNCLASSIFIED_VALUE", `${where} must be a string`);
    if (noPaths && /[\\/]/.test(value as string)) refuse("UNCLASSIFIED_VALUE", `${where} must not contain a path`);
    return value;
  }
  if (type === "count") {
    if (!Number.isSafeInteger(value) || (value as number) < 0) refuse("UNCLASSIFIED_VALUE", `${where} must be a whole number`);
    return value;
  }
  if (type === "number") {
    if (typeof value !== "number") refuse("UNCLASSIFIED_VALUE", `${where} must be a number`);
    return value;
  }
  if ("literal" in type) {
    if (value !== type.literal) refuse("UNCLASSIFIED_VALUE", `${where} must be ${String(type.literal)}`);
    return value;
  }
  if ("oneOf" in type) {
    if (typeof value !== "string" || !type.oneOf.includes(value)) refuse("UNCLASSIFIED_VALUE", `${where} must be one of ${type.oneOf.join(", ")}`);
    return value;
  }
  if ("list" in type) {
    if (!Array.isArray(value)) refuse("UNCLASSIFIED_VALUE", `${where} must be a list`);
    return (value as unknown[]).map((item, i) => canonical(item, type.list, `${where}[${i}]`, noPaths));
  }
  return record(value, type.record, where, noPaths || type.noPaths === true);
}

function record(value: unknown, fields: readonly (readonly [string, FieldType])[], where: string, noPaths: boolean) {
  if (typeof value !== "object" || value === null || Array.isArray(value)) refuse("UNCLASSIFIED_VALUE", `${where} must be a record`);
  const source = value as Record<string, unknown>;
  const declared = new Set(fields.map(([name]) => name));
  for (const key of Reflect.ownKeys(source)) {
    if (typeof key !== "string" || !declared.has(key)) refuse("UNCLASSIFIED_PROPERTY", `${where} has the undeclared field ${String(key)}`);
  }
  const out: Record<string, unknown> = {};
  for (const [name, type] of fields) {
    const descriptor = Object.getOwnPropertyDescriptor(source, name);
    if (descriptor === undefined) refuse("UNCLASSIFIED_PROPERTY", `${where} is missing ${name}`);
    // Read each field exactly once, as data: an accessor could answer differently on a second read.
    if (!("value" in descriptor!)) refuse("UNCLASSIFIED_PROPERTY", `${where}.${name} is an accessor`);
    out[name] = canonical(descriptor!.value, type, `${where}.${name}`, noPaths);
  }
  return out;
}

/** The header body in version-1 order (read once) and its bytes. */
function canonicalBody(body: unknown): Accepted<{ readonly ordered: SnapshotHeaderBody; readonly bytes: Uint8Array }> | Refused<SnapshotRefusal> {
  let ordered: SnapshotHeaderBody;
  try {
    ordered = record(body, BODY_FIELDS, "header", false) as SnapshotHeaderBody;
  } catch (error) {
    if (error instanceof HeaderRefusal) return { ok: false, refusal: error.refusal };
    throw error;
  }
  const bytes = encodeValue("header", ordered);
  return bytes.ok ? { ok: true, value: { ordered, bytes: bytes.value } } : bytes;
}

/** The header body's bytes: its fields in version-1 order, whatever order they were given in. */
export function encodeHeaderBody(body: SnapshotHeaderBody): Accepted<Uint8Array> | Refused<SnapshotRefusal> {
  const canonical = canonicalBody(body);
  return canonical.ok ? { ok: true, value: canonical.value.bytes } : canonical;
}

/** What SHA-256 is taken over: u32 little-endian body length, the body's bytes, then the state bytes. */
function integrityInput(bodyBytes: Uint8Array, state: Uint8Array) {
  const out = new Uint8Array(4 + bodyBytes.length + state.length);
  new DataView(out.buffer).setUint32(0, bodyBytes.length, true);
  out.set(bodyBytes, 4);
  out.set(state, 4 + bodyBytes.length);
  return out;
}

/**
 * Seals captured state bytes. The body is read once and the state copied before the hash is awaited, so nothing done
 * during the await can change what is sealed, and the header returned is the one hashed. Without WebCrypto the result
 * is `INTEGRITY_UNAVAILABLE`.
 */
export async function sealSnapshot(body: SnapshotHeaderBody, state: Uint8Array): Promise<Accepted<SealedSnapshot> | Refused<SnapshotRefusal>> {
  const canonical = canonicalBody(body);
  if (!canonical.ok) return canonical;
  const bodyBytes = canonical.value.bytes;
  const captured = state.slice();
  const digest = await sha256(integrityInput(bodyBytes, captured));
  if (!digest.ok) return digest;
  // The body's fields in version-1 order, then sha256: the sealed header's field order.
  const header: SnapshotHeader = { ...canonical.value.ordered, sha256: digest.value };
  const sealed = encodeValue("header", header);
  if (!sealed.ok) return sealed;
  return { ok: true, value: { header, headerBytes: sealed.value, state: captured } };
}
