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
  "systems.reviewer": { name: "Maya Patel", role: "Systems Lead", portrait: mayaPatel },
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
  "software.lead": { name: "Daniel Reyes", role: "Software Lead", portrait: danielReyes },
  "software.author": { name: "Daniel Reyes", role: "Software Lead", portrait: danielReyes },
};

export function demoPerson(userName: string, fallbackName?: string, fallbackRole = "Program member"): DemoPerson | undefined {
  const normalized = userName.trim().toLowerCase();
  const exact = people[normalized];
  if (exact) return exact;
  if (normalized.startsWith("systems.lead")) return people["systems.lead"];
  if (normalized.startsWith("verification.engineer")) return people["verification.engineer"];
  if (normalized.startsWith("engineering.manager")) return people["engineering.manager"];
  if (normalized.startsWith("configuration.specialist")) return people["cm.fms"];
  if (!fallbackName) return undefined;
  return { name: fallbackName, role: fallbackRole, portrait: "" };
}

export const decisionPeople = {
  systems: people["systems.lead"],
  verification: people["test.engineer"],
  safety: people["program.manager"],
  release: people["release.manager"],
};
