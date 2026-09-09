import { createHash } from 'node:crypto'
import { lstatSync, readdirSync, readFileSync, realpathSync } from 'node:fs'
import { basename, dirname, join, relative, resolve } from 'node:path'

function isOutside(root, destination) {
  const relation = relative(root, destination)
  return relation === '..' || relation.startsWith(`..${process.platform === 'win32' ? '\\' : '/'}`) || relation.startsWith('/') || /^[A-Za-z]:[\\/]/.test(relation)
}

// Resolve the deepest existing ancestor before appending missing components. This follows junctions/symlinks
// for an existing parent while still allowing a new snapshot leaf. Inspection errors other than a genuinely
// missing path fail closed so an inaccessible parent is never mistaken for a safe destination.
export function canonicalizePath(target) {
  const absolute = resolve(target)
  const missing = []
  let current = absolute
  while (true) {
    try {
      lstatSync(current)
      break
    } catch (error) {
      if (error?.code !== 'ENOENT' && error?.code !== 'ENOTDIR') {
        throw new Error(`Path could not be inspected: ${absolute}`)
      }
      const parent = dirname(current)
      if (parent === current) throw new Error(`Path could not be canonicalized: ${absolute}`)
      missing.unshift(basename(current))
      current = parent
    }
  }
  let canonical
  try {
    canonical = realpathSync.native(current)
  } catch {
    throw new Error(`Path could not be canonicalized: ${absolute}`)
  }
  return missing.reduce((parent, child) => join(parent, child), canonical)
}

export function assertDestinationOutsideRoot(root, destination) {
  const canonicalRoot = canonicalizePath(root)
  const canonicalDestination = canonicalizePath(destination)
  if (!isOutside(canonicalRoot, canonicalDestination)) {
    throw new Error('Snapshot destination must not write the evidence store being protected')
  }
  return { canonicalRoot, canonicalDestination }
}

// Preserve the original operator-family proof: names, sizes, modification times and content hashes must be
// identical, including the distinction between an absent evidence store and an empty one.
export function snapshotEvidence(root) {
  let rootStat
  try {
    rootStat = lstatSync(root)
  } catch (error) {
    if (error?.code === 'ENOENT' || error?.code === 'ENOTDIR') return ['<absent>']
    throw new Error(`Evidence root could not be inspected: ${root}`)
  }
  if (!rootStat.isDirectory()) throw new Error('Persistent evidence root was not a directory')
  const entries = []
  function visit(current) {
    const stat = lstatSync(current)
    const name = relative(root, current) || '<root>'
    entries.push(`${name}|D|${stat.size}|${stat.birthtimeMs}|${stat.mtimeMs}|${stat.mode}`)
    for (const entry of readdirSync(current, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
      const absolute = join(current, entry.name)
      if (entry.isDirectory()) {
        visit(absolute)
      } else {
        const stat = lstatSync(absolute)
        const entryName = relative(root, absolute)
        const digest = entry.isFile() ? createHash('sha256').update(readFileSync(absolute)).digest('hex') : 'non-file'
        entries.push(`${entryName}|${stat.size}|${stat.mtimeMs}|${digest}`)
      }
    }
  }
  visit(root)
  return entries
}
