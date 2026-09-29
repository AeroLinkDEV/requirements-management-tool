/**
 * The independent test-side oracle for the helicopter transition down to hover (TD → gate segment → TD/H), the
 * T6 check at TDN, and the laboratory departure from hover (TU-LAB).
 *
 * It implements the contract in the helicopter-first plan, revision 3 (§3a parameters, D-T T5 and T6), as amended by
 * R3-01 of the revision 3.1 addendum and Astra's implementation notes on it:
 * - the full start state: IAS, radio height, vertical speed, the ground-velocity vector, the frozen wind;
 * - vertical-speed changes limited to 600 fpm/s, including from a nonzero initial vertical speed, so a vertical move
 *   is a trapezoidal (or, when short, triangular) vertical-speed profile, and a move begun with momentum away from
 *   its target first brakes, overshoots and comes back;
 * - capture (entering the §3a band), completion (inside the completion tolerance) and target arrival as three
 *   different events; distances follow the command profile, which capture does not change;
 * - refusal below the 80 KIAS gate speed; T6 at full precision; the closed-loop TD/H deceleration in kt/s with
 *   saturation at [0.5, 1.25] and no evaluation at d ≤ 0;
 * - ISA IAS↔TAS; TU-LAB initialised from the ground-velocity vector.
 *
 * It imports nothing from the product: it is the reference the production AFCS is compared against (within
 * ±0.005 NM, ±5 ft and ±1 kt at each stage boundary), so it must not share the production's arithmetic. Vertical
 * profiles are closed form; distances are integrals of ground speed over time, taken by composite Simpson's rule on
 * each smooth piece.
 *
 * Units: kt, KIAS, ft, fpm, fpm/s, kt/s, NM, s. Angles in degrees true. Heights are radio heights over terrain at
 * `elevationFt` (0 by default), and ISA is evaluated at elevation + height.
 */

// ---------------------------------------------------------------------------------------------- parameters (§3a)

export const TDN_PARAMETERS = {
  /** TD: descent rate (fpm) and gate height (ft, the target is min(this, RA at TDN)). */
  tdDescentFpm: 500,
  gateHeightFt: 200,
  /** TD: deceleration (kt/s, IAS) to the gate speed (KIAS). Below the gate speed at TDN, TD is refused. */
  tdDecelKtPerS: 1.0,
  gateSpeedKias: 80,
  /** The gate segment's planning minimum, NM. */
  gateMinimumNm: 0.2,
  /** TD/H: nominal ground deceleration and its closed-loop bounds, kt/s. */
  tdhDecelKtPerS: 0.75,
  tdhDecelMinKtPerS: 0.5,
  tdhDecelMaxKtPerS: 1.25,
  /** TD/H: descent rate to the hover height, fpm. */
  tdhDescentFpm: 150,
  /** TD/H engagement window (borrowed): RA ≥ 30 and ≤ 210 ft, IAS < 85 KIAS. */
  tdhWindowMinFt: 30,
  tdhWindowMaxFt: 210,
  tdhWindowMaxKias: 85,
  /** Hover height: selectable 30–200 ft, default 50. */
  hoverMinFt: 30,
  hoverMaxFt: 200,
  hoverDefaultFt: 50,
  /** Vertical speed limit (fpm) and vertical acceleration (fpm/s). */
  maxVsFpm: 1000,
  vsRateFpmPerS: 600,
  /** Radio altimeter valid range, ft. */
  raMaxFt: 2500,
  /** TU-LAB: acceleration (kt/s, IAS) to the climb speed (KIAS), climb (fpm) to the RA target (ft), HDG capture (KIAS). */
  tuAccelKtPerS: 1.0,
  tuTargetKias: 80,
  tuClimbFpm: 500,
  tuTargetRaFt: 200,
  tuHeadingCaptureKias: 40,
  /** TU-LAB is engageable below this IAS, with RA ≥ MUH. */
  tuEngageBelowKias: 40,
  minimumUseHeightFt: 30,
  /** Low-speed lateral acceleration limit, kt/s, which drives the cross-axis ground velocity to 0 on departure. */
  lateralAccelKtPerS: 1.5,
  /** Capture bands. */
  heightCaptureFt: 20,
  vsCaptureFpm: 200,
  iasCaptureKt: 2,
  hovCaptureGsKt: 1,
  hovCaptureRadiusM: 50,
  /** Completion tolerances (R3-01 2). */
  speedCompletionKt: 1,
  heightCompletionFt: 5,
  vsCompletionFpm: 50,
} as const;

const P = TDN_PARAMETERS;
const METRES_PER_NM = 1852;

// ---------------------------------------------------------------------------------------------- ISA

/** ISA density ratio σ at a pressure altitude in feet (troposphere). */
export const isaDensityRatio = (altitudeFt: number) => Math.pow(1 - 6.8756e-6 * altitudeFt, 4.2559);
/** True airspeed from indicated (equivalent, incompressible) airspeed: TAS = IAS / √σ. */
export const tasFromIas = (iasKt: number, altitudeFt: number) => iasKt / Math.sqrt(isaDensityRatio(altitudeFt));
export const iasFromTas = (tasKt: number, altitudeFt: number) => tasKt * Math.sqrt(isaDensityRatio(altitudeFt));

// ---------------------------------------------------------------------------------------------- vertical profiles

/** A piece of constant vertical acceleration: height (ft) and vertical speed (fpm) at its start. */
export type VerticalSegment = { startS: number; durationS: number; heightFt: number; vsFpm: number; accelFpmPerS: number };

export type VerticalProfile = {
  segments: VerticalSegment[];
  targetFt: number;
  /** Target arrival: at the target height with zero vertical speed. */
  arrivalS: number;
  heightAt(tS: number): number;
  vsAt(tS: number): number;
  /** The highest and lowest heights the profile passes through (an overshoot shows here). */
  maxHeightFt: number;
  minHeightFt: number;
};

/**
 * The vertical profile from a height and vertical speed to rest at a target height, flown at `rateFpm` with every
 * vertical-speed change limited to `accelFpmPerS`. Moving away from the target, it first brakes; moving toward it
 * too fast to stop in the distance, it brakes, overshoots and returns; otherwise it ramps to the commanded rate (or
 * less, when the move is too short for a plateau: a triangular profile), holds it, and ramps to rest on the target.
 */
export function verticalProfile(heightFt: number, vsFpm: number, targetFt: number, rateFpm: number, accelFpmPerS: number = P.vsRateFpmPerS): VerticalProfile {
  const segments: VerticalSegment[] = [];
  const a = accelFpmPerS;
  let t = 0, h = heightFt, v = vsFpm;
  const push = (durationS: number, accel: number) => {
    if (durationS <= 0) return;
    segments.push({ startS: t, durationS, heightFt: h, vsFpm: v, accelFpmPerS: accel });
    h += (v * durationS + (accel * durationS * durationS) / 2) / 60;
    v += accel * durationS;
    t += durationS;
  };
  // At most three passes: brake if moving away, brake through an overshoot, then the move to rest.
  for (let pass = 0; pass < 4; pass++) {
    const distance = targetFt - h;
    if (Math.abs(distance) < 1e-12 && Math.abs(v) < 1e-12) break;
    const s = distance !== 0 ? Math.sign(distance) : -Math.sign(v);
    const toward = v * s; // fpm toward the target (negative: away)
    if (toward < 0) { push(-toward / a, s * a); v = 0; continue; }
    const stopping = (toward * toward) / (2 * a) / 60; // ft
    if (stopping > Math.abs(distance) + 1e-12) { push(toward / a, -s * a); v = 0; continue; }
    // A move to rest on the target: to the peak rate, hold it, back to rest.
    const triangularPeak = Math.sqrt((2 * a * 60 * Math.abs(distance) + toward * toward) / 2);
    // Faster than the commanded rate (and able to stop): slow to the rate first. Otherwise ramp up to it, or less.
    const peak = toward > rateFpm ? rateFpm : Math.min(rateFpm, triangularPeak);
    const toPeak = Math.abs(peak - toward) / a;
    const rampDistance = (Math.abs(peak * peak - toward * toward) / (2 * a) + (peak * peak) / (2 * a)) / 60;
    const hold = peak > 0 ? ((Math.abs(distance) - rampDistance) * 60) / peak : 0;
    push(toPeak, peak >= toward ? s * a : -s * a);
    push(Math.max(0, hold), 0);
    push(peak / a, -s * a);
    h = targetFt; v = 0; // absorb rounding at the end of the move
    break;
  }
  const arrivalS = t;
  const at = (tS: number) => {
    let seg = segments.find(entry => tS < entry.startS + entry.durationS);
    if (!seg || tS < 0) return { h: tS < 0 ? heightFt : targetFt, v: tS < 0 ? vsFpm : 0 };
    const dt = tS - seg.startS;
    return { h: seg.heightFt + (seg.vsFpm * dt + (seg.accelFpmPerS * dt * dt) / 2) / 60, v: seg.vsFpm + seg.accelFpmPerS * dt };
  };
  let maxHeightFt = Math.max(heightFt, targetFt), minHeightFt = Math.min(heightFt, targetFt);
  for (const seg of segments) {
    // A turning point inside a segment is where the vertical speed crosses zero.
    const end = seg.heightFt + (seg.vsFpm * seg.durationS + (seg.accelFpmPerS * seg.durationS * seg.durationS) / 2) / 60;
    maxHeightFt = Math.max(maxHeightFt, end); minHeightFt = Math.min(minHeightFt, end);
    if (seg.accelFpmPerS !== 0) {
      const tz = -seg.vsFpm / seg.accelFpmPerS;
      if (tz > 0 && tz < seg.durationS) {
        const hz = seg.heightFt + (seg.vsFpm * tz + (seg.accelFpmPerS * tz * tz) / 2) / 60;
        maxHeightFt = Math.max(maxHeightFt, hz); minHeightFt = Math.min(minHeightFt, hz);
      }
    }
  }
  return { segments, targetFt, arrivalS, heightAt: tS => at(tS).h, vsAt: tS => at(tS).v, maxHeightFt, minHeightFt };
}

/**
 * The first time a vertical profile is within `heightBandFt` of its target with |VS| ≤ `vsBandFpm`: the RHT/ALT
 * capture with the §3a band (20 ft, 200 fpm), or completion with the completion tolerance (5 ft, 50 fpm). Exact:
 * within a segment height is quadratic and VS linear in time, so the earliest time is a segment start or a root of a
 * band edge.
 */
export function firstTimeWithin(profile: VerticalProfile, heightBandFt: number, vsBandFpm: number): number {
  const eps = 1e-9;
  const inside = (tS: number) => Math.abs(profile.heightAt(tS) - profile.targetFt) <= heightBandFt + eps && Math.abs(profile.vsAt(tS)) <= vsBandFpm + eps;
  for (const seg of profile.segments) {
    const candidates = [seg.startS];
    const { vsFpm: v0, accelFpmPerS: acc, heightFt: h0, durationS } = seg;
    if (acc !== 0) for (const edge of [vsBandFpm, -vsBandFpm]) candidates.push(seg.startS + (edge - v0) / acc);
    // h(t) = h0 + (v0 t + acc t²/2)/60 = target ± band  →  (acc/120) t² + (v0/60) t + (h0 - level) = 0
    for (const level of [profile.targetFt + heightBandFt, profile.targetFt - heightBandFt]) {
      const qa = acc / 120, qb = v0 / 60, qc = h0 - level;
      if (Math.abs(qa) < 1e-15) { if (qb !== 0) candidates.push(seg.startS - qc / qb); }
      else {
        const disc = qb * qb - 4 * qa * qc;
        if (disc >= 0) for (const sign of [-1, 1]) candidates.push(seg.startS + (-qb + sign * Math.sqrt(disc)) / (2 * qa));
      }
    }
    const within = candidates.filter(c => c >= seg.startS - eps && c <= seg.startS + durationS + eps).sort((x, y) => x - y);
    for (const c of within) if (inside(Math.max(seg.startS, c))) return Math.max(seg.startS, c);
  }
  return profile.arrivalS;
}

/** The three events of a stage axis (R3-01 2). */
export type AxisEvents = { captureS: number; completionS: number; arrivalS: number };

const verticalEvents = (profile: VerticalProfile): AxisEvents => ({
  captureS: firstTimeWithin(profile, P.heightCaptureFt, P.vsCaptureFpm),
  completionS: firstTimeWithin(profile, P.heightCompletionFt, P.vsCompletionFpm),
  arrivalS: profile.arrivalS,
});

/** A speed axis changing at a constant rate from `fromKt` to `toKt`: capture ±2 kt, completion ±1 kt, arrival. */
export function speedEvents(fromKt: number, toKt: number, rateKtPerS: number): AxisEvents {
  const time = (remaining: number) => Math.max(0, (Math.abs(toKt - fromKt) - remaining) / rateKtPerS);
  return { captureS: time(P.iasCaptureKt), completionS: time(P.speedCompletionKt), arrivalS: time(0) };
}

/** Composite Simpson's rule on [a, b] with n (even) intervals. */
function simpson(f: (x: number) => number, a: number, b: number, n = 400): number {
  if (b <= a) return 0;
  const step = (b - a) / n;
  let sum = f(a) + f(b);
  for (let i = 1; i < n; i++) sum += f(a + i * step) * (i % 2 ? 4 : 2);
  return (sum * step) / 3;
}

/** ∫ f over [0, end] split at the given breakpoints (where f's derivative may jump). Returns NM when f is in kt and time in s. */
function distanceNm(groundSpeedKt: (tS: number) => number, endS: number, breakpoints: number[]): number {
  const cuts = [0, ...breakpoints.filter(b => b > 0 && b < endS), endS].sort((x, y) => x - y);
  let total = 0;
  for (let i = 1; i < cuts.length; i++) total += simpson(groundSpeedKt, cuts[i - 1], cuts[i]);
  return total / 3600;
}

// ---------------------------------------------------------------------------------------------- the transition (T5)

export type TransitionStart = {
  iasKt: number;
  /** Radio height, ft; null or non-finite is an invalid RA. */
  raFt: number | null;
  vsFpm: number;
  /** The frozen wind resolved on the final track: headwind positive, and the crosswind the aircraft crabs against. */
  headwindKt: number;
  crosswindKt?: number;
  /** Selected hover height, ft (default 50). */
  hoverFt?: number;
  elevationFt?: number;
};

export type Refusal = { refused: true; reason: RefusalReason };
export type RefusalReason =
  | "below gate speed" | "vertical speed limit" | "radio height invalid" | "hover height out of range"
  | "outside TD/H window" | "no closure" | "TDN DIST SHORT" | "below minimum use height" | "not in the low-speed regime";

/** A stage boundary, for comparison with the production run. */
export type Boundary = { name: string; tS: number; distanceNm: number; heightFt: number; vsFpm: number; gsKt: number; iasKt: number };

export type TransitionPlan = {
  refused: false;
  td: {
    rht: AxisEvents;
    ias: AxisEvents;
    /** TD ends when both axes' command profiles have arrived; its distance follows the commands, not the captures. */
    durationS: number;
    distanceNm: number;
    gateHeightFt: number;
    maxHeightFt: number;
    minHeightFt: number;
  };
  gate: { gsKt: number; minimumNm: number; minimumDurationS: number };
  tdh: {
    gsStartKt: number;
    decelKtPerS: number;
    durationS: number;
    distanceNm: number;
    hoverHeightFt: number;
    rht: AxisEvents;
    /** When GS ≤ 1 kt (HOV capture speed), from the start of TD/H. */
    hovSpeedS: number;
  };
  /** D(TD) + gate minimum + D(TD/H), full precision. */
  plannedDtraNm: number;
  boundaries: Boundary[];
};

function admit(start: TransitionStart): RefusalReason | null {
  const ra = start.raFt;
  if (ra === null || !Number.isFinite(ra) || ra < 0 || ra > P.raMaxFt) return "radio height invalid";
  if (!(start.iasKt >= P.gateSpeedKias)) return "below gate speed";
  if (!(Math.abs(start.vsFpm) <= P.maxVsFpm)) return "vertical speed limit";
  const hover = start.hoverFt ?? P.hoverDefaultFt;
  if (!(hover >= P.hoverMinFt && hover <= P.hoverMaxFt)) return "hover height out of range";
  const gateHeight = Math.min(P.gateHeightFt, ra);
  if (gateHeight < P.tdhWindowMinFt || gateHeight > P.tdhWindowMaxFt) return "outside TD/H window";
  return null;
}

/**
 * The planned transition from the state at TDN (T5 as amended). TD brings the collective to min(200 ft, RA) at
 * 500 fpm and the IAS to 80 KIAS at 1.0 kt/s, each axis independently; the gate segment is level at 80 KIAS and at
 * least 0.20 NM; TD/H decelerates over the ground at 0.75 kt/s to GS 0 at MRK and descends at 150 fpm to the hover
 * height (the selected height, or the gate height if lower).
 */
export function planTransition(start: TransitionStart): TransitionPlan | Refusal {
  const reason = admit(start);
  if (reason) return { refused: true, reason };
  const elevation = start.elevationFt ?? 0;
  const crosswind = start.crosswindKt ?? 0;
  const ra = start.raFt!;
  const gateHeightFt = Math.min(P.gateHeightFt, ra);
  const hoverHeightFt = Math.min(start.hoverFt ?? P.hoverDefaultFt, gateHeightFt);

  // TD: TD commands no climb target; an initial climb is a transient braked at the vertical acceleration limit.
  const vertical = verticalProfile(ra, start.vsFpm, gateHeightFt, P.tdDescentFpm);
  const iasAt = (tS: number) => Math.max(P.gateSpeedKias, start.iasKt - P.tdDecelKtPerS * tS);
  const gsAt = (ias: number, heightFt: number) => {
    const tas = tasFromIas(ias, elevation + heightFt);
    return Math.sqrt(Math.max(0, tas * tas - crosswind * crosswind)) - start.headwindKt;
  };
  const ias = speedEvents(start.iasKt, P.gateSpeedKias, P.tdDecelKtPerS);
  const rht = verticalEvents(vertical);
  const tdDuration = Math.max(ias.arrivalS, rht.arrivalS);
  const tdGs = (tS: number) => gsAt(iasAt(tS), vertical.heightAt(tS));
  if (tdGs(0) <= 0) return { refused: true, reason: "no closure" };
  const breaks = [...vertical.segments.map(seg => seg.startS), ias.arrivalS];
  const tdDistance = distanceNm(tdGs, tdDuration, breaks);

  // Gate segment and TD/H.
  const gateGs = gsAt(P.gateSpeedKias, gateHeightFt);
  if (gateGs <= 0) return { refused: true, reason: "no closure" };
  const tdhVertical = verticalProfile(gateHeightFt, 0, hoverHeightFt, P.tdhDescentFpm);
  const tdhDuration = gateGs / P.tdhDecelKtPerS;
  const tdhDistance = (gateGs * gateGs) / (2 * P.tdhDecelKtPerS * 3600);
  const gateDuration = (P.gateMinimumNm / gateGs) * 3600;
  const plannedDtraNm = tdDistance + P.gateMinimumNm + tdhDistance;

  const tdhStart = tdDuration + gateDuration;
  const boundaries: Boundary[] = [
    { name: "TDN", tS: 0, distanceNm: 0, heightFt: ra, vsFpm: start.vsFpm, gsKt: tdGs(0), iasKt: start.iasKt },
    { name: "TD end", tS: tdDuration, distanceNm: tdDistance, heightFt: gateHeightFt, vsFpm: 0, gsKt: gateGs, iasKt: P.gateSpeedKias },
    { name: "TD/H start", tS: tdhStart, distanceNm: tdDistance + P.gateMinimumNm, heightFt: gateHeightFt, vsFpm: 0, gsKt: gateGs, iasKt: P.gateSpeedKias },
    {
      name: "MRK", tS: tdhStart + Math.max(tdhDuration, tdhVertical.arrivalS), distanceNm: plannedDtraNm, heightFt: hoverHeightFt, vsFpm: 0, gsKt: 0,
      iasKt: iasFromTas(Math.hypot(start.headwindKt, crosswind), elevation + hoverHeightFt),
    },
  ];
  return {
    refused: false,
    td: { rht, ias, durationS: tdDuration, distanceNm: tdDistance, gateHeightFt, maxHeightFt: vertical.maxHeightFt, minHeightFt: vertical.minHeightFt },
    gate: { gsKt: gateGs, minimumNm: P.gateMinimumNm, minimumDurationS: gateDuration },
    tdh: {
      gsStartKt: gateGs, decelKtPerS: P.tdhDecelKtPerS, durationS: tdhDuration, distanceNm: tdhDistance, hoverHeightFt,
      rht: verticalEvents(tdhVertical), hovSpeedS: Math.max(0, (gateGs - P.hovCaptureGsKt) / P.tdhDecelKtPerS),
    },
    plannedDtraNm,
    boundaries,
  };
}

// ---------------------------------------------------------------------------------------------- T6 at TDN

export type TdnCheck = { decision: "engage"; gateNm: number; plan: TransitionPlan } | { decision: "refuse"; reason: RefusalReason; gateNm: number | null };

/**
 * T6: recompute against the fixed MRK from the actual state. gate = remaining − D(TD) − D(TD/H) at full precision;
 * gate ≥ 0 engages (the slack is the gate segment), gate < 0 refuses with TDN DIST SHORT.
 */
export function checkAtTdn(start: TransitionStart, remainingNm: number): TdnCheck {
  const plan = planTransition(start);
  if (plan.refused) return { decision: "refuse", reason: plan.reason, gateNm: null };
  const gateNm = remainingNm - plan.td.distanceNm - plan.tdh.distanceNm;
  return gateNm >= 0 ? { decision: "engage", gateNm, plan } : { decision: "refuse", reason: "TDN DIST SHORT", gateNm };
}

// ---------------------------------------------------------------------------------------------- TD/H closed loop

export type TdhOutcome = {
  /** The deceleration flown (kt/s): the exact requirement when inside the bounds, else the bound. */
  decelKtPerS: number;
  saturated: "none" | "upper" | "lower";
  /** Where GS reaches 0, NM past MRK (negative: short of it). */
  stopOffsetNm: number;
  stopS: number;
  /** "arrived": GS 0 at MRK. Otherwise HOV must recover to MRK; recording the offset is not an arrival. */
  outcome: "arrived" | "overshoot-recovery" | "short-recovery";
  /** Whether HOV captures where it stops (GS ≤ 1 kt within 50 m of MRK). */
  hovCapturesAtStop: boolean;
};

/**
 * The TD/H ground deceleration from a ground speed and a remaining distance to MRK, closed loop:
 * a = GS² / (2 · d · 3600), bounded to [0.5, 1.25] kt/s. Inside the bounds the requirement is invariant under its own
 * deceleration, so GS reaches 0 exactly at MRK. Above the upper bound (or with d ≤ 0, where the expression is never
 * evaluated) it flies the upper bound and stops past MRK; below the lower bound it flies the lower bound and stops
 * short. Either way the requirement only moves further out of bounds, so the bound holds to the stop.
 */
export function tdhClosedLoop(gsKt: number, remainingNm: number): TdhOutcome | { invalid: "no closure" } {
  if (!(gsKt > 0)) return { invalid: "no closure" };
  let decel: number, saturated: TdhOutcome["saturated"];
  if (remainingNm <= 0) { decel = P.tdhDecelMaxKtPerS; saturated = "upper"; }
  else {
    const required = (gsKt * gsKt) / (2 * remainingNm * 3600);
    if (required > P.tdhDecelMaxKtPerS) { decel = P.tdhDecelMaxKtPerS; saturated = "upper"; }
    else if (required < P.tdhDecelMinKtPerS) { decel = P.tdhDecelMinKtPerS; saturated = "lower"; }
    else { decel = required; saturated = "none"; }
  }
  const stopDistance = (gsKt * gsKt) / (2 * decel * 3600);
  const stopOffsetNm = saturated === "none" ? 0 : stopDistance - remainingNm;
  const outcome = saturated === "none" ? "arrived" : stopOffsetNm > 0 ? "overshoot-recovery" : "short-recovery";
  return {
    decelKtPerS: decel, saturated, stopOffsetNm, stopS: gsKt / decel, outcome,
    hovCapturesAtStop: Math.abs(stopOffsetNm) * METRES_PER_NM <= P.hovCaptureRadiusM,
  };
}

// ---------------------------------------------------------------------------------------------- TU-LAB

export type Vector = { northKt: number; eastKt: number };

export type HoverStart = {
  raFt: number | null;
  vsFpm?: number;
  /** Heading held through the departure, degrees true. */
  headingDeg: number;
  groundVelocity: Vector;
  /** Wind as a velocity (the direction the air moves toward), kt. A 20 kt wind from 230° is 20 kt toward 050°. */
  windVelocity: Vector;
  elevationFt?: number;
};

export type DeparturePlan = {
  refused: false;
  /** Forward and cross-axis components of the air velocity at the start, and the forward IAS from ISA. */
  initialForwardTasKt: number;
  initialCrossAirKt: number;
  initialIasKt: number;
  /** The cross-axis ground velocity at the start, and when the lateral limit has driven it to 0. */
  initialCrossGroundKt: number;
  crossGroundZeroS: number;
  rht: AxisEvents;
  ias: AxisEvents;
  /** HDG captures at IAS ≥ 40 KIAS (0 if already there). */
  headingCaptureS: number;
  /** Along-heading distance from the start to the later of the two arrivals, NM. */
  distanceNm: number;
  durationS: number;
};

const toHeading = (v: Vector, headingDeg: number) => {
  const h = (headingDeg * Math.PI) / 180;
  return { forward: v.northKt * Math.cos(h) + v.eastKt * Math.sin(h), cross: -v.northKt * Math.sin(h) + v.eastKt * Math.cos(h) };
};

/**
 * TU-LAB (B3.2, R3-01 6): from the true ground-velocity vector, air velocity = ground velocity − wind, and the
 * forward IAS from ISA. Pitch accelerates at 1.0 kt/s (IAS) along the held heading to 80 KIAS; the collective climbs
 * at 500 fpm to 200 ft RA with the vertical ramps; HDG captures at 40 KIAS; the cross-axis ground velocity is driven
 * to 0 at the 1.5 kt/s low-speed lateral limit. Engageable below 40 KIAS with a valid RA at or above MUH.
 */
export function departFromHover(start: HoverStart): DeparturePlan | Refusal {
  const ra = start.raFt;
  if (ra === null || !Number.isFinite(ra) || ra < 0 || ra > P.raMaxFt) return { refused: true, reason: "radio height invalid" };
  if (ra < P.minimumUseHeightFt) return { refused: true, reason: "below minimum use height" };
  const vs = start.vsFpm ?? 0;
  if (!(Math.abs(vs) <= P.maxVsFpm)) return { refused: true, reason: "vertical speed limit" };
  const elevation = start.elevationFt ?? 0;
  const air = { northKt: start.groundVelocity.northKt - start.windVelocity.northKt, eastKt: start.groundVelocity.eastKt - start.windVelocity.eastKt };
  const airAxes = toHeading(air, start.headingDeg);
  const groundAxes = toHeading(start.groundVelocity, start.headingDeg);
  const windAxes = toHeading(start.windVelocity, start.headingDeg);
  const initialIas = iasFromTas(airAxes.forward, elevation + ra);
  if (!(initialIas < P.tuEngageBelowKias)) return { refused: true, reason: "not in the low-speed regime" };

  const vertical = verticalProfile(ra, vs, P.tuTargetRaFt, P.tuClimbFpm);
  const ias = speedEvents(initialIas, P.tuTargetKias, P.tuAccelKtPerS);
  const iasAt = (tS: number) => Math.min(P.tuTargetKias, initialIas + P.tuAccelKtPerS * tS);
  const durationS = Math.max(ias.arrivalS, vertical.arrivalS);
  const forwardGs = (tS: number) => tasFromIas(iasAt(tS), elevation + vertical.heightAt(tS)) + windAxes.forward;
  const distance = distanceNm(forwardGs, durationS, [...vertical.segments.map(seg => seg.startS), ias.arrivalS]);
  return {
    refused: false,
    initialForwardTasKt: airAxes.forward,
    initialCrossAirKt: airAxes.cross,
    initialIasKt: initialIas,
    initialCrossGroundKt: groundAxes.cross,
    crossGroundZeroS: Math.abs(groundAxes.cross) / P.lateralAccelKtPerS,
    rht: verticalEvents(vertical),
    ias,
    headingCaptureS: Math.max(0, (P.tuHeadingCaptureKias - initialIas) / P.tuAccelKtPerS),
    distanceNm: distance,
    durationS,
  };
}

// ---------------------------------------------------------------------------------------------- GA climb gradient

/**
 * Height gained over a ground distance from level flight at a constant ground speed, climbing at `rateFpm` with the
 * vertical ramp (R3-04 3): the measured GA climb gradient, ft per `distanceNm`.
 */
export function climbGainFt(gsKt: number, rateFpm: number, distanceNm = 1): number {
  const profile = verticalProfile(0, 0, 1e9, rateFpm);
  return profile.heightAt((distanceNm / gsKt) * 3600);
}
