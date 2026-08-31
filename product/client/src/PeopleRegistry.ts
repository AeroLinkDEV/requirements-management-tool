import mayaPatel from "./assets/people/maya-patel.png";
import ethanBrooks from "./assets/people/ethan-brooks.png";
import oliviaChen from "./assets/people/olivia-chen.png";
import danielReyes from "./assets/people/daniel-reyes.png";

export type DemoPerson = {
  name: string;
  role: string;
  portrait: string;
};

const people: Record<string, DemoPerson> = {
  "systems.lead": { name: "Maya Patel", role: "Systems Lead", portrait: mayaPatel },
  "systems.author": { name: "Maya Patel", role: "Systems Lead", portrait: mayaPatel },
  // A distinct approval principal: it must never be rendered as the showcase author.
  "systems.reviewer": { name: "Systems Engineer", role: "Systems Assurance Reviewer", portrait: "" },
  "lead.reviewer": { name: "Maya Patel", role: "Systems Lead", portrait: mayaPatel },
  "test.engineer": { name: "Ethan Brooks", role: "Verification Lead", portrait: ethanBrooks },
  "test.author": { name: "Ethan Brooks", role: "Verification Lead", portrait: ethanBrooks },
  "verification.engineer": { name: "Ethan Brooks", role: "Verification Lead", portrait: ethanBrooks },
  "assurance.reviewer": { name: "Olivia Chen", role: "Safety Lead", portrait: oliviaChen },
  "manager.reviewer": { name: "Olivia Chen", role: "Safety Lead", portrait: oliviaChen },
  "engineering.manager": { name: "Olivia Chen", role: "Safety Lead", portrait: oliviaChen },
  "program.manager": { name: "Olivia Chen", role: "Program Manager", portrait: oliviaChen },
  "release.manager": { name: "Daniel Reyes", role: "Release Manager", portrait: danielReyes },
  "cm.fms": { name: "Daniel Reyes", role: "Configuration Manager", portrait: danielReyes },
  "software.lead": { name: "Rina Shah", role: "Software Engineering Lead", portrait: "" },
  "software.author": { name: "Daniel Reyes", role: "Software Lead", portrait: danielReyes },
  "quality.analyst": { name: "Marcus Hale", role: "Software Quality Analyst", portrait: "" },
};

export function demoPerson(userName: string, fallbackName?: string, fallbackRole = "Program member"): DemoPerson | undefined {
  const normalized = userName.trim().toLowerCase();
  const exact = people[normalized];
  if (exact) return exact;
  // Only the explicit, trusted showcase identities above may contribute a portrait or role. A username
  // prefix is not identity evidence: generated, renamed, or real accounts must retain their API-provided
  // display name and use the initials fallback rather than inheriting somebody else's likeness or title.
  if (!fallbackName) return undefined;
  return { name: fallbackName, role: fallbackRole, portrait: "" };
}

export const decisionPeople = {
  systems: people["systems.lead"],
  verification: people["test.engineer"],
  safety: people["program.manager"],
  release: people["release.manager"],
};

/**
 * A person's name as a plain string, for the places a component cannot go — a template literal, an aria-label,
 * a document title.
 *
 * Lives here rather than beside `PersonName` in People.tsx because a module that exports both components and
 * plain functions loses Fast Refresh: editing it remounts the tree instead of hot-swapping it, and the state
 * you were part-way through setting up disappears. It belongs here anyway — it is a thin reading of
 * `demoPerson`, which is defined two lines up.
 *
 * Falls back to the account name, so an unmapped or real account still identifies itself rather than
 * disappearing.
 */
export function personLabel(userName: string | undefined, displayName?: string) {
  if (!userName) return displayName ?? "";
  return demoPerson(userName, displayName)?.name ?? displayName ?? userName;
}
