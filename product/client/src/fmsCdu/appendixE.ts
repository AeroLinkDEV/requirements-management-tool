/**
 * Where each message the FMS Test Bench raises comes from (Stage F12 groundwork): its row in Appendix E of the CMA-9000
 * Operator's Manual (S/W -300, 19 November 2009, "System Messages and Remote Annunciators"), by the page the row is on,
 * and `also` where the manual gives a second row for another configuration; or, for a message the manual does not have,
 * `laboratory` and why the bench raises it.
 *
 * This is the shared map the alert and radio work reads (F2's sensor messages, F8's per-row message predicates); it
 * holds no behaviour. The rows were found in the manual by their message text and reviewed one by one. Each row's
 * trigger, clear, inhibit and applicability are the manual's; tests of those belong with the code that raises them.
 */
export type AppendixESource = { page: string; also?: string } | { laboratory: string };

/** Appendix E's message classes, by its contents page: E-1 to E-18, E-19 to E-20, E-21 to E-28, E-29 to E-37, E-38 to E-50. */
export type AppendixEClass = "SYSTEM ALERT" | "MAINTENANCE ALERT" | "MAINTENANCE ADVISORY" | "STATUS ADVISORY" | "DATA ENTRY ADVISORY";

export function appendixEClass(page: string): AppendixEClass | null {
  const match = /^E-(\d+)$/.exec(page);
  const n = match ? Number(match[1]) : NaN;
  if (n >= 1 && n <= 18) return "SYSTEM ALERT";
  if (n >= 19 && n <= 20) return "MAINTENANCE ALERT";
  if (n >= 21 && n <= 28) return "MAINTENANCE ADVISORY";
  if (n >= 29 && n <= 37) return "STATUS ADVISORY";
  if (n >= 38 && n <= 50) return "DATA ENTRY ADVISORY";
  return null;
}

export const APPENDIX_E: Readonly<Record<string, AppendixESource>> = {
  "AIR DATA LOST": { page: "E-2" },
  "ARM APPROACH": { page: "E-2" },
  "CHECK ANP": { page: "E-3" },
  "COURSE CHANGE>125 AHEAD": { page: "E-4", also: "E-31" },
  "DATABASE OUT OF DATE": { page: "E-4" },
  "DIRECT TO FIX": { page: "E-5", also: "E-32" },
  "DME/DME NAV LOST": { page: "E-6" },
  "DVS NAV LOST": { page: "E-6" },
  "END OF ROUTE": { page: "E-6" },
  "ENTER POS/DATE/TIME": { page: "E-6" },
  "FMS DEGRADED": { page: "E-7" },
  "FUEL RESERVE": { page: "E-7" },
  "GPS NAV LOST": { page: "E-8" },
  "GPS POS UNCERTAIN": { page: "E-8" },
  "HEADING INPUT LOST": { page: "E-9" },
  "HF CONTROL LOST": { page: "E-9" },
  // Plan C3 (F8a): each radio's CONTROL LOST row, numbered as the manual names the radio.
  "ADF1 CONTROL LOST": { page: "E-2" }, "ADF2 CONTROL LOST": { page: "E-2" },
  "ATC1 CONTROL LOST": { page: "E-2" }, "ATC2 CONTROL LOST": { page: "E-2" },
  "COM1 CONTROL LOST": { page: "E-4" }, "COM2 CONTROL LOST": { page: "E-4" },
  "DME1 CONTROL LOST": { page: "E-6" }, "DME2 CONTROL LOST": { page: "E-6" },
  "NAV1 CONTROL LOST": { page: "E-13" }, "NAV2 CONTROL LOST": { page: "E-13" },
  "HIGH GLIDEPATH ANGLE": { page: "E-9" },
  "HIGH HOLDING SPEED": { page: "E-9" },
  "HIGH SAR SPEED": { page: "E-9" },
  "INDEPENDENT OP": { page: "E-10" },
  "KALMAN NAV LOST": { page: "E-12" },
  "LOW BATTERY POWER": { page: "E-12" },
  "LOW GLIDEPATH ANGLE": { page: "E-12" },
  "MANUAL WPT SEQUENCE": { page: "E-13" },
  "NO APPR INTEGRITY": { page: "E-13" },
  "NOT ENOUGH FUEL": { page: "E-14" },
  "POSITION SHIFT": { page: "E-14" },
  "RENDEZVOUS UNACHIEVABLE": { page: "E-14", also: "E-35" },
  "SET QNH": { page: "E-15" },
  "SET AIRPORT TEMP": { page: "E-15" },
  "TDN FUNCTION LOST": { page: "E-16" },
  "TDN NOT POSSIBLE": { page: "E-17" },
  "TIMER ALARM": { page: "E-17" },
  "TPDR CONTROL LOST": { page: "E-17" },
  "VERIFY RNP VALUE": { page: "E-17" },
  "VHF CONTROL LOST": { page: "E-17" },
  "VUHF CONTROL LOST": { page: "E-18" },
  // A maintenance alert: the magnetic variation tables fail their checksum (FMS FAILED).
  "MAG VAR CRC FAILED": { page: "E-20" },
  // Not a message of its own: the MESSAGE RECALL text the manual gives with an FMS FAILED condition (MAG VAR CRC FAILED).
  "SYSTEM FAILED": { page: "E-20" },
  "RALT FAILED": { page: "E-27" },
  "FMS NAV IN DR": { page: "E-33" },
  "TRANSITION DOWN": { page: "E-36" },
  "APPR ON GPS1": { laboratory: "the bench's approach-aware AUTO receiver transfer (gpsSensors.ts)" },
  "APPR ON GPS2": { laboratory: "the bench's approach-aware AUTO receiver transfer (gpsSensors.ts)" },
  "GPS1 NOT USABLE": { laboratory: "lost GPS redundancy, which the M300 does not annunciate" },
  "GPS2 NOT USABLE": { laboratory: "lost GPS redundancy, which the M300 does not annunciate" },
  "GPS DISAGREE": { laboratory: "the bench's GPS1/GPS2 position compare (gpsSensors.ts GPS_DISAGREE_NM)" },
  "UNABLE HOLD": { laboratory: "the bench cannot fly the racetrack when the wind is at least the airspeed (holds.ts)" },
  "TDN DIST SHORT": { laboratory: "the bench's check that the distance left at TDN can hold the transition to MRK" },
};
