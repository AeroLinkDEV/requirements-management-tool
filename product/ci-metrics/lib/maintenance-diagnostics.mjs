// Fixed diagnostic identifiers only. Never inspect exception text, HTTP payloads or credentials.
const codes = new Set([
  'event-read', 'runtime-context', 'request-configuration', 'authority-configuration', 'ruleset-configuration',
  'initial-publication', 'approval-read', 'approval-reread', 'approval-history-guard', 'approval-evaluation',
  'collection', 'collection-rest-read', 'collection-ruleset-read', 'collection-graphql-read',
  'collection-pr-read', 'collection-product-read', 'collection-jobs-read', 'collection-main-read',
  'collection-repository-read', 'collection-commit-read', 'collection-tree-read', 'collection-product-list-read', 'collection-checks-read',
  'collection-binding-read', 'collection-environment-read', 'collection-policy-read',
  'binding-attempt-guard', 'binding-identity-guard', 'product-identity-guard', 'binding-freshness-guard',
  'review-validation', 'final-product-read', 'final-binding-read', 'final-execution-guard', 'final-publication',
])
const failures = new WeakMap()
const readKinds = [
  [/^\/pulls\/[0-9]+$/, 'collection-pr-read'],
  [/^\/actions\/runs\/[0-9]+$/, 'collection-product-read'],
  [/^\/actions\/runs\/[0-9]+\/jobs\?filter=latest&per_page=100&page=[0-9]+$/, 'collection-jobs-read'],
  [/^\/git\/ref\/heads\/main$/, 'collection-main-read'],
  [/^\/git\/commits\/[0-9a-f]{40}$/, 'collection-commit-read'],
  [/^\/git\/trees\/[0-9a-f]{40}\?recursive=1$/, 'collection-tree-read'],
  [/^\/actions\/workflows\/ci.yml\/runs\?event=merge_group&head_sha=[0-9a-f]{40}&per_page=100$/, 'collection-product-list-read'],
  [/^\/check-suites\/[0-9]+\/check-runs\?filter=latest&per_page=100$/, 'collection-checks-read'],
  [/^\/environments\/[a-z-]+\/deployment-branch-policies$/, 'collection-policy-read'],
  [/^\/environments\/[a-z-]+$/, 'collection-environment-read'],
]

export function maintenanceReadCode(path, repositoryRoot) {
  if (path === repositoryRoot) return 'collection-repository-read'
  const relative = typeof path === 'string' && path.startsWith(`${repositoryRoot}/`) ? path.slice(repositoryRoot.length) : ''
  return readKinds.find(([pattern]) => pattern.test(relative))?.[1] ?? 'collection-rest-read'
}

export function maintenanceFailure(code, error) {
  const failure = error !== null && (typeof error === 'object' || typeof error === 'function')
    ? error : new Error('Maintenance operation failed closed.')
  if (!failures.has(failure)) failures.set(failure, codes.has(code) ? code : 'unclassified')
  return failure
}

export async function maintenanceStage(code, operation) {
  try { return await operation() } catch (error) { throw maintenanceFailure(code, error) }
}

export function maintenanceFailureCode(error) {
  return error !== null && (typeof error === 'object' || typeof error === 'function')
    ? failures.get(error) ?? 'unclassified' : 'unclassified'
}
