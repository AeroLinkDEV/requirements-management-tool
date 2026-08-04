import { expect, test } from '@playwright/test'
import { artifactAcronym, artifactTypeLabel, documentTypeLabel, targetsFor } from '../src/presentation'

test('numbered artifacts keep their canonical uppercase acronym in presentation', () => {
  const examples = [
    ['SRCR-00076.00', 'SRCR'], ['HLRCR-00087.00', 'HLRCR'], ['LLRCR-00088.00', 'LLRCR'],
    ['SYSR-000008.01', 'SYSR'], ['HLR-000008.01', 'HLR'], ['LLR-000008.01', 'LLR'],
    ['SYSTCR-000002.00', 'SYSTCR'], ['HLRTCR-000003.00', 'HLRTCR'], ['LLRTCR-000004.00', 'LLRTCR'],
    ['SYSTP-000008.00', 'SYSTP'], ['HLRTP-000009.00', 'HLRTP'], ['LLRTP-000010.00', 'LLRTP'],
    ['PR-00001.00', 'PR'], ['SW-01.60', 'SW'],
    ['SYSRD-000016.00', 'SYSRD'], ['HLRD-000016.00', 'HLRD'], ['LLRD-000016.00', 'LLRD'],
    ['SYSTD-000016.00', 'SYSTD'], ['HLRTD-000016.00', 'HLRTD'], ['LLRTD-000016.00', 'LLRTD'],
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
  expect(documentTypeLabel('HighLevelTestProcedures')).toBe('HLR Test Procedure Document (HLRTD)')
  expect(documentTypeLabel('LowLevelTestProcedures')).toBe('LLR Test Procedure Document (LLRTD)')
  expect(targetsFor('Software').map((target) => target.label)).toEqual([
    'High-Level Software Requirements Document (HLRD)',
    'Low-Level Software Requirements Document (LLRD)',
  ])
  expect(targetsFor('Software').every((target) => !target.label.includes('Draft'))).toBeTruthy()
})
