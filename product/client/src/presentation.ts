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

/**
 * Allocation and state, as two separate answers.
 *
 * `ScrState` was carrying both, and the two questions have different answers: *which build is this going into*
 * and *how far has it got*. Two of the five stored values were really allocations — `Deferred` says where the
 * work sits, `SelectedForBaseline` says which build it was picked into — so a reader asking either question got
 * a word that half answered the other.
 *
 *   allocation   1.6  ·  Deferred
 *   state        Draft · In review · Approved · Incorporated · Superseded
 *
 * `Incorporated` and `Superseded` are both derived rather than stored, and deliberately. Incorporated becomes
 * true when the *build* is released, which is a fact about the release; superseded becomes true when a later
 * revision of the same change request exists, which is a fact about the set. Storing either would mean a
 * transition somebody has to remember to perform, and a stored flag that disagrees with reality is worse than
 * no flag. Neither can disagree with reality when it is read from it.
 */
export type ChangeRequestFacts = {
  state?: string
  deferredFromState?: string | null
  targetRelease?: { version: string; isReleased: boolean }
  /** A later revision of this change request exists, so this one is history. */
  superseded?: boolean
}

export const changeRequestAllocation = (facts: ChangeRequestFacts) => {
  if (facts.state === 'Deferred') return 'Deferred'
  if (!facts.targetRelease) return 'No build'
  return facts.targetRelease.version
}

export const changeRequestState = (facts: ChangeRequestFacts) => {
  // Superseded first. A superseded revision may be Approved, and reading "Approved" against a revision that a
  // later one has replaced is the one wrong answer here: it invites somebody to work from stale content.
  if (facts.superseded) return 'Superseded'
  // A deferred change request reports how far it got, not that it is away — that is the allocation's job. Rows
  // deferred before this was remembered fall back to the plain label rather than inventing a state for them.
  if (facts.state === 'Deferred') return facts.deferredFromState ? stateLabel(facts.deferredFromState) : 'Deferred'
  if (facts.state !== 'SelectedForBaseline') return stateLabel(facts.state)
  // Approved and allocated is still approved work until the build it is in actually ships.
  return facts.targetRelease?.isReleased ? 'Incorporated' : 'Approved'
}
