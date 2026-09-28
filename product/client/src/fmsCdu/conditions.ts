import type { Lamp } from "./screen";

/**
 * Conditions an engineer can inject from the test bench. Most light an annunciator (when the chosen hardware
 * variation has that window) and change what the scripted FMS shows, as the Operator's Manual describes for the
 * real condition. The sensor conditions change what the FMS navigates with; the POS, RNP and NPA annunciators then
 * follow the navigation state (dead reckoning, ANP above RNP, a non-precision approach) rather than a switch.
 */
export type ConditionId =
  | "fmsFail" | "gpsLost" | "gpsIntegrity" | "dmeOutage" | "rnpExceeded" | "npa" | "offset" | "independent"
  | "gsmCall" | "sms" | "atcUplink" | "tx1" | "tx2" | "vuhf" | "hf" | "menuRequest";

export type Condition = { id: ConditionId; label: string; description: string; lamp?: Lamp };

export const CONDITIONS: readonly Condition[] = [
  { id: "fmsFail", label: "FMS failure", lamp: "FAIL", description: "The display goes blank and keys are ignored." },
  { id: "gpsLost", label: "GPS lost", description: "GPS NAV LOST; the FMS reverts to DME/DME, VOR/DME or dead reckoning (POS)." },
  { id: "gpsIntegrity", label: "GPS integrity lost (RAIM)", description: "GPS POS UNCERTAIN; ANP grows; no RNAV approach integrity." },
  { id: "dmeOutage", label: "DME outage", description: "No DME updating; with GPS lost too, the FMS dead reckons and drifts." },
  { id: "rnpExceeded", label: "Force RNP exceeded", lamp: "RNP", description: "CHECK ANP; ANP above RNP whatever the sensors say." },
  { id: "npa", label: "Force non-precision approach", lamp: "NPA", description: "Approach RNP of 0.30 NM on PROGRESS." },
  { id: "offset", label: "Lateral offset", lamp: "OFST", description: "A 2 NM left offset; enter or delete it on PROGRESS 4/4." },
  { id: "independent", label: "Independent operation", lamp: "IND", description: "INDEPENDENT OP: no longer synchronised." },
  { id: "gsmCall", label: "Incoming GSM call", lamp: "GSM", description: "ANS answers, then hangs up." },
  { id: "sms", label: "New SMS", lamp: "SMS", description: "ANS shows the newest message." },
  { id: "atcUplink", label: "ATC uplink", lamp: "ATC", description: "A CPDLC clearance to answer on the ATC page (simulated)." },
  { id: "tx1", label: "Radio 1 transmitting", lamp: "TX1", description: "COM1 shows TX on RADIO." },
  { id: "tx2", label: "Radio 2 transmitting", lamp: "TX2", description: "COM2 shows TX on RADIO." },
  { id: "vuhf", label: "V/UHF radio active", lamp: "V/UHF", description: "Lights the V/UHF annunciator." },
  { id: "hf", label: "HF radio active", lamp: "HF", description: "Lights the HF annunciator." },
  { id: "menuRequest", label: "Subsystem request", lamp: "MENU", description: "A subsystem asks for attention on MCDU MENU." },
];
