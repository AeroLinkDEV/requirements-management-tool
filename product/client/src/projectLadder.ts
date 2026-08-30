export type LadderLevel = 'System' | 'HighLevel' | 'LowLevel' | 'Interface'

export type LadderRelationship = {
  /** Server-owned catalogue vocabulary; runtime policy must not silently discard a future level. */
  parent: string
  child: string
}

export type ProjectLadderProjection = {
  /** Authored steps are intentionally not used by runtime surfaces; the server supplies this effective view. */
  effectiveSteps: {
    catalogueEntry: string
    capabilities: number | string
    enabledArtifactKinds?: string[]
  }[]
  /** Direct relationships from the effective, activated ladder. Authored draft edges are not runtime truth. */
  effectiveRelationships?: LadderRelationship[]
  state?: string
  classification?: string
}

export const LadderCapability = {
  ChangeControl: 1,
  Verification: 2,
  RequirementsDocument: 4,
  CodeTraceability: 8,
} as const

const capabilityNames: Record<string, number> = {
  ChangeControl: LadderCapability.ChangeControl,
  Verification: LadderCapability.Verification,
  RequirementsDocument: LadderCapability.RequirementsDocument,
  CodeTraceability: LadderCapability.CodeTraceability,
  HasChangeControl: LadderCapability.ChangeControl,
  HasVerification: LadderCapability.Verification,
  HasRequirementsDocument: LadderCapability.RequirementsDocument,
  HasCodeTraceability: LadderCapability.CodeTraceability,
}

/** ASP.NET's configured enum converter returns flag values as a comma-separated name list. */
export function capabilityMask(value: unknown): number {
  if (typeof value === 'number' && Number.isFinite(value)) return value
  if (typeof value !== 'string') return 0
  const numeric = Number(value)
  if (Number.isFinite(numeric)) return numeric
  return value.split(',').reduce((mask, name) => {
    const capability = capabilityNames[name.trim()]
    return mask | (capability ?? 0)
  }, 0)
}

/**
 * A null projection means the project configuration is still loading (or failed). Policy-dependent surfaces
 * fail closed until the stored projection arrives; route parsing remains independent so an existing deep link
 * can still be displayed as a controlled page rather than silently presenting every legacy level.
 */
export function ladderAllows(
  ladder: ProjectLadderProjection | null | undefined,
  level: LadderLevel,
  capability?: number,
): boolean {
  if (!ladder) return false
  const step = ladder.effectiveSteps.find(item => item.catalogueEntry === level)
  return !!step && (capability === undefined || (capabilityMask(step.capabilities) & capability) === capability)
}

export function ladderHasAny(
  ladder: ProjectLadderProjection | null | undefined,
  levels: LadderLevel[],
  capability?: number,
): boolean {
  return levels.some(level => ladderAllows(ladder, level, capability))
}

/**
 * Downstream assessments are applicable only where the effective ladder says that a configured parent with
 * change control feeds this exact target level. This intentionally does not infer topology from enum order,
 * treat System specially, or inspect authored draft relationships.
 */
export function ladderAllowsDownstreamAssessment(
  ladder: ProjectLadderProjection | null | undefined,
  targetLevel: LadderLevel,
): boolean {
  if (!ladder || !ladderAllows(ladder, targetLevel)) return false
  return (ladder.effectiveRelationships ?? []).some(edge => {
    if (edge.child !== targetLevel) return false
    const parent = ladder.effectiveSteps.find(step => step.catalogueEntry === edge.parent)
    return !!parent && (capabilityMask(parent.capabilities) & LadderCapability.ChangeControl) === LadderCapability.ChangeControl
  })
}

/** Exact artifact-profile predicate; level verification alone must not activate dormant Procedure surfaces. */
export function ladderEnablesArtifactKind(
  ladder: ProjectLadderProjection | null | undefined,
  level: LadderLevel,
  kind: string,
): boolean {
  if (!ladder) return false
  const step = ladder.effectiveSteps.find(item => item.catalogueEntry === level)
  return !!step?.enabledArtifactKinds?.some(value => value.toLowerCase() === kind.toLowerCase())
}
