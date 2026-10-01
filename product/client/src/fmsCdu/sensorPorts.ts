import type { LatLon } from "./fmsModel";
import type { GpsBus } from "./gps";
import type { Navaid } from "./navData";
import { HELICOPTER_PROFILE } from "./profile";

/** Internal bench contracts, not physical ARINC framing or an interface to an actual CMA unit. */
export type SensorStatus = "NORMAL" | "NCD" | "FAIL";
export type Sample<T> = { at: number; sequence: number; status: SensorStatus; value: T | null };
/** Existing laboratory air-data acceptance ceiling, shared with navigation's validity and no-TAS age allowance. */
export const MAX_ACCEPTED_TAS_KT = 600;
export type AirData = { headingTrue: number; tasKt: number; altitudeFt: number;
  /** Per-input navigation validity; omitted means valid for legacy adapters. Physical flight truth is separate. */
  headingValid?: boolean; tasValid?: boolean;
  /** Laboratory atmosphere reference for the indicated-altitude display; not an OEM air-data word. */
  indicationQnhHpa?: number;
  /** Adapter-provided validity flags; omitted means the legacy corrected, mutually consistent air-data contract. */
  baroCorrected?: boolean; pressureAltitudeFt?: number; altitudeRateValid?: boolean; altitudesAgree?: boolean };
export type Attitude = { bank: number; pitch: number };
/** Body-axis velocity and reflecting-surface mode are one measured Doppler word with one epoch. */
export type DopplerData = { alongKt: number; acrossKt: number; verticalFtMin?: number; surface?: "LAND" | "SEA" };
const validDoppler = (value: DopplerData) => Number.isFinite(value.alongKt) && Number.isFinite(value.acrossKt)
  && (value.verticalFtMin === undefined || Number.isFinite(value.verticalFtMin))
  && (value.surface === undefined || value.surface === "LAND" || value.surface === "SEA");
/** Range identity is supplied by the receiver adapter, never inferred for an external word. */
export type RangeIdentity = { receiver: "dme1" | "dme2"; channel: 1 | 2 | 3; frequency: string; commandSequence: number };
export function validRangeIdentity(identity: RangeIdentity | undefined, frequency: string): identity is RangeIdentity {
  return !!identity && ["dme1", "dme2"].includes(identity.receiver) && [1, 2, 3].includes(identity.channel)
    && identity.frequency === frequency && Number.isSafeInteger(identity.commandSequence) && identity.commandSequence >= 0;
}
export type RadioObservation = { rangeIdentity?: RangeIdentity; station: Navaid; slantRangeNm: Sample<number>; bearingTrue: Sample<number>; reportedDmeIdent?: Sample<string> };
export type SensorFrame = {
  air: Sample<AirData>;
  attitude: Sample<Attitude>;
  radioHeight: Sample<number>;
  gps: readonly [Sample<GpsBus>, Sample<GpsBus>];
  radios: readonly RadioObservation[];
  /** Plan F11: the APIRS's earth-frame accelerations (m/s²) and the Doppler's body-axis velocity over the surface (kt). */
  apirs?: Sample<{ northMs2: number; eastMs2: number }>;
  /** Legacy adapters omitting surface retain LAND semantics; omission never grants water-current correction. */
  dvs?: Sample<DopplerData>;
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
  /** Optional delivery may stop; only a genuinely newer accepted word can advance its watermark. */
  private acceptedDvs: Sample<DopplerData> | undefined;
  publish(frame: SensorFrame): boolean {
    const samples = [frame.air, frame.attitude, frame.radioHeight, ...frame.gps, ...(frame.dvs ? [frame.dvs] : []), ...frame.radios.flatMap(radio => [radio.slantRangeNm, radio.bearingTrue, ...(radio.reportedDmeIdent ? [radio.reportedDmeIdent] : [])])];
    if (!samples.every(validStamp) || frame.radios.some(radio => !validPosition(radio.station.position))) return false;
    if (frame.dvs && (!monotonic(frame.dvs, this.acceptedDvs) || frame.dvs.value !== null && !validDoppler(frame.dvs.value))) return false;
    if (this.frame && (frame.air.at < this.frame.air.at || frame.air.sequence <= this.frame.air.sequence)) return false;
    if (!monotonic(frame.attitude, this.frame?.attitude) || !monotonic(frame.radioHeight, this.frame?.radioHeight)) return false;
    if (frame.gps.some((sample, index) => !monotonic(sample, this.frame?.gps[index]))) return false;
    if (frame.radios.some(radio => {
      const previous = this.frame?.radios.find(old => old.station.ident === radio.station.ident && old.station.frequency === radio.station.frequency);
      return !monotonic(radio.slantRangeNm, previous?.slantRangeNm) || !monotonic(radio.bearingTrue, previous?.bearingTrue)
        || !!radio.reportedDmeIdent && !monotonic(radio.reportedDmeIdent, previous?.reportedDmeIdent);
    })) return false;
    this.frame = structuredClone(frame);
    if (this.frame.dvs) this.acceptedDvs = this.frame.dvs;
    return true;
  }
  read() { return this.frame ? structuredClone(this.frame) : null; }
}

/** Two seconds is a declared bench age limit, not an OEM bus timeout. Future-dated samples are unusable. */
export function sampled<T>(sample: Sample<T> | undefined, now: number, maxAgeMs = HELICOPTER_PROFILE.parameters.sensorMaxAge.value * 1000): T | null {
  return sample?.status === "NORMAL" && Number.isFinite(sample.at) && Number.isSafeInteger(sample.sequence)
    && sample.sequence >= 0 && sample.at <= now && now - sample.at <= maxAgeMs ? sample.value : null;
}
/** Both direct adapters and the mailbox must supply a finite, fresh, surface-qualified Doppler word. */
export function sampledDoppler(sample: Sample<DopplerData> | undefined, now: number, maxAgeMs: number): DopplerData | null {
  const value = sampled(sample, now, maxAgeMs);
  return value && validDoppler(value) ? value : null;
}
export const validPosition = (position: LatLon) => Number.isFinite(position.lat) && Math.abs(position.lat) <= 90
  && Number.isFinite(position.lon) && Math.abs(position.lon) <= 180;

export type GuidanceFrame<T> = { at: number; sequence: number; status: SensorStatus; value: T | null };
export interface GuidanceOutputPort<T> { write(frame: GuidanceFrame<T>): void }
