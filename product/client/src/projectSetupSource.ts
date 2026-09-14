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
  | "SourceOnly"
  | "Exclude"
  | "Statement"
  | "Rationale"
  | "VerificationMethod"
  | "SourceIdentifier"
  | "Title"
  | "Objective"
  | "Preconditions"
  | "Steps"
  | "ExpectedResult";

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

export type SourceObject = {
  key: string;
  module: string;
  sourceIdentifier: string;
  kind: string;
  attributes: Record<string, string>;
};

export type SourceModule = {
  key: string;
  name: string;
  objectCount: number;
  objectKeys?: string[];
  objects?: SourceObject[];
  level?: string;
  attributes: { key: string; name: string }[];
  mappings: SourceAttributeMapping[];
  include: boolean;
  exclusionReason?: string;
};

export type SourceRelation = {
  key?: string;
  sourceKey?: string;
  targetKey?: string;
  sourceType: string;
  count: number;
  include: boolean;
  type?: string;
  mappingType?: "AllocatedFrom" | "DerivedFrom";
  sourceIsParent?: boolean;
  exclusionReason?: string;
  attributes?: Record<string, string>;
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

export type SourceLadderSuggestion = {
  levels: string[];
  relationships: {
    key: string;
    type: string;
    sourceLevel: string;
    targetLevel: string;
  }[];
  findings: string[];
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
  stage?: string;
  manifestHash?: string;
  ladderSuggestion?: SourceLadderSuggestion;
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
    value === "SourceIdentifier" ||
    value === "Title" ||
    value === "Objective" ||
    value === "Preconditions" ||
    value === "Steps" ||
    value === "ExpectedResult"
    ? value
    : "SourceOnly";
}

function decodeValueMappings(value: unknown): SourceValueMapping[] | undefined {
  if (Array.isArray(value)) {
    return value.flatMap((entry) => {
      const row = asRecord(entry);
      const sourceValue = text(row.sourceValue ?? row.source);
      const destinationValue = text(row.destinationValue ?? row.destination);
      return sourceValue || destinationValue ? [{ sourceValue, destinationValue }] : [];
    });
  }
  const values = asRecord(value);
  const entries = Object.entries(values).flatMap(([sourceValue, destinationValue]) =>
    typeof destinationValue === "string" ? [{ sourceValue, destinationValue }] : [],
  );
  return entries.length ? entries : undefined;
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

function decodeObjects(value: unknown): SourceObject[] {
  if (!Array.isArray(value)) return [];
  return value.flatMap((entry) => {
    const row = asRecord(entry);
    const key = text(row.key).trim();
    if (!key) return [];
    const attributes = Object.fromEntries(
      Object.entries(asRecord(row.attributes)).flatMap(([attribute, attributeValue]) =>
        typeof attributeValue === "string" ? [[attribute, attributeValue]] : [],
      ),
    );
    return [
      {
        key,
        module: text(row.module).trim(),
        sourceIdentifier: text(row.sourceIdentifier).trim(),
        kind: text(row.kind).trim(),
        attributes,
      },
    ];
  });
}

function mappingRows(value: unknown, property: "objects" | "relations") {
  const row = asRecord(value);
  const candidates = Array.isArray(row[property]) ? row[property] : [];
  return new Map(
    candidates.flatMap((entry) => {
      const item = asRecord(entry);
      const key = text(item.sourceKey).trim();
      return key ? [[key, item] as const] : [];
    }),
  );
}

function decodeModules(value: unknown, mappingValue?: unknown): SourceModule[] {
  if (!Array.isArray(value)) return [];
  const mappingsByObject = mappingRows(mappingValue, "objects");
  return value.flatMap((entry) => {
    const row = asRecord(entry);
    const key = text(row.key).trim();
    if (!key) return [];
    const objects = decodeObjects(row.objects);
    const observedAttributes = objects.flatMap((object) =>
      Object.keys(object.attributes).map((attributeKey) => ({ key: attributeKey, name: attributeKey })),
    );
    const attributes = Array.isArray(row.attributes)
      ? row.attributes.flatMap((attribute) => {
          const item = asRecord(attribute);
          const attributeKey = text(item.key ?? item.name).trim();
          const name = text(item.name ?? item.key).trim();
          return attributeKey ? [{ key: attributeKey, name: name || attributeKey }] : [];
        })
      : observedAttributes;
    const uniqueAttributes = Array.from(
      new Map(attributes.map((attribute) => [attribute.key, attribute])).values(),
    );
    const objectMappingEntries = objects.flatMap((object) => {
      const mapping = mappingsByObject.get(object.key);
      return mapping ? [mapping] : [];
    });
    const firstObjectMapping = objectMappingEntries[0];
    const mappings = decodeAttributes(firstObjectMapping?.attributes ?? row.mappings);
    const observedMappings = uniqueAttributes.map((attribute) => ({
      sourceAttribute: attribute.key,
      destination: "SourceOnly" as const,
    }));
    const objectKeys = Array.isArray(row.objectKeys)
      ? row.objectKeys.filter((item): item is string => typeof item === "string" && item.trim() !== "")
      : objects.map((object) => object.key);
    return [
      {
        key,
        name: text(row.name).trim() || key,
        objectCount: nonNegative(row.objectCount) || objects.length,
        ...(objectKeys.length ? { objectKeys } : {}),
        ...(objects.length ? { objects } : {}),
        ...(text(firstObjectMapping?.level ?? row.level).trim()
          ? { level: text(firstObjectMapping?.level ?? row.level).trim() }
          : objects.every((object) => object.attributes.Level === objects[0]?.attributes.Level)
            && text(objects[0]?.attributes.Level).trim()
            ? { level: text(objects[0]?.attributes.Level).trim() }
            : {}),
        attributes: uniqueAttributes,
        mappings: mappings.length ? mappings : observedMappings,
        include: firstObjectMapping
          ? objectMappingEntries.every((mapping) => boolean(mapping.include, true))
          : boolean(row.include, true),
        ...(text(firstObjectMapping?.exclusionReason ?? row.exclusionReason).trim()
          ? { exclusionReason: text(firstObjectMapping?.exclusionReason ?? row.exclusionReason).trim() }
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

function decodeRelations(value: unknown, mappingValue?: unknown): SourceRelation[] {
  if (!Array.isArray(value)) return [];
  const mappingsByRelation = mappingRows(mappingValue, "relations");
  return value.flatMap((entry) => {
    const row = asRecord(entry);
    const key = text(row.key).trim();
    const mapping = key ? mappingsByRelation.get(key) : undefined;
    const sourceType = text(row.sourceType ?? row.type ?? key).trim();
    if (!sourceType) return [];
    return [
      {
        ...(key ? { key } : {}),
        ...(text(row.sourceKey).trim() ? { sourceKey: text(row.sourceKey).trim() } : {}),
        ...(text(row.targetKey).trim() ? { targetKey: text(row.targetKey).trim() } : {}),
        sourceType,
        count: nonNegative(row.count) || 1,
        include: mapping ? boolean(mapping.include, true) : boolean(row.include, true),
        ...(text(row.type).trim() ? { type: text(row.type).trim() } : {}),
        ...((mapping?.type ?? row.mappingType) === "AllocatedFrom" ||
        (mapping?.type ?? row.mappingType) === "DerivedFrom"
          ? { mappingType: (mapping?.type ?? row.mappingType) as "AllocatedFrom" | "DerivedFrom" }
          : {}),
        ...(typeof (mapping?.sourceIsParent ?? row.sourceIsParent) === "boolean"
          ? { sourceIsParent: (mapping?.sourceIsParent ?? row.sourceIsParent) as boolean }
          : {}),
        ...(text(mapping?.exclusionReason ?? row.exclusionReason).trim()
          ? { exclusionReason: text(mapping?.exclusionReason ?? row.exclusionReason).trim() }
          : {}),
        ...(Object.keys(asRecord(row.attributes)).length
          ? {
              attributes: Object.fromEntries(
                Object.entries(asRecord(row.attributes)).flatMap(([name, value]) =>
                  typeof value === "string" ? [[name, value]] : [],
                ),
              ),
            }
          : {}),
      },
    ];
  });
}

function deriveCategories(
  modules: SourceModule[],
  relations: SourceRelation[],
  kind: SourceKind,
  format: string,
): SourceCategory[] {
  const counts = new Map<SourceCategoryKey, number>(
    sourceCategoryKeys.map((key) => [key, 0]),
  );
  for (const object of modules.flatMap((module) => module.objects ?? [])) {
    const category =
      object.kind === "Requirement" || object.kind === "Unmapped"
        ? "Requirements"
        : object.kind === "Case"
          ? "Cases"
          : object.kind === "Procedure"
            ? "Procedures"
            : object.kind === "Evidence"
              ? "Evidence"
              : null;
    if (category) counts.set(category, (counts.get(category) ?? 0) + 1);
  }
  for (const relation of relations) {
    const relationType = (relation.type ?? relation.sourceType).toLocaleLowerCase();
    if (
      relationType === "requirementtrace" ||
      relationType === "allocatedfrom" ||
      relationType === "derivedfrom"
    )
      counts.set("Traces", (counts.get("Traces") ?? 0) + relation.count);
    else if (relationType === "caseprocedure")
      counts.set("Cases", (counts.get("Cases") ?? 0) + relation.count);
    else if (relationType === "verificationcoverage")
      counts.set("Procedures", (counts.get("Procedures") ?? 0) + relation.count);
    else if (relationType === "evidenceexecution")
      counts.set("Evidence", (counts.get("Evidence") ?? 0) + relation.count);
  }
  const externalFormat = format.toLocaleLowerCase();
  const supported =
    kind === "AeroLinkBaseline"
      ? new Set<SourceCategoryKey>(sourceCategoryKeys)
      : externalFormat === "reqif" || externalFormat === "reqifz"
        ? new Set<SourceCategoryKey>(["Requirements", "Traces"])
        : new Set<SourceCategoryKey>(["Requirements"]);
  return sourceCategoryKeys.map((key) => ({
    key,
    count: counts.get(key) ?? 0,
    requires: key === "Traces" ? ["Requirements"] : [],
    supported: supported.has(key),
    ...(supported.has(key) ? {} : { reason: `${format || "This source format"} does not support ${key} during inception.` }),
  }));
}

function decodeLadderSuggestion(value: unknown): SourceLadderSuggestion | undefined {
  const row = asRecord(value);
  if (!Array.isArray(row.levels)) return undefined;
  const levels = row.levels.filter((entry): entry is string => typeof entry === "string" && entry.trim() !== "");
  const relationships = Array.isArray(row.relationships)
    ? row.relationships.flatMap((entry) => {
        const item = asRecord(entry);
        const key = text(item.key).trim();
        const type = text(item.type).trim();
        const sourceLevel = text(item.sourceLevel).trim();
        const targetLevel = text(item.targetLevel).trim();
        return key && type && sourceLevel && targetLevel
          ? [{ key, type, sourceLevel, targetLevel }]
          : [];
      })
    : [];
  const findings = Array.isArray(row.findings)
    ? row.findings.filter((entry): entry is string => typeof entry === "string" && entry.trim() !== "")
    : [];
  return { levels, relationships, findings };
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
    displayName:
      text(row.displayName).trim() ||
      text(row.fileName).trim() ||
      (kind === "AeroLinkBaseline" ? "Authorized AeroLink baseline" : "Imported baseline"),
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
    categories: (() => {
      const decoded = decodeCategories(row.categories);
      return decoded.length
        ? decoded
        : deriveCategories(
            decodeModules(row.modules, row.mapping),
            decodeRelations(row.relations, row.mapping),
            kind,
            text(row.format),
          );
    })(),
    selectedCategories: Array.isArray(row.selectedCategories)
      ? row.selectedCategories.filter((item): item is string => typeof item === "string")
      : [],
    modules: decodeModules(row.modules, row.mapping),
    relations: decodeRelations(row.relations, row.mapping),
    findings: Array.isArray(row.findings)
      ? row.findings.flatMap((entry) => {
          if (typeof entry === "string") {
            const message = entry.trim();
            return message ? [{ key: message, message }] : [];
          }
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
    ...(text(row.stage).trim() ? { stage: text(row.stage).trim() } : {}),
    ...(text(row.manifestHash).trim() ? { manifestHash: text(row.manifestHash).trim() } : {}),
    ...(decodeLadderSuggestion(row.ladderSuggestion)
      ? { ladderSuggestion: decodeLadderSuggestion(row.ladderSuggestion) }
      : {}),
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
  const objectMappings = source.modules.flatMap((module) => {
    const objects = module.objects?.length
      ? module.objects
      : (module.objectKeys ?? []).map((key) => ({
          key,
          module: module.name,
          sourceIdentifier: key,
          kind: "",
          attributes: {},
        }));
    return objects.map((object) => ({
      sourceKey: object.key,
      include: module.include,
      ...(module.exclusionReason?.trim() ? { exclusionReason: module.exclusionReason.trim() } : {}),
      ...(module.level ? { level: module.level } : {}),
      attributes: module.mappings.map((mapping) => ({
        sourceAttribute: mapping.sourceAttribute,
        destination: mapping.destination,
        ...(mapping.reason?.trim() ? { reason: mapping.reason.trim() } : {}),
        ...(mapping.valueMappings
          ? { valueMappings: Object.fromEntries(mapping.valueMappings.map((value) => [value.sourceValue, value.destinationValue])) }
          : {}),
      })),
    }));
  });
  const relations = source.relations.map((relation) => ({
    sourceKey: relation.key ?? relation.sourceKey ?? relation.sourceType,
    include: relation.include,
    ...(relation.mappingType ? { type: relation.mappingType } : { type: null }),
    ...(typeof relation.sourceIsParent === "boolean"
      ? { sourceIsParent: relation.sourceIsParent }
      : {}),
    ...(relation.type && !["RequirementTrace", "AllocatedFrom", "DerivedFrom"].includes(relation.type)
      ? { relationshipKind: relation.type }
      : {}),
    ...(relation.exclusionReason?.trim() ? { exclusionReason: relation.exclusionReason.trim() } : {}),
  }));
  const mapping = {
    sourceSha256: source.sha256,
    objects: objectMappings,
    relations,
    findingResolutions: { ...source.findingResolutions },
  };
  return {
    expectedVersion,
    selectedCategories: [...selectedCategories],
    // Parser-derived metadata is returned by SourceView and is intentionally never sent as a
    // creator assertion. The source endpoint rejects edited metadata; an empty object keeps the
    // typed request explicit.
    metadata: {},
    mapping,
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
  void draftId;
  return `${api}/api/project-setups/source-options?offset=${offset}&limit=${limit}`;
}

export const sourceUploadAccept = ".reqif,.reqifz,.xml,.csv,.xlsx";
