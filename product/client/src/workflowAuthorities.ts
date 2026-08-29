/**
 * The required authorities new workflow configuration may demand, since the #816 Slice 4 cutover.
 *
 * A stage records two independent facts: which authority must sign (a base project role, or the one
 * accountable Project Leadership position with its standing backup) and what the signature means
 * (Review or Approval, from the stage kind). Generic Reviewer/Approver are absent by decision — they are
 * signature meanings, not authorities — and the same four names appear in both groups on purpose: the base
 * role is the job many people hold, the leadership entry is the accountable position. Each option names its
 * group so a reader can tell them apart.
 */

export const baseRoleAuthorities: readonly string[] = [
  'SystemEngineer',
  'SoftwareEngineer',
  'SystemTestEngineer',
  'SoftwareTestEngineer',
  'ProjectEngineer',
  'ProgramManager',
  'EngineeringManager',
  'ConfigurationManager',
  'SoftwareQualityAnalyst',
  'Airworthiness',
]

export const leadershipAuthorities: readonly string[] = [
  'ProjectEngineer',
  'ProgramManager',
  'EngineeringManager',
  'ConfigurationManager',
  'SystemEngineeringLead',
  'SoftwareEngineeringLead',
  'SystemTestLead',
  'SoftwareTestLead',
]

export const authorityRoleLabels: Record<string, string> = {
  SystemEngineer: 'System Engineer',
  SoftwareEngineer: 'Software Engineer',
  SystemTestEngineer: 'System Test Engineer',
  SoftwareTestEngineer: 'Software Test Engineer',
  ProjectEngineer: 'Project Engineer',
  ProgramManager: 'Program Manager',
  EngineeringManager: 'Engineering Manager',
  ConfigurationManager: 'Configuration Manager',
  SoftwareQualityAnalyst: 'Software Quality Assurance',
  Airworthiness: 'Airworthiness',
  SystemEngineeringLead: 'System Engineering Lead',
  SoftwareEngineeringLead: 'Software Engineering Lead',
  SystemTestLead: 'System Test Lead',
  SoftwareTestLead: 'Software Test Lead',
  // Legacy rows recorded before the cutover may still name these; they render as historical, never as choices.
  Reviewer: 'Reviewer',
  Approver: 'Approver',
  Engineer: 'Engineer',
  TestEngineer: 'Test Engineer',
  TestLead: 'Test Lead',
  ProjectEngineeringLead: 'Project Engineering Lead',
  Administrator: 'Administrator',
}

export const authorityLabel = (value: string) => authorityRoleLabels[value] ?? value

/** The `<option>` value encodes kind and payload so an impossible combination cannot be built in the form. */
export const authorityToken = (kind: 'BaseRole' | 'LeadershipPosition', value: string) => `${kind}:${value}`

export const parseAuthorityToken = (token: string): { kind: 'BaseRole' | 'LeadershipPosition'; value: string } | null => {
  const separator = token.indexOf(':')
  if (separator <= 0) return null
  const kind = token.slice(0, separator)
  const value = token.slice(separator + 1)
  if (kind !== 'BaseRole' && kind !== 'LeadershipPosition') return null
  if (!value) return null
  return { kind, value }
}
