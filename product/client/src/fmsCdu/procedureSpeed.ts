import type { Leg } from "./fmsModel";
import type { Procedure, ProcedureLeg, SpeedLimit } from "./navData";
import { joinTransition } from "./procedures";

/**
 * The procedure speed limit in force (Stage C, C.5), from the approach's coded limits (ARINC 424 columns 100-102).
 *
 * A limit coded at a fix applies from that fix onward, until a later coded limit replaces it; a limit coded on a
 * conditional leg (a CA) applies from the start of that leg. Only limits that cap the speed (at, or at or below) are
 * limits; an at-or-above value is a minimum and is not applied here. This reads the FAA Copter charts' notes, the
 * oracle the coded values are checked against: 87N "Limit final and missed approach to 70K" is coded as 070 at the IF
 * (TIDUE), the MAP (CRANN) and the missed approach DF (BEADS).
 *
 * The missed approach release (MA-SPD-90, R3-04): in the missed approach, the missed approach hold's limit (87N 090)
 * takes over once valid barometric altitude is at or above the missed approach altitude (the hold's altitude, 2,000
 * ft), from that tick, whatever the altitude capture does. An invalid (null) barometric altitude keeps the lower limit.
 *
 * Under the ADVISORY profile the FMS has no speed authority: the limit is shown and used for the planned and predicted
 * speed, and the crew flies it. A hold's own speed is the hold's (hold.speed), not this.
 */
export type ProcedureSpeed = { kt: number; descriptor: SpeedLimit["descriptor"]; source: string };

type Entry = { leg: ProcedureLeg; missed: boolean };

/** Whether a coded speed caps the speed (at, or at or below). */
export const caps = (limit: SpeedLimit | undefined): limit is SpeedLimit => limit !== undefined && limit.descriptor !== "AT OR ABOVE";

/** The approach as flown: the chosen transition joined to the final by role, then the missed approach. */
function sequence(approach: Procedure, transition: string | undefined): Entry[] {
  const flown = joinTransition(transition ? approach.transitions[transition] ?? [] : [], approach.legs);
  return [...flown.map(leg => ({ leg, missed: false })), ...(approach.missed ?? []).map(leg => ({ leg, missed: true }))];
}

/** Where the active route leg is in the approach as flown, or -1 when it is not part of it. */
function locate(entries: Entry[], active: Leg | undefined): number {
  if (!active || active.kind === "disco" || (active.source !== "APPR" && active.source !== "MISSED")) return -1;
  const missed = active.source === "MISSED";
  return entries.findIndex(entry => entry.missed === missed && (active.kind === "wpt"
    ? "ident" in entry.leg && entry.leg.ident === active.ident
    : !("ident" in entry.leg) && entry.leg.path === active.path && Math.abs(entry.leg.course - active.course) < 0.05));
}

const feet = (text: string | undefined) => { const m = /^(\d{1,5})/.exec(text ?? ""); return m ? Number(m[1]) : null; };

export function procedureSpeedLimit(approach: Procedure | undefined, transition: string | undefined, active: Leg | undefined, baroAltitude: number | null): ProcedureSpeed | null {
  if (!approach) return null;
  const entries = sequence(approach, transition);
  const at = locate(entries, active);
  if (at < 0) return null;
  let limit: ProcedureSpeed | null = null;
  for (let i = 0; i <= at; i += 1) {
    const leg = entries[i].leg;
    // A fix's limit from the fix onward (once passed); a conditional leg's from its start.
    const inForce = "ident" in leg ? i < at : true;
    if (inForce && caps(leg.speedLimit)) limit = { ...leg.speedLimit, source: "ident" in leg ? `${approach.ident} ${leg.ident}` : `${approach.ident} ${leg.path}` };
  }
  const release = approach.missedHold?.speedLimit, releaseAt = feet(approach.missedHold?.altitude);
  if (entries[at].missed && caps(release) && releaseAt !== null && baroAltitude !== null && baroAltitude >= releaseAt) {
    return { ...release, source: `${approach.ident} missed approach altitude ${releaseAt} reached` };
  }
  return limit;
}

/** The lowest speed a procedure caps anything at (legs, conditional legs, holds), for the VMINI check; null when none. */
export function lowestProcedureLimit(approach: Procedure): number | null {
  const all: ProcedureLeg[] = [...approach.legs, ...(approach.missed ?? []), ...Object.values(approach.transitions).flat()];
  const limits = all.flatMap(leg => [leg.speedLimit, "ident" in leg ? leg.hold?.speedLimit : undefined]).filter(caps).map(limit => limit.kt);
  if (caps(approach.missedHold?.speedLimit)) limits.push(approach.missedHold!.speedLimit!.kt);
  return limits.length ? Math.min(...limits) : null;
}
