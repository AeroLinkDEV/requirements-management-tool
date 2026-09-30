import { bearingDeg, distanceNm, type LatLon } from "./fmsModel";
import type { NavEntry, Navaid } from "./navData";

/**
 * How the FMS works out where it is. Airline FMSs blend GPS, DME/DME, VOR/DME and inertial position in a fixed
 * priority. This bench currently selects GPS, two DMEs with good geometry, a VOR with collocated DME, then a
 * laboratory dead-reckoning estimate; it has no configured inertial equipment. ANP is the estimated 95% position
 * error radius. RNP is
 * the accuracy required for the phase of flight; ANP above RNP for longer than the phase's time to alert raises an
 * alert. (ICAO Doc 9613 RNAV/RNP functional requirements; Boeing 737 FCOM 11.31, Navigation Performance.)
 *
 * The figures here are representative, not a qualified sensor model.
 */

export type NavMode = "GPS" | "DME/DME" | "VOR/DME" | "DR";
export type FlightPhase = "EN ROUTE" | "TERMINAL" | "APPROACH";

/** Default RNP and time to alert by phase of flight (seconds). */
export const RNP_DEFAULTS: Record<FlightPhase, { rnp: number; alertSeconds: number }> = {
  "EN ROUTE": { rnp: 2.0, alertSeconds: 80 },
  TERMINAL: { rnp: 1.0, alertSeconds: 60 },
  APPROACH: { rnp: 0.3, alertSeconds: 10 },
};

/** Temporary laboratory DR error growth, NM/h; not an inertial sensor. Replaced by measured DR in the navigation phase. */
export const LAB_DR_ERROR_NM_PER_HOUR = 2.0;

export type SensorInputs = {
  gpsAvailable: boolean;
  /** Receiver autonomous integrity monitoring (or SBAS) confirms the GPS position. */
  gpsIntegrity: boolean;
  dmeAvailable: boolean;
  inhibited: readonly string[];
};

export type Selection = { mode: NavMode; dmes: Navaid[]; vor: Navaid | null; baseAnp: number };

/** Radio line of sight in NM from an altitude in feet, capped at the typical DME service range. */
export const radioRange = (altitude: number) => Math.min(160, 1.23 * Math.sqrt(Math.max(0, altitude)));

const hasDme = (n: Navaid) => n.type === "VORDME" || n.type === "VORTAC" || n.type === "DME";
const hasVor = (n: Navaid) => n.type === "VOR" || n.type === "VORDME" || n.type === "VORTAC";

/** Chooses the navigation source, in the airline priority order, from what is in range and not inhibited. */
export function selectSources(entries: NavEntry[], at: LatLon, altitude: number, inputs: SensorInputs): Selection {
  const range = radioRange(altitude);
  const navaids = entries
    .filter((e): e is Navaid => e.kind === "navaid" && !inputs.inhibited.includes(e.ident))
    .map(n => ({ n, d: distanceNm(at, n.position) }))
    .filter(({ d }) => d <= range)
    .sort((a, b) => a.d - b.d);
  const dmes = inputs.dmeAvailable ? navaids.filter(({ n }) => hasDme(n)) : [];
  const vor = navaids.find(({ n }) => hasVor(n) && hasDme(n) && inputs.dmeAvailable) ?? navaids.find(({ n }) => hasVor(n));

  // The DME pair with the best crossing angle: position accuracy falls off as the lines of position get parallel.
  let pair: { a: Navaid; b: Navaid; angle: number } | null = null;
  for (let i = 0; i < dmes.length; i += 1)
    for (let j = i + 1; j < dmes.length; j += 1) {
      const diff = Math.abs(((bearingDeg(at, dmes[i].n.position) - bearingDeg(at, dmes[j].n.position) + 540) % 360) - 180);
      if (diff >= 30 && diff <= 150 && (!pair || Math.abs(90 - diff) < Math.abs(90 - pair.angle))) pair = { a: dmes[i].n, b: dmes[j].n, angle: diff };
    }

  if (inputs.gpsAvailable)
    return { mode: "GPS", dmes: pair ? [pair.a, pair.b] : dmes.slice(0, 2).map(d => d.n), vor: vor?.n ?? null, baseAnp: inputs.gpsIntegrity ? 0.05 : 0.3 };
  if (pair) return { mode: "DME/DME", dmes: [pair.a, pair.b], vor: vor?.n ?? null, baseAnp: 0.1 + 0.15 / Math.sin((pair.angle * Math.PI) / 180) };
  // VOR/DME needs the DME for range: in a DME outage a VOR alone gives only a bearing, not a position.
  const vorDme = vor && hasDme(vor.n) && inputs.dmeAvailable ? vor : undefined;
  if (vorDme) return { mode: "VOR/DME", dmes: [vorDme.n], vor: vorDme.n, baseAnp: 0.2 + 0.03 * vorDme.d };
  return { mode: "DR", dmes: [], vor: vor?.n ?? null, baseAnp: 0.1 };
}

/** The position error each source leaves: small and steady for GPS, larger for radio updating. */
export function sourceError(mode: NavMode): { nm: number; bearing: number } {
  switch (mode) {
    case "GPS": return { nm: 0.02, bearing: 30 };
    case "DME/DME": return { nm: 0.15, bearing: 135 };
    case "VOR/DME": return { nm: 0.4, bearing: 200 };
    case "DR": return { nm: 0, bearing: 45 };
  }
}
