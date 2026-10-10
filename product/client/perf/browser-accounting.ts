/**
 * #1557 compatibility gate. No browser accounting schema is qualified by this implementation: the historical
 * interval-count schema cannot describe whole-frame work across timeout continuations. A later accounting change
 * must qualify its producer, measured boundaries and attribution before adding an accepted schema here.
 */
export function browserScoringRefusal(protocol: { browser?: { accounting?: { revision?: number; mode?: string } } }) {
  return {
    status: 'REFUSED' as const,
    accountingIssue: '#1557',
    policyRevision: protocol.browser?.accounting?.revision ?? null,
    declaredMode: protocol.browser?.accounting?.mode ?? null,
    supportedAccountingSchemas: [] as string[],
    reason: 'Completed frames, outstanding work at window boundaries and continuation cost are not qualified; interval count times selected rate is not browser scoring evidence.',
    metrics: null,
  }
}

/** The direct owner/configuration also calls this, so bypassing the orchestrator cannot enable scoring. */
export function assertBrowserScoringAvailable(protocol: Parameters<typeof browserScoringRefusal>[0]): never {
  throw new Error(`FMS_BROWSER_ACCOUNTING_UNAVAILABLE: ${browserScoringRefusal(protocol).reason}`)
}

/** Preserve attributable records without computing browser levels, intervals, planned N or budget verdicts. */
export function browserQualification(protocol: Parameters<typeof browserScoringRefusal>[0], records: {
  index: number; status: string; arm: string; configuration?: string; rate?: number;
  armIdentity?: { commit?: string }; result?: Record<string, unknown> | null;
}[]) {
  const refusal = browserScoringRefusal(protocol)
  return {
    ...refusal, status: 'NOT_QUALIFIED' as const,
    records: records.map(record => ({
      index: record.index, status: record.status, arm: record.arm, commit: record.armIdentity?.commit ?? null,
      configuration: record.configuration ?? null, rate: record.rate ?? null,
      accountingSchema: record.result?.schema ?? null,
      compatibility: record.result?.schema === 'aerolink.fms-perf-browser-run.v1' ? 'historical-interval-inference' : 'unsupported-or-missing',
      reason: refusal.reason,
    })),
  }
}
