import { useEffect, useState } from 'react'
import { apiRequest } from './apiClient'
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

export const FEATURE_LABELS: Record<ProjectFeature, string> = {
  TeamWork: 'Team Work',
  Requirements: 'Requirements',
  Verification: 'Verification',
  Code: 'Code',
  DocumentationCenter: 'Documentation Center',
  ProblemReports: 'Problem Reports',
  Release: 'Release',
}

export const FEATURE_DESCRIPTIONS: Record<ProjectFeature, string> = {
  TeamWork: 'Project-wide lifecycle board of who holds which work.',
  Requirements: 'System and software requirements, their change requests and generated documents.',
  Verification: 'Test cases, procedures, coverage, results and test change requests.',
  Code: 'Merge requests, the code explorer and code-to-requirement evidence.',
  DocumentationCenter: 'Controlled Word documents authored outside AeroLink.',
  ProblemReports: 'Problem Reports through SCCB, implementation, verification and SQA closure.',
  Release: 'Release readiness, release campaigns and configuration baselines.',
}

/**
 * Mirrors the server rule (DEC-136, DEC-144) so a page explains a refusal before it is sent; the server still
 * decides. Verification may stand without Requirements: its cases are then standalone.
 */
export function featureDependencyNote(enabled: ReadonlySet<ProjectFeature>): string | null {
  if (enabled.has('Code') && !enabled.has('Requirements')) return 'Code needs Requirements: code is traced to the requirements it implements.'
  return null
}

/**
 * Whether a project uses Requirements, for pages that author verification (DEC-144). Null until known. An
 * unreadable answer is "yes", the default for a project with no stored set; the server still decides.
 */
export function useRequirementsInUse(api: string, projectId: string): boolean | null {
  const [inUse, setInUse] = useState<boolean | null>(null)
  useEffect(() => {
    let current = true
    setInUse(null)
    apiRequest<ProjectFeatureProjection>(`${api}/api/projects/${projectId}/features`)
      .then(next => { if (current) setInUse(!Array.isArray(next?.enabled) || next.enabled.includes('Requirements')) })
      .catch(() => { if (current) setInUse(true) })
    return () => { current = false }
  }, [api, projectId])
  return inUse
}

/**
 * The features an inherited setup keeps (#1113): inception brings requirements, verification procedures and an
 * inception baseline, and the server refuses records for a feature that is off.
 */
export const INHERITED_START_FEATURES: ProjectFeature[] = ['Requirements', 'Verification', 'Release']
