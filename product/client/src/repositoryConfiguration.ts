export type RepositoryMode = "ConfigureLater" | "ConnectNow";
export type RepositoryStatus = "Pending" | "ConfiguredUnverified" | "Verified";

export type RepositoryRecord = {
  projectId: string;
  mode: RepositoryMode;
  status: RepositoryStatus;
  provider?: string | null;
  endpoint?: string | null;
  version: number;
  configuredBy?: string | null;
  configuredAt?: string | null;
  lastVerifiedAt?: string | null;
  lastVerifiedBy?: string | null;
  remoteProjectId?: number | null;
  remotePath?: string | null;
  lastVerificationFailureAt?: string | null;
  lastVerificationFailureBy?: string | null;
};

export type RepositoryObservation = {
  verified: boolean;
  code?: string | null;
  detail?: string | null;
  remoteProjectId?: number | null;
  remotePath?: string | null;
};

export type RepositoryResponse = {
  repository: RepositoryRecord | null;
  canManage: boolean;
};

const asObject = (value: unknown): Record<string, unknown> =>
  value && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : {};

const statuses = new Set<RepositoryStatus>(["Pending", "ConfiguredUnverified", "Verified"]);
const modes = new Set<RepositoryMode>(["ConfigureLater", "ConnectNow"]);

const optionalField = (source: Record<string, unknown>, key: string): string | null | undefined =>
  source[key] === null || source[key] === undefined
    ? null
    : typeof source[key] === "string"
      ? source[key] as string
      : undefined;

/** Decode the project-scoped contract without trusting browser supplied status or identity fields. */
export function decodeRepositoryResponse(value: unknown): RepositoryResponse | undefined {
  const source = asObject(value);
  if (typeof source.canManage !== "boolean") return undefined;
  if (source.repository === null || source.repository === undefined) {
    return { repository: null, canManage: source.canManage };
  }
  const record = asObject(source.repository);
  const projectId = typeof record.projectId === "string" ? record.projectId.trim() : "";
  const mode = record.mode;
  const status = record.status;
  const version = record.version;
  if (
    !projectId ||
    !modes.has(mode as RepositoryMode) ||
    !statuses.has(status as RepositoryStatus) ||
    typeof version !== "number" ||
    !Number.isInteger(version) ||
    version < 1
  )
    return undefined;
  const remoteProjectId = record.remoteProjectId;
  if (
    (remoteProjectId !== null && remoteProjectId !== undefined &&
      (typeof remoteProjectId !== "number" || !Number.isInteger(remoteProjectId))) ||
    optionalField(record, "provider") === undefined ||
    optionalField(record, "endpoint") === undefined ||
    optionalField(record, "configuredBy") === undefined ||
    optionalField(record, "configuredAt") === undefined ||
    optionalField(record, "lastVerifiedAt") === undefined ||
    optionalField(record, "lastVerifiedBy") === undefined ||
    optionalField(record, "remotePath") === undefined ||
    optionalField(record, "lastVerificationFailureAt") === undefined ||
    optionalField(record, "lastVerificationFailureBy") === undefined
  )
    return undefined;
  return {
    repository: {
      projectId,
      mode: mode as RepositoryMode,
      status: status as RepositoryStatus,
      provider: optionalField(record, "provider"),
      endpoint: optionalField(record, "endpoint"),
      version,
      configuredBy: optionalField(record, "configuredBy"),
      configuredAt: optionalField(record, "configuredAt"),
      lastVerifiedAt: optionalField(record, "lastVerifiedAt"),
      lastVerifiedBy: optionalField(record, "lastVerifiedBy"),
      remoteProjectId: remoteProjectId as number | null | undefined,
      remotePath: optionalField(record, "remotePath"),
      lastVerificationFailureAt: optionalField(record, "lastVerificationFailureAt"),
      lastVerificationFailureBy: optionalField(record, "lastVerificationFailureBy"),
    },
    canManage: source.canManage,
  };
}

/** Decode the server observation independently from the stored configuration envelope. */
export function decodeRepositoryObservation(value: unknown): RepositoryObservation | undefined {
  const source = asObject(value);
  if (typeof source.verified !== "boolean") return undefined;
  const remoteProjectId = source.remoteProjectId;
  if (
    (remoteProjectId !== null && remoteProjectId !== undefined &&
      (typeof remoteProjectId !== "number" || !Number.isInteger(remoteProjectId))) ||
    optionalField(source, "code") === undefined ||
    optionalField(source, "detail") === undefined ||
    optionalField(source, "remotePath") === undefined
  ) return undefined;
  return {
    verified: source.verified,
    code: optionalField(source, "code"),
    detail: optionalField(source, "detail"),
    remoteProjectId: remoteProjectId as number | null | undefined,
    remotePath: optionalField(source, "remotePath"),
  };
}

export function repositoryStatusLabel(status: RepositoryStatus | undefined): string {
  if (status === "Verified") return "Verified";
  if (status === "ConfiguredUnverified") return "Configured · unverified";
  return "Pending";
}

export function repositoryStatusDescription(status: RepositoryStatus | undefined): string {
  if (status === "Verified") return "The server observed the configured GitLab project identity.";
  if (status === "ConfiguredUnverified") return "Configuration is saved, but no successful server verification is recorded.";
  return "No repository connection is verified. Unrelated project work can continue.";
}
