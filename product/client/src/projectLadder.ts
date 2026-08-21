export type LadderLevel = 'System' | 'HighLevel' | 'LowLevel'

export type ProjectLadderProjection = {
  /** Authored steps are intentionally not used by runtime surfaces; the server supplies this effective view. */
  effectiveSteps: { catalogueEntry: LadderLevel; capabilities: number | string }[]
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
