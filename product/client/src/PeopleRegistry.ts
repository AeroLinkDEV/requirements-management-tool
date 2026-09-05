import manifest from "./people-manifest.json";

export type DemoPerson = {
  name: string;
  role: string;
  portrait: string;
};

/**
 * The FMS showcase identity mapping (#913).
 *
 * `people-manifest.json` is generated from the same directory the identity seeder writes
 * (IdentityService People + GeneratedPeople): per exact username it carries the seeded display
 * name and the repository-owned portrait file served from `/people/<username>.png`. The mapping
 * is therefore explicit and identity-safe — a portrait is never chosen by username prefix,
 * display-name similarity, or render-time inference, and one person's picture can never attach
 * to another account.
 *
 * The display name and role shown for a person come from the API's own directory record (passed
 * in by the caller as the fallback). The manifest's seeded name is used only when a caller has no
 * API record at all, so surfaces stay truthful even before an workspace context resolves.
 * Unmapped accounts (real customers, renamed accounts) keep the initials fallback by design.
 */
const directory = manifest.people as Record<string, { file: string; name: string; role: string }>;

export function demoPerson(userName: string, fallbackName?: string, fallbackRole?: string): DemoPerson | undefined {
  const normalized = (userName ?? "").trim().toLowerCase();
  if (!normalized) return undefined;
  const entry = directory[normalized];
  if (!entry && fallbackName === undefined && !fallbackRole) return undefined;
  // A caller-supplied role is authoritative (it is the account's live membership); otherwise the
  // seeded manifest role wins, and the generic label is last.
  return {
    name: fallbackName ?? entry?.name ?? userName,
    role: fallbackRole ?? entry?.role ?? "Program member",
    portrait: entry?.file ?? "",
  };
}

export const decisionPeople = {
  systems: demoPerson("systems.lead", "Systems Engineering Lead", "System Engineer")!,
  verification: demoPerson("test.engineer", "Ethan Brooks", "System Test Engineer")!,
  safety: demoPerson("program.manager", "Olivia Chen", "Program Manager")!,
  release: demoPerson("release.manager", "Daniel Reyes", "Configuration Manager")!,
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
