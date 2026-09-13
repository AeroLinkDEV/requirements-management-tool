import { expect, test } from '@playwright/test'
import {
  decodeRepositoryObservation,
  decodeRepositoryResponse,
  repositoryStatusDescription,
  repositoryStatusLabel,
} from '../src/repositoryConfiguration'

test('repository configuration decoder preserves server identity and status evidence', () => {
  const decoded = decodeRepositoryResponse({
    canManage: true,
    repository: {
      projectId: 'project-a',
      mode: 'ConnectNow',
      status: 'Verified',
      provider: 'GitLab',
      endpoint: 'https://gitlab.example/aerolink/nav',
      version: 4,
      configuredBy: 'manager@example.test',
      configuredAt: '2026-09-13T20:00:00Z',
      lastVerifiedAt: '2026-09-13T20:02:00Z',
      lastVerifiedBy: 'manager@example.test',
      remoteProjectId: 123,
      remotePath: 'aerolink/nav',
      lastVerificationFailureAt: null,
      lastVerificationFailureBy: null,
    },
  })

  expect(decoded).toMatchObject({ canManage: true })
  expect(decoded?.repository).toMatchObject({
    projectId: 'project-a',
    mode: 'ConnectNow',
    status: 'Verified',
    version: 4,
    remoteProjectId: 123,
    remotePath: 'aerolink/nav',
  })
})

test('repository configuration decoder fails closed for invalid status, version, or identity', () => {
  const base = {
    projectId: 'project-a',
    mode: 'ConfigureLater',
    status: 'Pending',
    provider: null,
    endpoint: null,
    version: 1,
  }

  expect(decodeRepositoryResponse({ canManage: true, repository: { ...base, status: 'Ready' } })).toBeUndefined()
  expect(decodeRepositoryResponse({ canManage: true, repository: { ...base, version: 0 } })).toBeUndefined()
  expect(decodeRepositoryResponse({ canManage: true, repository: { ...base, projectId: '' } })).toBeUndefined()
  expect(decodeRepositoryResponse({ canManage: 'yes', repository: null })).toBeUndefined()
})

test('repository status wording keeps deferred and unverified states truthful', () => {
  expect(repositoryStatusLabel('Pending')).toBe('Pending')
  expect(repositoryStatusDescription('Pending')).toContain('Unrelated project work can continue')
  expect(repositoryStatusLabel('ConfiguredUnverified')).toBe('Configured · unverified')
  expect(repositoryStatusDescription('ConfiguredUnverified')).toContain('no successful server verification')
  expect(repositoryStatusLabel('Verified')).toBe('Verified')
  expect(repositoryStatusDescription('Verified')).toContain('server observed')
})

test('repository verification observation is accepted only when its server result is typed', () => {
  expect(decodeRepositoryObservation({ verified: true, code: 'verified', detail: 'ok', remoteProjectId: 7, remotePath: 'group/project' }))
    .toMatchObject({ verified: true, remoteProjectId: 7, remotePath: 'group/project' })
  expect(decodeRepositoryObservation({ verified: false, code: 'unauthorized', detail: 'no access', remoteProjectId: null, remotePath: null }))
    .toMatchObject({ verified: false, code: 'unauthorized' })
  expect(decodeRepositoryObservation({ verified: 'yes' })).toBeUndefined()
  expect(decodeRepositoryObservation({ verified: true, remoteProjectId: '7' })).toBeUndefined()
})
