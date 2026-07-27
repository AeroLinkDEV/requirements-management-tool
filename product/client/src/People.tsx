import { demoPerson } from "./PeopleRegistry";

/**
 * A person's name, where the product would otherwise print the account they sign in with.
 *
 * `PeopleRegistry` already maps every seeded account to a name, a role and a portrait, and two surfaces used
 * it. Everywhere else — audit histories, approval steps, control status, evidence provenance — rendered
 * `cm.fms` and `assurance.reviewer` at the reader. An audit trail exists to say who did something, and a
 * login handle is a worse answer to that than a name.
 *
 * Falls back to the account name, so an unmapped or real account still identifies itself rather than
 * disappearing.
 */
/**
 * The same name as a plain string, for the places a component cannot go — a template literal, an aria-label,
 * a document title.
 */
export function personLabel(userName: string | undefined, displayName?: string) {
  if (!userName) return displayName ?? "";
  return demoPerson(userName, displayName)?.name ?? displayName ?? userName;
}

// `userName` is optional because several of the records this renders have an optional actor — a lock with no
// holder, an impact item nobody has resolved. Those must render as nothing, not as "undefined".
export function PersonName({ userName, displayName, role, withRole = false }: {
  userName?: string;
  displayName?: string;
  role?: string;
  withRole?: boolean;
}) {
  const person = userName ? demoPerson(userName, displayName, role) : undefined;
  const name = person?.name ?? displayName ?? userName ?? "";
  // The account stays available to anyone who needs it — an auditor reconciling against the identity
  // provider should not have to guess which login "Maya Patel" was.
  return (
    <span className="personName" title={userName}>
      {name}
      {withRole && person?.role ? <small> · {person.role}</small> : null}
    </span>
  );
}

export function PersonAvatar({ userName, displayName, role, size = "medium" }: {
  userName: string;
  displayName?: string;
  role?: string;
  size?: "small" | "medium" | "large";
}) {
  const person = demoPerson(userName, displayName, role);
  const name = displayName ?? person?.name ?? userName;
  const initials = name.split(/\s+/).map((part) => part[0]).join("").slice(0, 2).toUpperCase();
  return person?.portrait
    ? <img className={`personAvatar ${size}`} src={person.portrait} alt={`${name}, ${person.role}`} />
    : <span className={`personAvatar personInitials ${size}`} aria-label={name}>{initials}</span>;
}
