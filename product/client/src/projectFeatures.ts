import type { Discipline, View } from './routing'

/** The switchable modules of a project (#1113). Command Center and My Work are always present. */
export type ProjectFeature = 'TeamWork' | 'Requirements' | 'Verification' | 'Code' | 'DocumentationCenter' | 'ProblemReports' | 'Release'

export type ProjectFeatureProjection = {
  persisted: boolean
  version: number
  canManage: boolean
  enabled: ProjectFeature[]
  features: { id: ProjectFeature; label: string; enabled: boolean; hasRecords: boolean }[]
  history: { version: number; previous: ProjectFeature[]; enabled: ProjectFeature[]; actor: string; reason: string; occurredAt: string; snapshotHash: string }[]
}

/**
 * Null means the project's features have not loaded; gated surfaces stay hidden until they do, the same
 * fail-closed posture the ladder takes. The server refuses a disabled feature's records regardless.
 */
export function hasFeature(features: ProjectFeature[] | null | undefined, feature: ProjectFeature): boolean {
  return !!features?.includes(feature)
}

const verificationViews: View[] = ['verification', 'testingCoverage', 'testChangeRequests', 'testChangeRequest', 'createTestChangeRequest', 'procedureExplorer', 'testResults']
const requirementViews: View[] = ['history', 'scr', 'createSystemScr', 'createSoftwareChange', 'createInterfaceChange', 'requirements']
const releaseViews: View[] = ['release', 'releaseImpact', 'releaseDecision', 'releaseOperations', 'baselines', 'planning']

/** Whether a workspace view belongs to a feature this project has switched on. The one rule for nav, palette and routes. */
export function viewEnabled(features: ProjectFeature[] | null | undefined, view: View, discipline?: Discipline): boolean {
  if (view === 'teamwork') return hasFeature(features, 'TeamWork')
  if (requirementViews.includes(view)) return hasFeature(features, 'Requirements')
  if (verificationViews.includes(view)) return hasFeature(features, 'Verification')
  if (view === 'documents') return hasFeature(features, discipline === 'systemTest' || discipline === 'softwareTest' ? 'Verification' : 'Requirements')
  if (view === 'code' || view === 'codeMergeRequests' || view === 'codeExplorer') return hasFeature(features, 'Code')
  if (view === 'managedDocuments') return hasFeature(features, 'DocumentationCenter')
  if (view === 'problemReports') return hasFeature(features, 'ProblemReports')
  if (releaseViews.includes(view)) return hasFeature(features, 'Release')
  // The Digital Thread traces requirements and verification; it has nothing to show without either.
  if (view === 'lifecycle') return hasFeature(features, 'Requirements') || hasFeature(features, 'Verification')
  return true
}

/** Every switchable feature: what a project with no stored feature set has. */
export const ALL_FEATURES: ProjectFeature[] = ['TeamWork', 'Requirements', 'Verification', 'Code', 'DocumentationCenter', 'ProblemReports', 'Release']
