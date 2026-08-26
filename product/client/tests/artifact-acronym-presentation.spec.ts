import { expect, test } from '@playwright/test'
import { artifactAcronym, artifactTypeLabel, configuredProcedureTargetsFor, documentTypeLabel, isVerificationProcedureKind, procedureTargetsFor, targetsFor, testChangeRequestAcronym, testChangeReviewWorkflowSubject, verificationArtifactApiRoot, verificationArtifactLevel, verificationArtifactNoun, verificationArtifactRouteKey } from '../src/presentation'

test('numbered artifacts keep their canonical uppercase acronym in presentation', () => {
  const examples = [
    ['SRCR-00076.00', 'SRCR'], ['HLRCR-00087.00', 'HLRCR'], ['LLRCR-00088.00', 'LLRCR'],
    ['SYSR-000008.01', 'SYSR'], ['HLR-000008.01', 'HLR'], ['LLR-000008.01', 'LLR'],
    ['SYSTPCR-000002.00', 'SYSTPCR'], ['HLRTCCR-000003.00', 'HLRTCCR'], ['LLRTCCR-000004.00', 'LLRTCCR'],
    ['SYSTP-000008.00', 'SYSTP'], ['HLRTP-000009.00', 'HLRTP'], ['LLRTP-000010.00', 'LLRTP'],
    ['PR-00001.00', 'PR'], ['SW-01.60', 'SW'],
    ['SYSRD-000016.00', 'SYSRD'], ['HLRD-000016.00', 'HLRD'], ['LLRD-000016.00', 'LLRD'],
    ['SYSTD-000016.00', 'SYSTD'], ['HLRTD-000016.00', 'HLRTD'], ['LLRTD-000016.00', 'LLRTD'],
    ['HLRTPD-000016.00', 'HLRTPD'], ['LLRTPD-000016.00', 'LLRTPD'],
    ['LIB-SYSR-00001.00', 'LIB-SYSR'],
  ] as const

  for (const [identifier, acronym] of examples) expect(artifactAcronym(identifier)).toBe(acronym)
  expect(artifactTypeLabel('change-request', 'SRCR-00076.00')).toBe('System Requirement Change Request (SRCR)')
  expect(artifactTypeLabel('change-request', 'HLRCR-00087.00')).toBe('HLR Change Request (HLRCR)')
  expect(artifactTypeLabel('change-request', 'LLRCR-00088.00')).toBe('LLR Change Request (LLRCR)')
  expect(artifactTypeLabel('requirement', 'HLR-000008.01')).toBe('High-Level Software Requirement (HLR)')
  expect(artifactTypeLabel('test-procedure', 'LLRTP-000010.00')).toBe('LLR Test Procedure (LLRTP)')
})

test('document labels use canonical acronyms while draft remains a separate status', () => {
  expect(documentTypeLabel('Sysrd')).toBe('System Requirements Document (SYSRD)')
  expect(documentTypeLabel('SwrdHighLevel')).toBe('High-Level Software Requirements Document (HLRD)')
  expect(documentTypeLabel('SwrdLowLevel')).toBe('Low-Level Software Requirements Document (LLRD)')
  expect(documentTypeLabel('SystemTestProcedures')).toBe('System Test Procedure Document (SYSTD)')
  expect(documentTypeLabel('HighLevelTestProcedures')).toBe('HLR Test Procedure Document (HLRTPD)')
  expect(documentTypeLabel('LowLevelTestProcedures')).toBe('LLR Test Procedure Document (LLRTPD)')
  expect(targetsFor('Software').map((target) => target.label)).toEqual([
    'High-Level Software Requirements Document (HLRD)',
    'Low-Level Software Requirements Document (LLRD)',
  ])
  expect(targetsFor('Software').every((target) => !target.label.includes('Draft'))).toBeTruthy()
  expect(procedureTargetsFor('Software', undefined, 'Procedure').map((target) => target.type)).toEqual([
    'HighLevelTestProcedures', 'LowLevelTestProcedures',
  ])
  expect(procedureTargetsFor('Software').map((target) => target.type)).toEqual([
    'HighLevelTestCases', 'LowLevelTestCases',
  ])
})

test('composite HLR and LLR Procedure route kinds share the Procedure vocabulary', () => {
  for (const [level, kind, acronym, subject] of [
    ['HighLevel', 'HighLevelProcedure', 'HLRTPCR', 'HighLevelSoftwareProcedure'],
    ['LowLevel', 'LowLevelProcedure', 'LLRTPCR', 'LowLevelSoftwareProcedure'],
  ] as const) {
    expect(isVerificationProcedureKind(kind)).toBeTruthy()
    expect(verificationArtifactNoun(level, kind)).toBe('Procedure')
    expect(verificationArtifactApiRoot('softwareTest', kind)).toBe('/api/test-procedures')
    expect(testChangeRequestAcronym(level, kind)).toBe(acronym)
    expect(testChangeReviewWorkflowSubject(level, kind)).toBe(subject)
    expect(verificationArtifactLevel(kind)).toBe(level)
    expect(verificationArtifactRouteKey(level, 'Procedure')).toBe(kind)
    expect(verificationArtifactRouteKey(level, 'Case')).toBe(level)
  }
})

test('configured verification document targets are filtered by each exact level and kind', () => {
  const ladder = { effectiveSteps: [
    { catalogueEntry: 'System' as const, capabilities: 2, enabledArtifactKinds: ['Procedure'] },
    { catalogueEntry: 'HighLevel' as const, capabilities: 2, enabledArtifactKinds: ['Case', 'Procedure'] },
    { catalogueEntry: 'LowLevel' as const, capabilities: 2, enabledArtifactKinds: ['Case'] },
  ] }
  expect(configuredProcedureTargetsFor(ladder, 'Software').map(target => target.type)).toEqual([
    'HighLevelTestCases', 'LowLevelTestCases', 'HighLevelTestProcedures',
  ])
  expect(configuredProcedureTargetsFor(ladder, 'Software', undefined, 'Procedure')
    .map(target => target.type)).toEqual(['HighLevelTestProcedures'])
  expect(configuredProcedureTargetsFor(ladder, 'Software', 'LowLevel', 'Procedure')).toEqual([])
})
