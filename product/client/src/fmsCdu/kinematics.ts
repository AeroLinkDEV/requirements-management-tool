/**
 * Air-relative and ground-relative motion (helicopter-first plan, Stage B1). The aircraft flies a heading through the
 * air at its true airspeed; the wind carries the air mass; the ground velocity is their vector sum. There is no scalar
 * "TAS minus headwind" shortcut and no speed floor: a track the air-relative speed cannot hold in the wind is reported
 * infeasible, never clamped to a plausible number.
 *
 * Conventions: angles in degrees true, clockwise from north; the wind is given as the direction it blows FROM (as the
 * FMS and the manuals give it) and its speed; speeds in knots.
 */

import { ACTIVE_PROFILE } from "./profile";

export type Wind = { direction: number; speed: number };

const rad = (deg: number) => (deg * Math.PI) / 180;
const deg = (r: number) => (r * 180) / Math.PI;
const norm360 = (a: number) => ((a % 360) + 360) % 360;

/** The ground velocity from flying a heading at a true airspeed in a wind: speed (never negative) and track. */
export function groundVelocity(tas: number, heading: number, wind: Wind) {
  // Air velocity along the heading, plus the wind's velocity toward (direction + 180).
  const north = tas * Math.cos(rad(heading)) - wind.speed * Math.cos(rad(wind.direction));
  const east = tas * Math.sin(rad(heading)) - wind.speed * Math.sin(rad(wind.direction));
  const speed = Math.hypot(north, east);
  return { north, east, speed, track: speed > 1e-9 ? norm360(deg(Math.atan2(east, north))) : null };
}

export type TrackSolution =
  | { feasible: true; groundSpeed: number; heading: number; windCorrection: number }
  | { feasible: false; reason: string };

/**
 * The heading and ground speed that hold a desired track at a true airspeed in a wind (the wind triangle). With θ the
 * angle from the track to the direction the wind blows from, the crab angle is asin(W·sin θ / TAS) and the ground
 * speed TAS·cos(crab) − W·cos θ. Infeasible when the crosswind is stronger than the airspeed, or when the along-track
 * ground speed would not be positive: the aircraft cannot make progress along that track at that airspeed.
 */
export function holdTrack(tas: number, track: number, wind: Wind): TrackSolution {
  const theta = rad(wind.direction - track);
  const cross = wind.speed * Math.sin(theta);
  if (tas <= 0 || Math.abs(cross) > tas) return { feasible: false, reason: `crosswind ${Math.abs(cross).toFixed(1)} kt exceeds TAS ${tas.toFixed(1)} kt` };
  const crab = Math.asin(cross / tas);
  const groundSpeed = tas * Math.cos(crab) - wind.speed * Math.cos(theta);
  if (groundSpeed <= 0) return { feasible: false, reason: `headwind ${(wind.speed * Math.cos(theta)).toFixed(1)} kt stops progress at TAS ${tas.toFixed(1)} kt` };
  return { feasible: true, groundSpeed, heading: norm360(track + deg(crab)), windCorrection: deg(crab) };
}

/**
 * The ground speed a prediction may use along a track, or null when there is no measurable progress there (infeasible,
 * or below the profile's no-progress threshold). A null makes the prediction unknown from that leg on; it is never
 * replaced by an invented speed.
 */
export function predictedGroundSpeed(tas: number, track: number, wind: Wind): number | null {
  const solution = holdTrack(tas, track, wind);
  if (!solution.feasible) return null;
  return solution.groundSpeed < ACTIVE_PROFILE.parameters.noProgressBelow.value ? null : solution.groundSpeed;
}

/** Whether a ground speed is measurable progress (at or above the profile's no-progress threshold). */
export const makingProgress = (groundSpeed: number) => groundSpeed >= ACTIVE_PROFILE.parameters.noProgressBelow.value;
