import { test } from 'node:test'
import assert from 'node:assert/strict'
import { deflateRawSync } from 'node:zlib'
import { listZipEntries, readZipEntry, readSingleJsonFromZip, readNamedJsonFromZip, ZipParseError } from '../lib/zip.mjs'

function crc32(buffer) {
  let crc = 0xffffffff
  for (const byte of buffer) {
    crc ^= byte
    for (let bit = 0; bit < 8; bit += 1) crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1))
  }
  return (crc ^ 0xffffffff) >>> 0
}

function buildZip(files, { method = 8 } = {}) {
  const localParts = []
  const centralParts = []
  let offset = 0
  for (const [name, content] of files) {
    const data = method === 8 ? deflateRawSync(Buffer.from(content, 'utf8')) : Buffer.from(content, 'utf8')
    const nameBuffer = Buffer.from(name, 'utf8')
    const local = Buffer.alloc(30)
    local.writeUInt32LE(0x04034b50, 0)
    local.writeUInt16LE(20, 4)
    local.writeUInt16LE(0, 6)
    local.writeUInt16LE(method, 8)
    local.writeUInt32LE(crc32(Buffer.from(content, 'utf8')), 14)
    local.writeUInt32LE(data.length, 18)
    local.writeUInt32LE(Buffer.byteLength(content, 'utf8'), 22)
    local.writeUInt16LE(nameBuffer.length, 26)
    local.writeUInt16LE(0, 28)
    localParts.push(local, nameBuffer, data)

    const central = Buffer.alloc(46)
    central.writeUInt32LE(0x02014b50, 0)
    central.writeUInt16LE(20, 4)
    central.writeUInt16LE(20, 6)
    central.writeUInt16LE(0, 8)
    central.writeUInt16LE(method, 10)
    central.writeUInt32LE(crc32(Buffer.from(content, 'utf8')), 16)
    central.writeUInt32LE(data.length, 20)
    central.writeUInt32LE(Buffer.byteLength(content, 'utf8'), 24)
    central.writeUInt16LE(nameBuffer.length, 28)
    central.writeUInt32LE(offset, 42)
    centralParts.push(central, nameBuffer)
    offset += 30 + nameBuffer.length + data.length
  }
  const local = Buffer.concat(localParts)
  const central = Buffer.concat(centralParts)
  const eocd = Buffer.alloc(22)
  eocd.writeUInt32LE(0x06054b50, 0)
  eocd.writeUInt16LE(files.length, 8)
  eocd.writeUInt16LE(files.length, 10)
  eocd.writeUInt32LE(central.length, 12)
  eocd.writeUInt32LE(local.length, 16)
  return Buffer.concat([local, central, eocd])
}

test('the zip reader extracts a deflate artifact entry', () => {
  const zip = buildZip([['run-metrics.json', '{"schemaVersion":"aerolink-ci-run/v2"}']])
  const entries = listZipEntries(zip)
  assert.equal(entries.length, 1)
  assert.equal(entries[0].name, 'run-metrics.json')
  assert.equal(readZipEntry(zip, entries[0]).toString('utf8'), '{"schemaVersion":"aerolink-ci-run/v2"}')
  assert.deepEqual(readSingleJsonFromZip(zip), { schemaVersion: 'aerolink-ci-run/v2' })
})

test('the zip reader supports stored entries and rejects unsupported methods', () => {
  const stored = buildZip([['a.json', '{"a":1}']], { method: 0 })
  assert.equal(readSingleJsonFromZip(stored).a, 1)

  const bogus = buildZip([['a.json', 'x']])
  const entries = listZipEntries(bogus)
  entries[0].method = 99
  assert.throws(() => readZipEntry(bogus, entries[0]), /unsupported compression method/)
})

test('the zip reader refuses malformed, oversized, or multi-json archives', () => {
  assert.throws(() => listZipEntries(Buffer.from('not a zip')), ZipParseError)
  assert.throws(() => readSingleJsonFromZip(Buffer.alloc(0)), ZipParseError)
  const multi = buildZip([['a.json', '{"a":1}'], ['b.json', '{"b":2}']])
  assert.throws(() => readSingleJsonFromZip(multi), /Expected exactly one JSON file/)
  const nonJson = buildZip([['notes.txt', 'hello']])
  assert.throws(() => readSingleJsonFromZip(nonJson), /Expected exactly one JSON file/)
  const huge = buildZip([['a.json', 'x'.repeat(11 * 1024 * 1024)]])
  assert.throws(() => readSingleJsonFromZip(huge), /bounded size/)
})

test('a named read finds its file in an artifact that holds a directory of outputs', () => {
  // The exact shape that broke the rolling collector: `ci-metrics-run-*` uploads an output directory, and
  // tested-tree provenance began writing `validated-tree.json` beside the merged report. Two JSON files made
  // "the only JSON" unanswerable, and 40 of 42 runs in the window were discarded as unreadable.
  const artifact = buildZip([
    ['run-metrics.json', '{"schemaVersion":"aerolink-ci-run/v2"}'],
    ['validated-tree.json', '{"tree":"deadbeef"}'],
    ['run-metrics.md', '# report'],
  ])
  assert.throws(() => readSingleJsonFromZip(artifact), /Expected exactly one JSON file/)
  assert.equal(readNamedJsonFromZip(artifact, 'run-metrics.json').schemaVersion, 'aerolink-ci-run/v2')
  assert.equal(readNamedJsonFromZip(artifact, 'validated-tree.json').tree, 'deadbeef')

  // A nested upload path still resolves by file name.
  const nested = buildZip([['out/run-metrics.json', '{"a":1}']])
  assert.equal(readNamedJsonFromZip(nested, 'run-metrics.json').a, 1)

  // Absent is absent: it names what it wanted and what was there, rather than silently taking another file.
  const wrong = buildZip([['validated-tree.json', '{"tree":"x"}']])
  assert.throws(() => readNamedJsonFromZip(wrong, 'run-metrics.json'), /does not contain "run-metrics.json"/)
  assert.throws(() => readNamedJsonFromZip(wrong, 'run-metrics.json'), /validated-tree.json/)

  // Malformed content is still a parse failure, not a silent skip.
  const broken = buildZip([['run-metrics.json', '{not json']])
  assert.throws(() => readNamedJsonFromZip(broken, 'run-metrics.json'), /could not be parsed/)
})

test('a named read refuses to choose between candidates by position', () => {
  // A root entry is the unambiguous answer even when a stale copy is nested beside it.
  const withBackup = buildZip([
    ['backup/run-metrics.json', '{"which":"stale"}'],
    ['run-metrics.json', '{"which":"root"}'],
  ])
  assert.equal(readNamedJsonFromZip(withBackup, 'run-metrics.json').which, 'root')

  // Duplicate central-directory entries: refused rather than resolved by listing order.
  const duplicated = buildZip([
    ['run-metrics.json', '{"which":"first"}'],
    ['run-metrics.json', '{"which":"second"}'],
  ])
  assert.throws(() => readNamedJsonFromZip(duplicated, 'run-metrics.json'), /contains 2 entries named/)

  // Two nested copies and no root entry: also refused, for the same reason.
  const twoNested = buildZip([
    ['a/run-metrics.json', '{"which":"a"}'],
    ['b/run-metrics.json', '{"which":"b"}'],
  ])
  assert.throws(() => readNamedJsonFromZip(twoNested, 'run-metrics.json'), /2 nested copies/)
})

test('a named read bounds its own diagnostic', () => {
  // A ZIP name may be 65,535 bytes. The message is stored in the collector's `missing` list and copied into
  // Markdown before the report's size cap applies, so an unbounded diagnostic lets one malformed artifact
  // abort the scheduled collector instead of being recorded as a single unreadable run.
  const longName = `${'n'.repeat(60_000)}.json`
  const hostile = buildZip([[longName, '{"a":1}'], ['other.json', '{"b":2}']])
  let message = ''
  try {
    readNamedJsonFromZip(hostile, 'run-metrics.json')
  } catch (error) {
    message = error.message
  }
  assert.match(message, /does not contain "run-metrics.json"/)
  assert.ok(message.length < 700, `diagnostic was ${message.length} characters`)
  assert.ok(!message.includes('n'.repeat(200)), 'the long entry name was not truncated')

  // Many entries are summarised rather than listed in full.
  const many = buildZip(Array.from({ length: 40 }, (_, i) => [`file-${i}.json`, '{}']))
  let manyMessage = ''
  try {
    readNamedJsonFromZip(many, 'run-metrics.json')
  } catch (error) {
    manyMessage = error.message
  }
  assert.match(manyMessage, /\+30 more/)
  assert.ok(manyMessage.length < 700, `diagnostic was ${manyMessage.length} characters`)
})
