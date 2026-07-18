import { demoPerson } from "./PeopleRegistry";

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
