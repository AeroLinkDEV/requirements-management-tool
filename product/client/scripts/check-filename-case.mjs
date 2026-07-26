/**
 * Fails when two files in the client can be reached by one import specifier on a case-insensitive filesystem.
 *
 * Linux resolves `./richContent` and `./RichContent` to two different modules; Windows and macOS resolve both
 * to whichever file they find first. A pair whose names differ only in case therefore compiles on the machine
 * it was written on and fails everywhere the product is actually deployed — which is exactly what happened:
 * `RichContent.tsx` and `richContent.ts` shipped as separate modules, and the client could not be typechecked
 * or built on Windows at all. Nothing caught it, because every check that would have was running on Linux.
 *
 * Note that the two names collide only once the extension is removed. Comparing whole filenames finds nothing,
 * because `.tsx` and `.ts` differ — the ambiguity is in the specifier `./richContent`, which a resolver
 * completes by trying each module extension in turn. So the stems are what must be compared.
 *
 * This runs on every platform as part of `npm run lint`, so the mistake is reported on the machine that
 * introduces it — which can only be a case-sensitive one — rather than on the machine that cannot build.
 */
import { readdirSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { relative, join, extname } from 'node:path'

const skip = new Set(['node_modules', 'dist', 'test-results', 'playwright-report', '.git'])

// The extensions a resolver will append to a specifier that does not carry one.
const moduleExtensions = new Set(['.ts', '.tsx', '.js', '.jsx', '.mjs', '.cjs'])

/**
 * The colliding pairs among the entries of one directory. Only entries of a single directory can collide,
 * because a path is resolved one segment at a time.
 *
 * Two rules, because there are two ways to reach the wrong file:
 *  - full names equal but for case, which makes the two indistinguishable as paths; and
 *  - module stems equal but for case, which makes an extensionless specifier ambiguous.
 *
 * A directory carries its own name as a stem, since `./foo` may resolve to `foo/index.ts`.
 *
 * Exported so it can be exercised directly. A case collision cannot be created on a case-insensitive
 * filesystem, so handing this the names is the only way to test it on Windows.
 */
export function collisionsAmong(entries) {
  const pairs = []
  const named = new Map()
  const stemmed = new Map()

  // `compared` is the text the rule is about — the whole name, or the stem. Two entries collide only when
  // their compared text differs while matching case-insensitively; when it is identical the pair is
  // unambiguous everywhere, which is how `foo.ts` alongside `foo.tsx` stays legal.
  const record = (index, compared, name, reason) => {
    const first = index.get(compared.toLowerCase())
    if (first === undefined) index.set(compared.toLowerCase(), { compared, name })
    else if (first.compared !== compared) pairs.push([first.name, name, reason])
  }

  for (const entry of entries) {
    const name = typeof entry === 'string' ? entry : entry.name
    const isDirectory = typeof entry === 'string' ? false : Boolean(entry.isDirectory?.())
    record(named, name, name, 'the names differ only in case')
    const extension = isDirectory ? '' : extname(name)
    if (isDirectory || moduleExtensions.has(extension)) {
      record(stemmed, name.slice(0, name.length - extension.length), name, 'one import specifier resolves to both')
    }
  }
  return pairs
}

function walk(directory, root) {
  const entries = readdirSync(directory, { withFileTypes: true }).filter(entry => !skip.has(entry.name))
  const found = collisionsAmong(entries).map(([first, second, reason]) => [
    relative(root, join(directory, first)),
    relative(root, join(directory, second)),
    reason,
  ])
  for (const entry of entries.filter(entry => entry.isDirectory())) {
    found.push(...walk(join(directory, entry.name), root))
  }
  return found
}

// Report only when run as a command, so a test can import collisionsAmong without exiting the process.
if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const clientDir = fileURLToPath(new URL('..', import.meta.url))
  const collisions = walk(clientDir, clientDir)
  if (collisions.length > 0) {
    console.error('These are one file on Windows and macOS:\n')
    for (const [first, second, reason] of collisions) console.error(`  ${first}\n  ${second}\n  -- ${reason}\n`)
    console.error('Rename one of each pair. See src/RichContent.tsx for why this is a build failure rather than a style preference.')
    process.exit(1)
  }
}
