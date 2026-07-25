const identities:Record<string,string>={
  admin:'AeroLink Administrator',
  'systems.author':'Systems Requirements Author',
  'software.author':'Software Requirements Author',
  'systems.reviewer':'Systems Assurance Reviewer',
  'assurance.reviewer':'Independent Assurance Reviewer',
  'release.manager':'Release Authority',
  'test.engineer':'System Test Engineer',
  'verification.engineer':'Verification Engineer',
}

export const identityLabel=(identity:string)=>identities[identity.toLowerCase()]??identity.split(/[._-]/).filter(Boolean).map(part=>part[0]?.toUpperCase()+part.slice(1)).join(' ')
export const identityInitials=(identity:string)=>identityLabel(identity).split(/\s+/).map(part=>part[0]).join('').slice(0,2).toUpperCase()


/**
 * Lifecycle states are PascalCase enum names on the wire, and were rendered raw on roughly twenty surfaces
 * while nine others applied their own ad-hoc regex. The same change request therefore read
 * "SelectedForBaseline" on the Command Center and "Selected For Baseline" in the decision room.
 *
 * Known states get wording a reader would use; anything else falls back to splitting the PascalCase, so a
 * state added later degrades to something readable rather than to a defect.
 */
const stateLabels: Record<string, string> = {
  inreview: 'In review',
  selectedforbaseline: 'Selected for baseline',
  changesrequested: 'Changes requested',
  awaitingclosureapproval: 'Awaiting closure approval',
  resolutionproposed: 'Resolution proposed',
  readyforrelease: 'Ready for release',
  notstarted: 'Not started',
  inprogress: 'In progress',
  mustchangepassword: 'Must change password',
}

export const stateLabel = (state?: string) => {
  if (!state) return ''
  return stateLabels[state.toLowerCase()]
    ?? state.replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/^./, first => first.toUpperCase())
}
