export type SourceKind = "AeroLinkBaseline" | "ExternalBaseline";

export const sourceCategoryKeys = [
  "Requirements",
  "Traces",
  "Cases",
  "Procedures",
  "Evidence",
] as const;

export type SourceCategoryKey = (typeof sourceCategoryKeys)[number];
export type SourceMappingDestination =
  "SourceOnly" | "Exclude" | "Statement" | "Rationale" | "VerificationMethod" | "SourceIdentifier";

export type SourceValueMapping = {
  sourceValue: string;
  destinationValue: string;
};

export type SourceAttributeMapping = {
  sourceAttribute: string;
  destination: SourceMappingDestination;
  reason?: string;
  valueMappings?: SourceValueMapping[];
};

export type SourceModule = {
  key: string;
  name: string;
  objectCount: number;
  level?: string;
  attributes: { key: string; name: string }[];
  mappings: SourceAttributeMapping[];
  include: boolean;
  exclusionReason?: string;
};

export type SourceRelation = {
  sourceType: string;
  count: number;
  include: boolean;
  type?: string;
  sourceIsParent?: boolean;
  exclusionReason?: string;
};

export type SourceCategory = {
  key: SourceCategoryKey;
  count: number;
  requires: string[];
  supported: boolean;
  reason?: string;
};

export type SourceReconciliation = {
  ready: boolean;
  observedObjects: number;
  includedObjects: number;
  excludedObjects: number;
  observedRelations: number;
  includedRelations: number;
  excludedRelations: number;
  errors: string[];
  manifestHash?: string;
};

export type SourceView = {
  id: string;
  kind: SourceKind;
  displayName: string;
  fileName?: string;
  format?: string;
  sha256: string;
  sourceBaselineId?: string;
  sourceProjectId?: string;
  sourceState?: string;
  metadata: {
    sourceSystem?: string;
    sourceSystemVersion?: string;
    sourceBaselineName?: string;
    sourceBaselineDate?: string;
    extractedBy?: string;
    extractedAt?: string;
  };
  categories: SourceCategory[];
  selectedCategories: string[];
  modules: SourceModule[];
  relations: SourceRelation[];
  findings: { key: string; message: string }[];
  findingResolutions: Record<string, string>;
  reconciliation: SourceReconciliation | null;
  assertion: { text: string; hash: string } | null;
};

export type NativeSourceOption = {
  baselineId: string;
  projectId: string;
  projectName: string;
  name: string;
  displayNumber: string;
  state: string;
  requirementsCount: number;
  casesCount: number;
  proceduresCount: number;
  evidenceCount: number;
};

export type NativeSourceOptionsPage = {
  items: NativeSourceOption[];
  total: number;
  offset: number;
  limit: number;
};

export type SourceDraftState = {
  source: SourceView | null;
  assertionAccepted: boolean;
  password: string;
};

const asRecord = (value: unknown): Record<string, unknown> =>
  value && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : {};
const text = (value: unknown) => (typeof value === "string" ? value : "");
const nonNegative = (value: unknown) =>
  typeof value === "number" && Number.isFinite(value) && value >= 0 ? value : 0;
const boolean = (value: unknown, fallback = false) =>
  typeof value === "boolean" ? value : fallback;

function destination(value: unknown): SourceMappingDestination {
  return value === "Exclude" ||
    value === "Statement" ||
    value === "Rationale" ||
    value === "VerificationMethod" ||
    value === "SourceIdentifier"
    ? value
    : "SourceOnly";
}

function decodeValueMappings(value: unknown): SourceValueMapping[] | undefined {
  if (!Array.isArray(value)) return undefined;
  return value.flatMap((entry) => {
    const row = asRecord(entry);
    const sourceValue = text(row.sourceValue ?? row.source);
    const destinationValue = text(row.destinationValue ?? row.destination);
    return sourceValue || destinationValue ? [{ sourceValue, destinationValue }] : [];
  });
}

function decodeAttributes(value: unknown): SourceAttributeMapping[] {
  if (!Array.isArray(value)) return [];
  return value.flatMap((entry) => {
    const row = asRecord(entry);
    const sourceAttribute = text(row.sourceAttribute ?? row.key ?? row.name).trim();
    if (!sourceAttribute) return [];
    return [
      {
        sourceAttribute,
        destination: destination(row.destination),
        ...(text(row.reason).trim() ? { reason: text(row.reason).trim() } : {}),
        ...(decodeValueMappings(row.valueMappings)
          ? { valueMappings: decodeValueMappings(row.valueMappings) }
          : {}),
      },
    ];
  });
}

function decodeModules(value: unknown): SourceModule[] {
  if (!Array.isArray(value)) return [];
  return value.flatMap((entry) => {
    const row = asRecord(entry);
    const key = text(row.key).trim();
    if (!key) return [];
    const attributes = Array.isArray(row.attributes)
      ? row.attributes.flatMap((attribute) => {
          const item = asRecord(attribute);
          const attributeKey = text(item.key ?? item.name).trim();
          const name = text(item.name ?? item.key).trim();
          return attributeKey ? [{ key: attributeKey, name: name || attributeKey }] : [];
        })
      : [];
    return [
      {
        key,
        name: text(row.name).trim() || key,
        objectCount: nonNegative(row.objectCount),
        ...(text(row.level).trim() ? { level: text(row.level).trim() } : {}),
        attributes,
        mappings: decodeAttributes(row.mappings),
        include: boolean(row.include, true),
        ...(text(row.exclusionReason).trim()
          ? { exclusionReason: text(row.exclusionReason).trim() }
          : {}),
      },
    ];
  });
}

function decodeCategories(value: unknown): SourceCategory[] {
  if (!Array.isArray(value)) return [];
  return value.flatMap((entry) => {
    const row = asRecord(entry);
    const key = text(row.key) as SourceCategoryKey;
    if (!sourceCategoryKeys.includes(key)) return [];
    const requires = Array.isArray(row.requires)
      ? row.requires.filter(
          (item): item is string => typeof item === "string" && item.trim() !== "",
        )
      : [];
    return [
      {
        key,
        count: nonNegative(row.count),
        requires,
        supported: boolean(row.supported),
        ...(text(row.reason).trim() ? { reason: text(row.reason).trim() } : {}),
      },
    ];
  });
}

function decodeRelations(value: unknown): SourceRelation[] {
  if (!Array.isArray(value)) return [];
  return value.flatMap((entry) => {
    const row = asRecord(entry);
    const sourceType = text(row.sourceType ?? row.key).trim();
    if (!sourceType) return [];
    return [
      {
        sourceType,
        count: nonNegative(row.count),
        include: boolean(row.include, true),
        ...(text(row.type).trim() ? { type: text(row.type).trim() } : {}),
        ...(typeof row.sourceIsParent === "boolean" ? { sourceIsParent: row.sourceIsParent } : {}),
        ...(text(row.exclusionReason).trim()
          ? { exclusionReason: text(row.exclusionReason).trim() }
          : {}),
      },
    ];
  });
}

function decodeReconciliation(value: unknown): SourceReconciliation | null {
  if (!value || typeof value !== "object" || Array.isArray(value)) return null;
  const row = asRecord(value);
  return {
    ready: boolean(row.ready),
    observedObjects: nonNegative(row.observedObjects),
    includedObjects: nonNegative(row.includedObjects),
    excludedObjects: nonNegative(row.excludedObjects),
    observedRelations: nonNegative(row.observedRelations),
    includedRelations: nonNegative(row.includedRelations),
    excludedRelations: nonNegative(row.excludedRelations),
    errors: Array.isArray(row.errors)
      ? row.errors.filter((item): item is string => typeof item === "string")
      : [],
    ...(text(row.manifestHash).trim() ? { manifestHash: text(row.manifestHash).trim() } : {}),
  };
}

export function decodeSourceView(value: unknown): SourceView | null {
  const row = asRecord(value);
  const id = text(row.id).trim();
  const kind = row.kind === "AeroLinkBaseline" || row.kind === "ExternalBaseline" ? row.kind : null;
  if (!id || !kind) return null;
  const metadata = asRecord(row.metadata);
  const assertion = asRecord(row.assertion);
  return {
    id,
    kind,
    displayName: text(row.displayName).trim() || "Source baseline",
    ...(text(row.fileName).trim() ? { fileName: text(row.fileName).trim() } : {}),
    ...(text(row.format).trim() ? { format: text(row.format).trim() } : {}),
    sha256: text(row.sha256).trim(),
    ...(text(row.sourceBaselineId).trim()
      ? { sourceBaselineId: text(row.sourceBaselineId).trim() }
      : {}),
    ...(text(row.sourceProjectId).trim()
      ? { sourceProjectId: text(row.sourceProjectId).trim() }
      : {}),
    ...(text(row.sourceState).trim() ? { sourceState: text(row.sourceState).trim() } : {}),
    metadata: {
      ...(text(metadata.sourceSystem).trim()
        ? { sourceSystem: text(metadata.sourceSystem).trim() }
        : {}),
      ...(text(metadata.sourceSystemVersion).trim()
        ? { sourceSystemVersion: text(metadata.sourceSystemVersion).trim() }
        : {}),
      ...(text(metadata.sourceBaselineName).trim()
        ? { sourceBaselineName: text(metadata.sourceBaselineName).trim() }
        : {}),
      ...(text(metadata.sourceBaselineDate).trim()
        ? { sourceBaselineDate: text(metadata.sourceBaselineDate).trim() }
        : {}),
      ...(text(metadata.extractedBy).trim()
        ? { extractedBy: text(metadata.extractedBy).trim() }
        : {}),
      ...(text(metadata.extractedAt).trim()
        ? { extractedAt: text(metadata.extractedAt).trim() }
        : {}),
    },
    categories: decodeCategories(row.categories),
    selectedCategories: Array.isArray(row.selectedCategories)
      ? row.selectedCategories.filter((item): item is string => typeof item === "string")
      : [],
    modules: decodeModules(row.modules),
    relations: decodeRelations(row.relations),
    findings: Array.isArray(row.findings)
      ? row.findings.flatMap((entry) => {
          const item = asRecord(entry);
          const key = text(item.key).trim();
          const message = text(item.message).trim();
          return key && message ? [{ key, message }] : [];
        })
      : [],
    findingResolutions: Object.fromEntries(
      Object.entries(asRecord(row.findingResolutions)).flatMap(([key, value]) =>
        typeof value === "string" ? [[key, value]] : [],
      ),
    ),
    reconciliation: decodeReconciliation(row.reconciliation),
    assertion:
      text(assertion.text).trim() && text(assertion.hash).trim()
        ? { text: text(assertion.text).trim(), hash: text(assertion.hash).trim() }
        : null,
  };
}

export function decodeNativeSourceOptions(value: unknown): NativeSourceOptionsPage {
  const row = asRecord(value);
  const items = Array.isArray(row.items)
    ? row.items.flatMap((entry) => {
        const item = asRecord(entry);
        const baselineId = text(item.baselineId).trim();
        if (!baselineId) return [];
        return [
          {
            baselineId,
            projectId: text(item.projectId).trim(),
            projectName: text(item.projectName).trim() || "Project",
            name: text(item.name).trim() || "Baseline",
            displayNumber: text(item.displayNumber).trim(),
            state: text(item.state).trim() || "Unknown",
            requirementsCount: nonNegative(item.requirementsCount),
            casesCount: nonNegative(item.casesCount),
            proceduresCount: nonNegative(item.proceduresCount),
            evidenceCount: nonNegative(item.evidenceCount),
          },
        ];
      })
    : [];
  const offset = nonNegative(row.offset);
  const limit = nonNegative(row.limit) || 50;
  return {
    items,
    total: nonNegative(row.total),
    offset,
    limit,
  };
}

export function sourceConfigurationPayload(
  source: SourceView,
  expectedVersion: number,
  selectedCategories: string[],
): Record<string, unknown> {
  return {
    expectedVersion,
    selectedCategories: [...selectedCategories],
    metadata: { ...source.metadata },
    modules: source.modules.map((module) => ({
      key: module.key,
      level: module.level ?? null,
      include: module.include,
      ...(module.exclusionReason?.trim() ? { exclusionReason: module.exclusionReason.trim() } : {}),
      attributes: module.mappings.map((mapping) => ({
        sourceAttribute: mapping.sourceAttribute,
        destination: mapping.destination,
        ...(mapping.reason?.trim() ? { reason: mapping.reason.trim() } : {}),
        ...(mapping.valueMappings
          ? {
              valueMappings: mapping.valueMappings.map((valueMapping) => ({
                sourceValue: valueMapping.sourceValue,
                destinationValue: valueMapping.destinationValue,
              })),
            }
          : {}),
      })),
    })),
    relations: source.relations.map((relation) => ({
      sourceType: relation.sourceType,
      include: relation.include,
      ...(relation.type?.trim() ? { type: relation.type.trim() } : {}),
      ...(typeof relation.sourceIsParent === "boolean"
        ? { sourceIsParent: relation.sourceIsParent }
        : {}),
      ...(relation.exclusionReason?.trim()
        ? { exclusionReason: relation.exclusionReason.trim() }
        : {}),
    })),
    findingResolutions: { ...source.findingResolutions },
  };
}

export function sourceFinalizationPayload(
  expectedVersion: number,
  idempotencyKey: string,
  state: SourceDraftState,
) {
  return {
    expectedVersion,
    idempotencyKey,
    password: state.password,
    sourceAssertionHash: state.source?.assertion?.hash ?? null,
    sourceAssertionAccepted: state.assertionAccepted,
  };
}

export function sourcePageUrl(api: string, draftId: string, offset: number, limit = 50) {
  return `${api}/api/project-setups/${draftId}/source-options?offset=${offset}&limit=${limit}`;
}

export const sourceUploadAccept = ".reqif,.xml,.csv,.xlsx";
