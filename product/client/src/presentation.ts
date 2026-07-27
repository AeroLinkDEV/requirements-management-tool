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

/**
 * A change request's state, said the way the programme says it.
 *
 * "Selected for baseline" describes the mechanism — a row was picked into a candidate — and says nothing a
 * reader wants to know, which is *which build is this going into, and has that build shipped*. Those are two
 * different facts and the stored state only carries the first:
 *
 *   Approved                     the engineering is signed for
 *   Allocated to 1.6             signed for, and going into this build
 *   Incorporated in 1.6          that build has been released, so it is in the product
 *
 * `Incorporated` is derived rather than stored, deliberately. It becomes true when the *build* is released, so
 * it is a fact about the release and not a transition somebody has to remember to perform — it can never
 * disagree with reality, and no new value has to be threaded through the readiness gates, the browser
 * journeys, the history filters and the seeded showcase that `ScrState` already reaches.
 */
export const changeRequestStateLabel = (
  state: string | undefined,
  targetRelease?: { version: string; isReleased: boolean },
) => {
  if (state !== 'SelectedForBaseline') return stateLabel(state)
  if (!targetRelease) return 'Allocated to a build'
  return targetRelease.isReleased
    ? `Incorporated in ${targetRelease.version}`
    : `Allocated to ${targetRelease.version}`
}
