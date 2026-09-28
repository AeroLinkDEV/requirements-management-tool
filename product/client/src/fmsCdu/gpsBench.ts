import type { FlightSimulator } from "./flight";
import { distanceNm } from "./fmsModel";
import { Constellation } from "./gnss";
import { GpsReceiver, type ApproachLevel, type GpsBus, type GpsInput, type GpsLabel, type GpsMode, type Override, type Ssm } from "./gps";
import type { FlightPhase } from "./navigation";
import type { ScriptedFms } from "./scriptedFms";

/**
 * The bench side of the CMA-5024 simulation: two receivers fed from the simulated aircraft's TRUE state (as a real
 * antenna would be), and the helpers the GPS sensors tab draws from. The FMS does not read these receivers yet: that is
 * the GPS integration (phase 3).
 */

/** What a receiver's antenna sees of the aircraft: its true position, altitude, attitude and velocity, at the bench time. */
export function gpsInput(fms: ScriptedFms, sim: FlightSimulator, options: { baroLost?: boolean } = {}): GpsInput {
  const speedFtPerS = Math.max(1, fms.groundSpeed * 1.68781);
  // Pitch from the flight path: the point-mass model has no angle of attack.
  const pitch = (Math.atan(fms.verticalSpeed / 60 / speedFtPerS) * 180) / Math.PI;
  return {
    time: fms.now.getTime(), position: fms.truePosition, altitude: fms.altitude, baroAltitude: options.baroLost ? null : fms.altitude,
    track: fms.track, groundSpeed: fms.groundSpeed, verticalSpeed: fms.verticalSpeed,
    attitude: { bank: sim.bankAngle, pitch, heading: fms.track },
  };
}

/** GPS 1 and GPS 2: one constellation, two receivers with their own error seeds (independent antennas and errors). */
export class GpsPair {
  readonly constellation = new Constellation(7);
  readonly receivers: [GpsReceiver, GpsReceiver] = [
    new GpsReceiver({ constellation: this.constellation, seed: 101 }),
    new GpsReceiver({ constellation: this.constellation, seed: 202 }),
  ];
  /** The bench's "baro lost" per receiver: its air data input goes silent. */
  readonly baroLost: [boolean, boolean] = [false, false];

  step(fms: ScriptedFms, sim: FlightSimulator) {
    this.receivers.forEach((rx, i) => rx.step(gpsInput(fms, sim, { baroLost: this.baroLost[i] })));
  }

  /** The distance between the two receivers' reported positions, m; null unless both report a valid one. */
  difference(): number | null {
    const [a, b] = this.receivers.map(rx => rx.bus());
    const position = (bus: GpsBus | null) => (bus && bus["110"].ssm === "NORMAL" && bus["111"].ssm === "NORMAL"
      ? { lat: bus["110"].value! + bus["120"].value!, lon: bus["111"].value! + bus["121"].value! } : null);
    const pa = position(a), pb = position(b);
    return pa && pb ? distanceNm(pa, pb) * 1852 : null;
  }
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

/** The GPS satellites in view below an elevation (terrain masking on the bench): the geostationary ones are left alone. */
export function lowSatellites(bus: GpsBus, belowDeg = 15): number[] {
  return bus["060"].map(word => word.value!).filter(s => !s.sbas && s.elevation < belowDeg).map(s => s.prn);
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
];
