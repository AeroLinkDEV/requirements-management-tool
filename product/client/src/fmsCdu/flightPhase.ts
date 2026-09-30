import { distanceNm, type LatLon } from "./fmsModel";
import type { Airport } from "./navData";
import type { FlightPhase } from "./navigation";

/** M300 1-11. Airport elevation substitutes for the local terrain reference in this bench. */
export function s300Phase(position: LatLon, altitude: number | null, departure: Airport | undefined, arrival: Airport | undefined, approach: boolean): FlightPhase {
  if (altitude === null || !Number.isFinite(altitude)) return "EN ROUTE";
  if (arrival && altitude - arrival.elevation < 15000 && approach) return "APPROACH";
  if (arrival && altitude - arrival.elevation < 15000 && distanceNm(position, arrival.position) <= 30) return "TERMINAL";
  if (departure && altitude - departure.elevation < 16000 && distanceNm(position, departure.position) < 33) return "TERMINAL";
  return "EN ROUTE";
}
