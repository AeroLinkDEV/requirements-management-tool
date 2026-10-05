// Discovery must never claim execution provenance. Only the selected execution command enables this marker;
// its checkout identity comes from git rather than an event SHA that may name a different dispatch revision.
export function browserReportMetadata(env = process.env) {
  if (!env.AEROLINK_E2E_REPORT_EXECUTION) return {}
  const identity = {
    reportKind: 'execution', runId: env.GITHUB_RUN_ID, runAttempt: env.GITHUB_RUN_ATTEMPT,
    checkoutSha: env.AEROLINK_E2E_REPORT_SHA, job: env.METRICS_JOB_ID, shard: env.AEROLINK_E2E_SHARD,
    shardTotal: env.AEROLINK_E2E_REPORT_SHARD_TOTAL,
  }
  if (env.AEROLINK_E2E_REPORT_EXECUTION !== '1'
    || !/^[1-9]\d*$/.test(identity.runId ?? '') || !/^[1-9]\d*$/.test(identity.runAttempt ?? '')
    || !/^[a-f0-9]{40}$/.test(identity.checkoutSha ?? '')
    || !['browser-pr', 'browser-full'].includes(identity.job) || !/^[1-9]\d*$/.test(identity.shard ?? '')
    || !/^[1-9]\d*$/.test(identity.shardTotal ?? '') || Number(identity.shard) > Number(identity.shardTotal)) {
    throw new Error('Browser execution report identity is missing or invalid.')
  }
  return { browserExecution: identity }
}
