import manifest from "./people-manifest.json" with { type: "json" };

export type DemoPerson = {
  name: string;
  role: string;
  portrait: string;
};

/**
 * The FMS showcase identity mapping.
 *
 * Two layers, by design (#913):
 *
 * - **Personhood (curated, below):** who the synthetic person behind each functional seed account is.
 *   The showcase casts one person across several functional accounts (Maya Patel is the systems
 *   author, the systems lead and the lead reviewer; Daniel Reyes is the release manager, the FMS
 *   configuration manager and the software author) — that casting is a product decision the
 *   attribution journeys enforce, and functional account labels ("Verification Author") are job
 *   descriptions, not people.
 * - **Portraits (manifest):** one repository-owned image per exact username, generated and guarded
 *   by `scripts/generate-people-portraits.mjs` and listed in `people-manifest.json`. The mapping is
 *   explicit per username — nothing is inferred at render time, and one person's picture can never
 *   attach to another account. Curated AI portraits cover the four photographed cast members; every
 *   other seeded account has a generated flat-design portrait.
 *
 * Unmapped accounts (real customers, renamed accounts, the system administrator) keep the initials
 * fallback: initials remain the technical fallback for real identities, exactly as #913 requires,
 * and account-attribution surfaces keep rendering the raw account.
 */
const personhood: Record<string, { name: string; role: string }> = {
  "systems.lead": { name: "Maya Patel", role: "Systems Lead" },
  "systems.author": { name: "Maya Patel", role: "Systems Lead" },
  // A distinct approval principal: it must never be rendered as the showcase author.
  "systems.reviewer": { name: "Systems Engineer", role: "Systems Assurance Reviewer" },
  "lead.reviewer": { name: "Maya Patel", role: "Systems Lead" },
  "test.engineer": { name: "Ethan Brooks", role: "Verification Lead" },
  "test.author": { name: "Ethan Brooks", role: "Verification Lead" },
  "verification.engineer": { name: "Ethan Brooks", role: "Verification Lead" },
  "assurance.reviewer": { name: "Olivia Chen", role: "Safety Lead" },
  "manager.reviewer": { name: "Olivia Chen", role: "Safety Lead" },
  "engineering.manager": { name: "Engineering Manager", role: "Engineering Manager" },
  "program.manager": { name: "Olivia Chen", role: "Program Manager" },
  "release.manager": { name: "Daniel Reyes", role: "Release Manager" },
  "cm.fms": { name: "Daniel Reyes", role: "Configuration Manager" },
  "software.lead": { name: "Rina Shah", role: "Software Engineering Lead" },
  "software.author": { name: "Daniel Reyes", role: "Software Lead" },
  "quality.analyst": { name: "Marcus Hale", role: "Software Quality Assurance" },
};

const portraits = manifest.people as Record<string, { file: string; name: string; role: string }>;

export function demoPerson(userName: string, fallbackName?: string, fallbackRole?: string): DemoPerson | undefined {
  const normalized = (userName ?? "").trim().toLowerCase();
  if (!normalized) return undefined;
  const cast = personhood[normalized];
  const entry = portraits[normalized];
  if (!cast && !entry && fallbackName === undefined && !fallbackRole) return undefined;
  // Resolution order: a caller-supplied historical/directory record is authoritative; then the
  // showcase casting names the person behind a functional account; then the manifest's seeded
  // directory entry for every other synthetic account; the generic label and raw username are last.
  return {
    name: fallbackName ?? cast?.name ?? entry?.name ?? userName,
    role: fallbackRole ?? cast?.role ?? entry?.role ?? "Program member",
    portrait: entry?.file ?? "",
  };
}

export const decisionPeople = {
  systems: demoPerson("systems.lead")!,
  verification: demoPerson("test.engineer")!,
  safety: demoPerson("program.manager")!,
  release: demoPerson("release.manager")!,
};

/**
 * A person's name as a plain string, for the places a component cannot go — a template literal, an aria-label,
 * a document title.
 *
 * Falls back to the account name, so an unmapped or real account still identifies itself rather than
 * disappearing.
 */
export function personLabel(userName: string | undefined, displayName?: string) {
  if (!userName) return displayName ?? "";
  return demoPerson(userName, displayName)?.name ?? displayName ?? userName;
}
