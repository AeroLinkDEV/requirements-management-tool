import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'

// The version is a stable contract label; the content hash identifies the exact planner implementation.
// Keeping both in local output and CI summaries makes a copied plan auditable without trusting a branch name.
export const PLANNER_VERSION = 'aerolink-test-planner/v1'

export const PLANNER_FILES = [
  'product/test-planner/lib/planner-meta.mjs',
  'product/test-planner/lib/classify.mjs',
  'product/test-planner/lib/workflow-jobs.mjs',
  'product/test-planner/tools/classify-ci.mjs',
  'product/test-planner/tools/plan.mjs',
]

export function plannerHash(repoRoot) {
  const hash = createHash('sha256')
  for (const relativePath of PLANNER_FILES) {
    hash.update(relativePath)
    hash.update('\0')
    hash.update(readFileSync(join(repoRoot, relativePath)))
    hash.update('\0')
  }
  return hash.digest('hex')
}
