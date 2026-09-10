// Minimal ZIP reader for GitHub Actions artifact archives.
//
// The rolling collector downloads `ci-metrics-run-*` artifacts from the REST API. Those artifacts are
// standard DEFLATE zips produced by actions/upload-artifact. Node has no built-in zip reader, so this
// module parses the end-of-central-directory and central directory records, then inflates the selected
// entry with zlib. Only the fields needed for bounded, validated extraction are read; nothing is ever
// executed or written to disk.

import { inflateRawSync } from 'node:zlib'

export class ZipParseError extends Error {}

const MAX_ZIP_BYTES = 50 * 1024 * 1024
const MAX_ENTRY_BYTES = 10 * 1024 * 1024
const MAX_ENTRIES = 1000

function u16(buffer, offset) {
  return buffer.readUInt16LE(offset)
}

function u32(buffer, offset) {
  return buffer.readUInt32LE(offset)
}

export function listZipEntries(input) {
  if (!Buffer.isBuffer(input)) throw new ZipParseError('ZIP input must be a Buffer.')
  if (input.length > MAX_ZIP_BYTES) throw new ZipParseError('ZIP archive exceeds the bounded size.')
  if (input.length < 22) throw new ZipParseError('ZIP archive is too small to contain an end record.')

  // End of central directory: scan backwards for the signature (PK\x05\x06).
  let eocd = -1
  const tail = Math.min(input.length - 22, 65535 + 22)
  for (let offset = input.length - 22; offset >= input.length - tail; offset -= 1) {
    if (input.readUInt32LE(offset) === 0x06054b50) {
      eocd = offset
      break
    }
  }
  if (eocd < 0) throw new ZipParseError('ZIP archive has no end-of-central-directory record.')

  const entryCount = u16(input, eocd + 10)
  const centralOffset = u32(input, eocd + 16)
  if (entryCount > MAX_ENTRIES) throw new ZipParseError('ZIP archive has too many entries.')
  if (centralOffset + entryCount * 46 > input.length) throw new ZipParseError('ZIP central directory is out of bounds.')

  const entries = []
  let cursor = centralOffset
  for (let index = 0; index < entryCount; index += 1) {
    if (input.readUInt32LE(cursor) !== 0x02014b50) throw new ZipParseError('ZIP central directory record is malformed.')
    const method = u16(input, cursor + 10)
    const compressedSize = u32(input, cursor + 20)
    const uncompressedSize = u32(input, cursor + 24)
    const nameLength = u16(input, cursor + 28)
    const extraLength = u16(input, cursor + 30)
    const commentLength = u16(input, cursor + 32)
    const localOffset = u32(input, cursor + 42)
    const name = input.toString('utf8', cursor + 46, cursor + 46 + nameLength)
    if (uncompressedSize > MAX_ENTRY_BYTES) throw new ZipParseError(`ZIP entry "${name}" exceeds the bounded size.`)
    entries.push({ name, method, compressedSize, uncompressedSize, localOffset })
    cursor += 46 + nameLength + extraLength + commentLength
  }
  return entries
}

export function readZipEntry(input, entry) {
  if (!Buffer.isBuffer(input)) throw new ZipParseError('ZIP input must be a Buffer.')
  if (entry.method !== 0 && entry.method !== 8) throw new ZipParseError(`ZIP entry "${entry.name}" uses an unsupported compression method.`)
  if (entry.localOffset + 30 > input.length) throw new ZipParseError(`ZIP entry "${entry.name}" local header is out of bounds.`)
  const nameLength = u16(input, entry.localOffset + 26)
  const extraLength = u16(input, entry.localOffset + 28)
  const dataStart = entry.localOffset + 30 + nameLength + extraLength
  if (dataStart + entry.compressedSize > input.length) throw new ZipParseError(`ZIP entry "${entry.name}" data is out of bounds.`)
  const data = input.subarray(dataStart, dataStart + entry.compressedSize)
  if (entry.method === 0) {
    if (data.length > MAX_ENTRY_BYTES) throw new ZipParseError(`ZIP entry "${entry.name}" exceeds the bounded output size.`)
    if (data.length !== entry.uncompressedSize) throw new ZipParseError(`ZIP entry "${entry.name}" stored size does not match the record.`)
    return Buffer.from(data)
  }
  try {
    // The central-directory size is untrusted and may understate a compression bomb. Keep zlib's output
    // allocation bounded while inflating, then retain the exact-size check for honest archives.
    const inflated = inflateRawSync(data, { maxOutputLength: MAX_ENTRY_BYTES })
    if (inflated.length !== entry.uncompressedSize) throw new ZipParseError(`ZIP entry "${entry.name}" inflated size does not match the record.`)
    return inflated
  } catch (error) {
    if (error instanceof ZipParseError) throw error
    throw new ZipParseError(`ZIP entry "${entry.name}" could not be inflated within the bounded output size.`)
  }
}

function parseJsonEntry(input, entry) {
  const content = readZipEntry(input, entry)
  try {
    return JSON.parse(content.toString('utf8'))
  } catch {
    throw new ZipParseError('Artifact JSON could not be parsed.')
  }
}

const jsonEntriesOf = (input) =>
  listZipEntries(input).filter((entry) => entry.name.endsWith('.json') && !entry.name.endsWith('/'))

export function readSingleJsonFromZip(input) {
  const jsonEntries = jsonEntriesOf(input)
  if (jsonEntries.length !== 1) {
    throw new ZipParseError(`Expected exactly one JSON file in the artifact zip, found ${jsonEntries.length}.`)
  }
  return parseJsonEntry(input, jsonEntries[0])
}

/**
 * Reads a named file from an artifact that holds a directory of outputs.
 *
 * `readSingleJsonFromZip` assumes an artifact carries exactly one JSON, which is only true while nothing else
 * writes beside it. `ci-metrics-run-*` uploads a whole output directory, and once the tested-tree provenance
 * work began writing `validated-tree.json` into that same directory every run's artifact held two JSON files
 * and the rolling collector rejected all of them — 40 of 42 unreadable runs in the window that exposed this.
 *
 * A consumer that knows which file it wants should ask for it by name rather than depend on being the only
 * writer, which is a property no shared output directory keeps for long.
 */
/** A ZIP name is bounded at 65,535 bytes, so every name reaching a message is truncated before it gets there. */
const MAX_DIAGNOSTIC_NAME = 80
const MAX_DIAGNOSTIC_NAMES = 10
const MAX_DIAGNOSTIC_LENGTH = 400

const clip = (value, limit) => (value.length > limit ? `${value.slice(0, limit)}…` : value)

function describeEntries(jsonEntries) {
  if (jsonEntries.length === 0) return 'none'
  const shown = jsonEntries.slice(0, MAX_DIAGNOSTIC_NAMES).map((entry) => clip(entry.name, MAX_DIAGNOSTIC_NAME))
  const suffix = jsonEntries.length > MAX_DIAGNOSTIC_NAMES ? `, +${jsonEntries.length - MAX_DIAGNOSTIC_NAMES} more` : ''
  return clip(`${shown.join(', ')}${suffix}`, MAX_DIAGNOSTIC_LENGTH)
}

export function readNamedJsonFromZip(input, fileName) {
  const jsonEntries = jsonEntriesOf(input)
  // An exact root entry is the unambiguous answer. Only when there is none does a single nested copy stand in,
  // and anything ambiguous is refused rather than resolved by position: `.find()` would have taken whichever
  // matching basename the central directory happened to list first, so a stale `backup/run-metrics.json` or a
  // duplicate entry could be read as the record. These artifacts are untrusted input, and an order-dependent
  // choice among several valid-looking candidates is exactly the kind of silent wrong answer that is
  // indistinguishable from a right one.
  const rootMatches = jsonEntries.filter((entry) => entry.name === fileName)
  const nestedMatches = jsonEntries.filter((entry) => entry.name.endsWith(`/${fileName}`))

  if (rootMatches.length > 1) {
    throw new ZipParseError(`Artifact zip contains ${rootMatches.length} entries named "${fileName}". JSON entries: ${describeEntries(jsonEntries)}.`)
  }
  if (rootMatches.length === 1) return parseJsonEntry(input, rootMatches[0])

  if (nestedMatches.length > 1) {
    throw new ZipParseError(`Artifact zip contains ${nestedMatches.length} nested copies of "${fileName}" and no root entry. JSON entries: ${describeEntries(jsonEntries)}.`)
  }
  if (nestedMatches.length === 1) return parseJsonEntry(input, nestedMatches[0])

  throw new ZipParseError(`Artifact zip does not contain "${fileName}". JSON entries: ${describeEntries(jsonEntries)}.`)
}

/**
 * Read one bounded text entry from an artifact upload using the same unambiguous root/nested lookup as JSON.
 * Artifact entries are data only: callers receive bytes and decide how to parse them. No file is extracted
 * or executed on disk.
 */
export function readNamedEntryFromZip(input, fileName) {
  if (typeof fileName !== 'string' || fileName.length === 0 || fileName.length > 200 || /[\r\n]/.test(fileName)) {
    throw new ZipParseError('Artifact entry name is invalid.')
  }
  const entries = listZipEntries(input).filter((entry) => entry.name === fileName || entry.name.endsWith(`/${fileName}`))
  const root = entries.filter((entry) => entry.name === fileName)
  const nested = entries.filter((entry) => entry.name.endsWith(`/${fileName}`))
  if (root.length > 1 || (root.length === 0 && nested.length > 1)) {
    throw new ZipParseError(`Artifact zip contains ambiguous entries named "${fileName}".`)
  }
  const selected = root[0] ?? nested[0]
  if (!selected) throw new ZipParseError(`Artifact zip does not contain "${fileName}".`)
  return readZipEntry(input, selected)
}
