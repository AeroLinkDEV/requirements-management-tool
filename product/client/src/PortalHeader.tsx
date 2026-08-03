import type { AuthUser } from "./IdentityCenter";
import { PersonAvatar } from "./People";
import "./PortalHeader.css";

export default function PortalHeader({
  user,
  onSignOut,
}: {
  user: AuthUser;
  onSignOut: () => void;
}) {
  const role = user.isAdministrator
    ? "Administrator"
    : user.programs.flatMap((program) => program.roles)[0]?.replace(/([a-z])([A-Z])/g, "$1 $2") ??
      "AeroLink user";

  return (
      <header className="projectsTopBar">
        <div className="projectsTopBarInner">
          <div className="projectsBrand"><span aria-hidden="true">▲</span><b>AeroLink</b></div>
          <div className="projectsAccount">
            <PersonAvatar userName={user.userName} displayName={user.displayName} size="small"/>
            <div><b>{user.displayName}</b><small>{role}</small></div>
            <button type="button" className="projectsSignOut" onClick={onSignOut}>Sign out</button>
          </div>
        </div>
      </header>
  );
}
