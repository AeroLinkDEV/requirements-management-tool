import { fromLocal, toLocal, type LatLon } from "./fmsModel";
import type { HoldSegment } from "./holds";

/**
 * Phase 1 of the transition down to hover: the path that joins the into-wind final track before TDN (plan D-T; Astra's
 * implementation review, Q9). A declared laboratory construction, not the OEM algorithm: the shortest turn–straight–
 * turn path over the ground (the four Dubins CSC words LSL, RSR, LSR, RSL) from the aircraft's present position and
 * track to the join point J, upstream of TDN on the final course, arriving on the final track. Its turns are circles of
 * one radius, sized for the fastest ground speed they can see (true airspeed plus the wind speed) at the design bank,
 * as the holds are (holds.ts): the bank then stays at or below the design value all round, whatever the wind. Started
 * over MRK, into the wind, it is an outbound-and-return path: a turn onto the downwind leg, straight, and a turn back
 * onto the final at J. The M300 illustrations do not establish a universal joining algorithm; this is an engineering
 * choice, selected and tested as one.
 *
 * Distances NM, angles degrees true (tracks measured clockwise from north).
 */

/** How far before TDN the join ends on the final course, NM: the aircraft is established before the TDN checks. */
export const JOIN_BEFORE_TDN_NM = 0.5;
/** The longest single arc piece, degrees: longer turns are flown as pieces (an arc's end is judged within 330 degrees). */
const MAX_ARC_PIECE = 180;

type Vec = { x: number; y: number };
type Word = "LSL" | "RSR" | "LSR" | "RSL";
export type JoinPath = { word: Word; lengthNm: number; radiusNm: number; from: LatLon; segments: HoldSegment[] };

const rad = (d: number) => (d * Math.PI) / 180;
const add = (a: Vec, b: Vec, k = 1): Vec => ({ x: a.x + b.x * k, y: a.y + b.y * k });
const sub = (a: Vec, b: Vec): Vec => ({ x: a.x - b.x, y: a.y - b.y });
const len = (v: Vec) => Math.hypot(v.x, v.y);
/** The unit vector along a track (clockwise from north: x east, y north). */
const along = (track: number): Vec => ({ x: Math.sin(rad(track)), y: Math.cos(rad(track)) });
/** Rotations in the local plane: left (counter-clockwise) and right (clockwise) by 90 degrees. */
const left = (v: Vec): Vec => ({ x: -v.y, y: v.x });
const right = (v: Vec): Vec => ({ x: v.y, y: -v.x });
/** The mathematical angle of a vector (counter-clockwise from east), radians. */
const angle = (v: Vec) => Math.atan2(v.y, v.x);
const mod2pi = (a: number) => ((a % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);

/** The centre of the turn circle of radius r to the given side of a pose. */
const centre = (p: Vec, t: Vec, side: "L" | "R", r: number) => add(p, side === "R" ? right(t) : left(t), r);

/** The angle swept from a to b around a centre, in the turn's direction (right: clockwise). */
function sweep(c: Vec, a: Vec, b: Vec, turn: "L" | "R") {
  const from = angle(sub(a, c)), to = angle(sub(b, c));
  return turn === "R" ? mod2pi(from - to) : mod2pi(to - from);
}

/**
 * The shortest turn–straight–turn path from (from, track) to (to, finalTrack) with turns of radius r, as segments over
 * the ground (the holds' arc and line segments, flown by the same guidance). Always found: LSL and RSR exist for any two
 * poses; LSR and RSL when their circles are at least 2r apart.
 */
export function joiningPath(from: LatLon, track: number, to: LatLon, finalTrack: number, radiusNm: number): JoinPath {
  const origin = from, r = radiusNm;
  const p0: Vec = { x: 0, y: 0 }, p1 = toLocal(origin, to);
  const t0 = along(track), t1 = along(finalTrack);
  let best: { word: Word; length: number; a: Vec; b: Vec; cs: Vec; ce: Vec; s: "L" | "R"; e: "L" | "R"; arc1: number; arc2: number } | null = null;
  for (const word of ["LSL", "RSR", "LSR", "RSL"] as Word[]) {
    const s = word[0] as "L" | "R", e = word[2] as "L" | "R";
    const cs = centre(p0, t0, s, r), ce = centre(p1, t1, e, r);
    const v = sub(ce, cs), d = len(v);
    let t: Vec, straight: number;
    if (s === e) {
      if (d < 1e-12) { t = t0; straight = 0; }
      else { t = { x: v.x / d, y: v.y / d }; straight = d; }
    } else {
      if (d < 2 * r) continue;
      straight = Math.sqrt(d * d - 4 * r * r);
      // RSL: V = L·t + 2r·u (u the left normal of t); LSR: V = L·t − 2r·u.
      const offsetAngle = Math.atan2(2 * r, straight) * (s === "R" ? -1 : 1);
      const a = angle(v) + offsetAngle;
      t = { x: Math.cos(a), y: Math.sin(a) };
    }
    // The tangent points: on the start circle where its travel is along t, on the end circle likewise.
    const a = add(cs, s === "R" ? left(t) : right(t), r);
    const b = add(ce, e === "R" ? left(t) : right(t), r);
    const arc1 = sweep(cs, p0, a, s), arc2 = sweep(ce, b, p1, e);
    const length = r * (arc1 + arc2) + straight;
    if (!best || length < best.length - 1e-9) best = { word, length, a, b, cs, ce, s, e, arc1, arc2 };
  }
  const w = best!;
  const segments: HoldSegment[] = [];
  const arc = (c: Vec, fromPoint: Vec, total: number, turn: "L" | "R") => {
    // Pieces of at most MAX_ARC_PIECE degrees, each ending on the circle.
    const pieces = Math.max(1, Math.ceil((total * 180) / Math.PI / MAX_ARC_PIECE - 1e-9));
    const start = angle(sub(fromPoint, c));
    for (let i = 1; i <= pieces; i++) {
      const at = start + (turn === "R" ? -1 : 1) * (total * i) / pieces;
      const end = { x: c.x + r * Math.cos(at), y: c.y + r * Math.sin(at) };
      segments.push({ kind: "arc", centre: fromLocal(origin, c), to: fromLocal(origin, end), turn, radius: r });
    }
  };
  if (w.arc1 > 1e-6) arc(w.cs, p0, w.arc1, w.s);
  if (len(sub(w.b, w.a)) > 1e-6) segments.push({ kind: "line", from: fromLocal(origin, w.a), to: fromLocal(origin, w.b) });
  if (w.arc2 > 1e-6) arc(w.ce, w.b, w.arc2, w.e);
  // End exactly on J (the local frame's rounding aside).
  const last = segments.at(-1);
  if (last) last.to = to;
  return { word: w.word, lengthNm: w.length, radiusNm: r, from, segments };
}
