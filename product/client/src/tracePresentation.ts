import { stateLabel } from './presentation'

/** Human wording for the server projection's provenance facts. The fact kind remains server-owned. */
export const traceProvenanceLabel = (kind: string) => {
  if (kind === 'AssessmentDerived') return 'From downstream assessment'
  if (kind === 'AuthorStated') return 'Author-stated relationship'
  if (kind === 'TcrOrigin' || kind === 'TcrAdditionalSource') return 'From controlled test-change work'
  if (kind === 'RequirementRevisionSource') return 'Source of materialized revision'
  if (kind === 'RequirementTrace') return 'Requirement trace'
  if (kind === 'CodeTraceabilityRecord') return 'Code traceability record'
  if (kind.endsWith('Origin')) return 'From controlled verification origin'
  return stateLabel(kind)
}

export const traceKindLabel = (kind: string) => {
  if (kind === 'ChangeRequest') return 'Change request'
  if (kind === 'TestChangeRequest') return 'Test change request'
  if (kind === 'RequirementRevision') return 'Requirement'
  if (kind === 'CodeTraceability') return 'Code traceability'
  return kind.replace(/([a-z])([A-Z])/g, '$1 $2')
}

/**
 * Human wording for a typed relation, reading along the connector in the story direction — upstream →
 * downstream (#925 V5). The server emits requirement-trace and change-upstream edges already
 * story-directed (#925 F5), so each phrase here was assigned after checking the stored endpoints,
 * never by renaming the stored orientation alone.
 *
 * These are relationship facts only. "Resolved by" does not assert that the problem report is closed,
 * and "verified by" does not assert that the verification passed; lifecycle and execution results
 * remain separate facts rendered beside them.
 */
const storyRelationPhrases: Record<string, string> = {
  // The owner-mandated change-network phrases.
  ProblemReportResolution: 'resolved by',
  Upstream: 'allocates to',
  CoveredByTestChangeRequest: 'verified by',
  // The artifact-thread projection emits its relations already human and story-directed; they pass
  // through verbatim rather than being re-cased by the fallback splitter.
  'allocates to': 'allocates to',
  'source of': 'source of',
  'verified by': 'verified by',
  'resolved by': 'resolved by',
  'run by': 'run by',
  authored: 'authored',
  'evidence for': 'evidence for',
  produced: 'produced',
  'retest of': 'retest of',
}

/** The same relationship phrased from the other endpoint, so an inspector row speaks in the listed record's own direction. */
const inverseStoryRelationPhrases: Record<string, string> = {
  ProblemReportResolution: 'resolves',
  Upstream: 'allocated from',
  CoveredByTestChangeRequest: 'verifies',
  'allocates to': 'allocated from',
  'source of': 'derived from',
  'verified by': 'verifies',
  'resolved by': 'resolves',
  'run by': 'runs',
  authored: 'authored by',
  'evidence for': 'evidenced by',
  produced: 'produced by',
  'retest of': 'retested by',
}

export const traceRelationLabel = (relation: string) =>
  storyRelationPhrases[relation] ?? relation
    .replace(/([a-z])([A-Z])/g, '$1 $2')
    .replace(/^./, first => first.toUpperCase())

/**
 * The phrase for a direct link whose listed record sits at the edge's `from` endpoint
 * (`listedIsSource`) or at its `to` endpoint. A parent listed upstream reads "allocates to"; a child
 * listed downstream reads "allocated from" — one stored relation, each row speaking in its own
 * direction.
 */
export const traceRelationLabelFor = (relation: string, listedIsSource: boolean) =>
  listedIsSource
    ? traceRelationLabel(relation)
    : inverseStoryRelationPhrases[relation] ?? traceRelationLabel(relation)
