import type { LatLon } from "./fmsModel";
import type { GpsBus } from "./gps";
import type { Navaid } from "./navData";
import { HELICOPTER_PROFILE } from "./profile";

/** Internal bench contracts, not physical ARINC framing or an interface to an actual CMA unit. */
export type SensorStatus = "NORMAL" | "NCD" | "FAIL";
export type Sample<T> = { at: number; sequence: number; status: SensorStatus; value: T | null };
export type AirData = { headingTrue: number; tasKt: number; altitudeFt: number };
export type Attitude = { bank: number; pitch: number };
export type RadioObservation = { station: Navaid; slantRangeNm: Sample<number>; bearingTrue: Sample<number> };
export type SensorFrame = {
  air: Sample<AirData>;
  attitude: Sample<Attitude>;
  radioHeight: Sample<number>;
  gps: readonly [Sample<GpsBus>, Sample<GpsBus>];
  radios: readonly RadioObservation[];
};
export interface SensorInputPort { read(): SensorFrame | null }
const validStamp = (sample: Sample<unknown>) => Number.isFinite(sample.at) && Number.isSafeInteger(sample.sequence)
  && sample.sequence >= 0 && ["NORMAL", "NCD", "FAIL"].includes(sample.status);
const monotonic = (sample: Sample<unknown>, previous?: Sample<unknown>) => !previous
  || sample.at >= previous.at && (sample.sequence > previous.sequence
    || sample.sequence === previous.sequence && JSON.stringify(sample) === JSON.stringify(previous));

/** Monotonic input mailbox for an external simulator or a replay. A refused frame never replaces accepted input. */
export class BufferedSensorPort implements SensorInputPort {
  private frame: SensorFrame | null = null;
  publish(frame: SensorFrame): boolean {
    const samples = [frame.air, frame.attitude, frame.radioHeight, ...frame.gps, ...frame.radios.flatMap(radio => [radio.slantRangeNm, radio.bearingTrue])];
    if (!samples.every(validStamp) || frame.radios.some(radio => !validPosition(radio.station.position))) return false;
    if (this.frame && (frame.air.at < this.frame.air.at || frame.air.sequence <= this.frame.air.sequence)) return false;
    if (!monotonic(frame.attitude, this.frame?.attitude) || !monotonic(frame.radioHeight, this.frame?.radioHeight)) return false;
    if (frame.gps.some((sample, index) => !monotonic(sample, this.frame?.gps[index]))) return false;
    if (frame.radios.some(radio => {
      const previous = this.frame?.radios.find(old => old.station.ident === radio.station.ident && old.station.frequency === radio.station.frequency);
      return !monotonic(radio.slantRangeNm, previous?.slantRangeNm) || !monotonic(radio.bearingTrue, previous?.bearingTrue);
    })) return false;
    this.frame = structuredClone(frame);
    return true;
  }
  read() { return this.frame ? structuredClone(this.frame) : null; }
}

/** Two seconds is a declared bench age limit, not an OEM bus timeout. Future-dated samples are unusable. */
export function sampled<T>(sample: Sample<T> | undefined, now: number, maxAgeMs = HELICOPTER_PROFILE.parameters.sensorMaxAge.value * 1000): T | null {
  return sample?.status === "NORMAL" && Number.isFinite(sample.at) && Number.isSafeInteger(sample.sequence)
    && sample.sequence >= 0 && sample.at <= now && now - sample.at <= maxAgeMs ? sample.value : null;
}
export const validPosition = (position: LatLon) => Number.isFinite(position.lat) && Math.abs(position.lat) <= 90
  && Number.isFinite(position.lon) && Math.abs(position.lon) <= 180;

export type GuidanceFrame<T> = { at: number; sequence: number; status: SensorStatus; value: T | null };
export interface GuidanceOutputPort<T> { write(frame: GuidanceFrame<T>): void }
