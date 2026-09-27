/**
 * Alert messages from the CMA-9000 Operator's Manual message list (Appendix, alert messages). The FMS shows an
 * alert in amber in the scratchpad and lights MSG until CLR acknowledges it. The meanings are short paraphrases
 * for engineers choosing what to inject; the manual is the authority for the triggering conditions.
 */
export type AlertDefinition = { text: string; meaning: string };

export const ALERTS: readonly AlertDefinition[] = [
  { text: "AIR DATA LOST", meaning: "Air data input is no longer valid" },
  { text: "ARM APPROACH", meaning: "Approach mode should be armed for the approach" },
  { text: "CHECK ANP", meaning: "Actual navigation performance exceeds the required value" },
  { text: "COURSE CHANGE>125 AHEAD", meaning: "A course change of more than 125 degrees is coming up" },
  { text: "DATABASE OUT OF DATE", meaning: "The navigation database effective period has expired" },
  { text: "DIRECT TO FIX", meaning: "A direct-to the next fix is required" },
  { text: "DME/DME NAV LOST", meaning: "DME/DME navigation is no longer available" },
  { text: "END OF ROUTE", meaning: "The last waypoint of the flight plan has been sequenced" },
  { text: "ENTER POS/DATE/TIME", meaning: "Position, date and time must be initialised" },
  { text: "FMS DEGRADED", meaning: "The FMS is operating with reduced capability" },
  { text: "FUEL RESERVE", meaning: "Fuel on board has reached the reserve quantity" },
  { text: "GPS NAV LOST", meaning: "GPS can no longer be used for navigation; the FMS dead reckons" },
  { text: "GPS POS UNCERTAIN", meaning: "GPS position integrity cannot be assured" },
  { text: "HEADING INPUT LOST", meaning: "The heading input is no longer valid" },
  { text: "HF CONTROL LOST", meaning: "The FMS can no longer tune the HF radio" },
  { text: "HIGH GLIDEPATH ANGLE", meaning: "The computed vertical path is steeper than 3.77 degrees" },
  { text: "HIGH HOLDING SPEED", meaning: "Speed is above the maximum holding speed for the altitude" },
  { text: "HIGH SAR SPEED", meaning: "Ground speed is above the maximum for the search pattern" },
  { text: "INDEPENDENT OP", meaning: "The FMS is no longer synchronised with the other FMS" },
  { text: "KALMAN NAV LOST", meaning: "The blended (Kalman filter) solution is no longer available" },
  { text: "LOW BATTERY POWER", meaning: "The internal battery is low" },
  { text: "LOW GLIDEPATH ANGLE", meaning: "The computed vertical path is shallower than 2.75 degrees" },
  { text: "MANUAL WPT SEQUENCE", meaning: "The waypoint must be sequenced manually" },
  { text: "NO APPR INTEGRITY", meaning: "GPS integrity is not sufficient for the approach" },
  { text: "NOT ENOUGH FUEL", meaning: "Predicted fuel at destination is below the reserve" },
  { text: "POSITION SHIFT", meaning: "The navigation solution has jumped" },
  { text: "RENDEZVOUS UNACHIEVABLE", meaning: "The rendezvous time cannot be met" },
  { text: "SET QNH", meaning: "Set the barometric reference for the approach" },
  { text: "TDN NOT POSSIBLE", meaning: "A tactical descent cannot be flown from the current state" },
  { text: "TIMER ALARM", meaning: "The alarm time or countdown on the TIMER page has been reached" },
  { text: "TPDR CONTROL LOST", meaning: "The FMS can no longer control the transponder" },
  { text: "VERIFY RNP VALUE", meaning: "The entered RNP differs from the value for the flight phase" },
  { text: "VHF CONTROL LOST", meaning: "The FMS can no longer tune the VHF radios" },
  { text: "VUHF CONTROL LOST", meaning: "The FMS can no longer tune the V/UHF radio" },
];

const byText = new Set(ALERTS.map(alert => alert.text));

/** An alert the simulation raises by itself; the name keeps every such message inside the library. */
export function alert(text: string) {
  if (!byText.has(text)) throw new Error(`${text} is not in the alert library`);
  return text;
}
