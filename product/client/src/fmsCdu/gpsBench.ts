import { distanceNm } from "./fmsModel";
import type { ApproachLevel, GpsLabel, GpsMode, GpsReceiverView, Override, Ssm } from "./gps";
import { busFix, type GpsChoice } from "./gpsSensors";
import { stimulusFor, type GpsStimulusView } from "./gpsStimulus";
export { lowSatellites } from "./gpsStimulus";
import type { FlightPhase } from "./navigation";
import type { FmsView, ScriptedFms } from "./scriptedFms";

/**
 * The bench side of the CMA-5024 simulation. The FMS owns GPS1 and GPS2 and feeds them from the aircraft's true state
 * (scriptedFms.ts); the GPS sensors tab reads them through this view. Every change the bench makes (a fault, an
 * override, a deselection, the receiver selection) is an action submitted to the kernel (`f14.gps`, `fms.gpsSelect`),
 * followed by the FMS re-reading them, so it reaches the FMS's choice at once. The view itself changes nothing.
 */
export type GpsView = {
  receivers: readonly GpsReceiverView[];
  /** The distance between the two receivers' reported positions, m; null unless both report a valid one. */
  difference(): number | null;
  /** The FMS's GPS selection (NAV OPTIONS): AUTO, one receiver, or OFF (GPS deselected). */
  choice: GpsChoice | "OFF";
  /** The GPS integrity condition holds the receivers' satellite selection: bench masking is replaced while it is on. */
  integrityHeld: boolean;
  /** What the bench has injected into each receiver, kept with the bench session (gpsStimulus.ts). */
  stimulus: GpsStimulusView;
};

export function fmsGpsView(fms: FmsView, sensorOwner: FmsView = fms): GpsView {
  // The condition replaces the bench's masking and clears it (ScriptedFms.setCondition, GpsStimulus.apply): drawing
  // this view never changes the record (#1518). The record is the sensor owner's, created with its first use.
  const stimulus: GpsStimulusView = stimulusFor(sensorOwner as ScriptedFms), integrityHeld = sensorOwner.hasCondition("gpsIntegrity");
  return {
    receivers: fms.gps,
    difference: () => {
      const [a, b] = fms.gps.map(rx => { const bus = rx.bus(); return bus ? busFix(bus) : null; });
      return a && b ? distanceNm(a, b) * 1852 : null;
    },
    choice: fms.gpsNavSelected ? fms.gpsReceiverChoice : "OFF",
    integrityHeld,
    stimulus,
  };
}

/**
 * The integrity limits the GPS card draws HPL and VPL against, by flight phase: 2 NM en route, 1 NM terminal, and on
 * the approach those of the level the receiver supports (LPV 40 m / 50 m, LNAV/VNAV 556 m / 50 m, LNAV 556 m).
 */
export function alertLimits(phase: FlightPhase, level: ApproachLevel): { halM: number; valM: number | null } {
  if (phase === "EN ROUTE") return { halM: 3704, valM: null };
  if (phase === "TERMINAL") return { halM: 1852, valM: null };
  if (level === "LPV") return { halM: 40, valM: 50 };
  if (level === "LNAV/VNAV") return { halM: 556, valM: 50 };
  return { halM: 556, valM: null };
}

/** The bus monitor's override form as a receiver override: the amount is the value (FORCE), offset (BIAS) or rate (RAMP). */
export function overrideFor(kind: Override["kind"], amount: number, ssm?: Ssm): Override {
  switch (kind) {
    case "FORCE": return ssm ? { kind, value: amount, ssm } : { kind, value: amount };
    case "FREEZE": return { kind };
    case "BIAS": return { kind, amount };
    case "RAMP": return { kind, perSecond: amount };
  }
}

/** A mode as the manual names it. */
export function modeLabel(mode: GpsMode) { return mode.replace(/_/g, " "); }

/** The labels the bus monitor lists, with their names (ARINC 743A usage); numeric ones can be overridden. */
export const MONITOR_LABELS: { label: GpsLabel; name: string; numeric: boolean }[] = [
  { label: "110", name: "Latitude", numeric: true }, { label: "120", name: "Latitude fine", numeric: true },
  { label: "111", name: "Longitude", numeric: true }, { label: "121", name: "Longitude fine", numeric: true },
  { label: "076", name: "Altitude MSL (ft)", numeric: true }, { label: "370", name: "Height HAE (ft)", numeric: true },
  { label: "103", name: "Track (°T)", numeric: true }, { label: "112", name: "Ground speed (kt)", numeric: true },
  { label: "165", name: "Vertical velocity (fpm)", numeric: true }, { label: "166", name: "N/S velocity (kt)", numeric: true },
  { label: "174", name: "E/W velocity (kt)", numeric: true }, { label: "101", name: "HDOP", numeric: true },
  { label: "102", name: "VDOP", numeric: true }, { label: "130", name: "HIL (NM)", numeric: true },
  { label: "133", name: "VIL (ft)", numeric: true }, { label: "247", name: "HFOM (NM)", numeric: true },
  { label: "136", name: "VFOM (ft)", numeric: true }, { label: "116", name: "Lateral deviation (ft)", numeric: true },
  { label: "117", name: "Vertical deviation (ft)", numeric: true }, { label: "201", name: "Distance to threshold (NM)", numeric: true },
  { label: "150", name: "UTC", numeric: false }, { label: "260", name: "Date", numeric: false },
  { label: "273", name: "GLSSU status", numeric: false }, { label: "355", name: "Fault summary", numeric: false },
  { label: "156", name: "Approach selection status", numeric: false }, { label: "305", name: "SBAS PA status", numeric: false },
  { label: "scale", name: "Deviation scaling (model output)", numeric: false },
];
