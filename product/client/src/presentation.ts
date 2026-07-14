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

