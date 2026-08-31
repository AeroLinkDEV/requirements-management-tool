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

export const traceRelationLabel = (relation: string) => relation
  .replace(/([a-z])([A-Z])/g, '$1 $2')
  .replace(/^./, first => first.toUpperCase())
