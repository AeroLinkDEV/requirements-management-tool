import { useState } from "react";
import { AccountSecurityDialog } from "./IdentityCenter";
import type { AuthUser } from "./IdentityCenter";
import { PersonAvatar } from "./People";
import "./PortalHeader.css";

export default function PortalHeader({
  api,
  user,
  onSignOut,
}: {
  api: string;
  user: AuthUser;
  onSignOut: () => void;
}) {
  const [securityOpen, setSecurityOpen] = useState(false);
  const role = user.isAdministrator
    ? "Administrator"
    : user.programs.flatMap((program) => program.roles)[0]?.replace(/([a-z])([A-Z])/g, "$1 $2") ??
      "AeroLink user";

  return (
    <>
      <header className="projectsTopBar">
        <div className="projectsTopBarInner">
          <div className="projectsBrand"><span aria-hidden="true">▲</span><b>AeroLink</b></div>
          <div className="projectsAccount">
            <button type="button" className="projectsSecurity" onClick={() => setSecurityOpen(true)}>
              Account security
            </button>
            <PersonAvatar userName={user.userName} displayName={user.displayName} size="small"/>
            <div><b>{user.displayName}</b><small>{role}</small></div>
            <button type="button" className="projectsSignOut" onClick={onSignOut}>Sign out</button>
          </div>
        </div>
      </header>
      {securityOpen && <AccountSecurityDialog api={api} onClose={() => setSecurityOpen(false)}/>}
    </>
  );
}
