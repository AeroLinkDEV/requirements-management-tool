import { createHash } from 'node:crypto'
import { existsSync, lstatSync, readdirSync, readFileSync } from 'node:fs'
import { join, relative } from 'node:path'

// Preserve the original operator-family proof: names, sizes, modification times and content hashes must be
// identical, including the distinction between an absent evidence store and an empty one.
export function snapshotEvidence(root) {
  if (!existsSync(root)) return ['<absent>']
  const entries = []
  function visit(current) {
    for (const entry of readdirSync(current, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
      const absolute = join(current, entry.name)
      const name = relative(root, absolute)
      if (entry.isDirectory()) {
        entries.push(`${name}/`)
        visit(absolute)
      } else {
        const stat = lstatSync(absolute)
        const digest = entry.isFile() ? createHash('sha256').update(readFileSync(absolute)).digest('hex') : 'non-file'
        entries.push(`${name}|${stat.size}|${stat.mtimeMs}|${digest}`)
      }
    }
  }
  visit(root)
  return entries
}
