import type { Lamp } from "./screen";

/**
 * Conditions an engineer can inject from the test bench. Most light an annunciator (when the chosen hardware
 * variation has that window) and change what the scripted FMS shows, as the Operator's Manual describes for the
 * real condition. The sensor conditions change what the FMS navigates with; the POS, RNP and NPA annunciators then
 * follow the navigation state (dead reckoning, ANP above RNP, a non-precision approach) rather than a switch.
 */
export type ConditionId =
  | "fmsFail" | "gpsLost" | "gpsIntegrity" | "dmeOutage" | "rnpExceeded" | "npa" | "offset" | "independent"
  | "gsmCall" | "sms" | "atcUplink" | "tx1" | "tx2" | "vuhf" | "hf" | "menuRequest" | "raFail" | "magvarCrc";

export type Condition = { id: ConditionId; label: string; description: string; lamp?: Lamp };

export const CONDITIONS: readonly Condition[] = [
  { id: "fmsFail", label: "FMS failure", lamp: "FAIL", description: "The display goes blank and keys are ignored." },
  { id: "magvarCrc", label: "MAGVAR checksum fault", description: "Corrupt the consumed coefficient package: MAG VAR CRC FAILED, SYSTEM FAILED and withdrawn navigation." },
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
  { id: "raFail", label: "Radio altimeter failed", description: "The radio height is a failure warning: no RADALT, and no hover procedure." },
];

/**
 * Sensor failures v1 does not model (rev 3 B3.5 F10): barometric altitude, heading and attitude are always valid in the
 * simulation, and the representative autopilot has no fallback for losing them. A scenario that injects one is refused at
 * admission with that reason, rather than run as though the aircraft had it; the bench lists them as not modelled.
 */
export const UNMODELLED_CONDITIONS: readonly { id: string; label: string }[] = [
  { id: "baroFail", label: "Barometric altitude invalid" },
  { id: "headingFail", label: "Heading invalid" },
  { id: "attitudeFail", label: "Attitude invalid" },
];
