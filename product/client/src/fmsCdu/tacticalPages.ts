import {
  SAR_PATTERNS, WAYPOINT, boxes, caption, courseDeg, dashes, distanceNm, fixed, formatPosition, maxSarGroundSpeed, medium,
  numberIn, offset, parsePosition, prompt, small, three, title, type Leg, type Page, type TacticalPageId,
} from "./fmsModel";
import type { Line, Segment } from "./screen";
import type { ScriptedFms } from "./scriptedFms";

/**
 * Tactical pages of the SAR and tactical variations: the DES+SAR index, the SQUARE, LADDER and SECTOR search
 * patterns, the tactical approach and HOVER (Operator's Manual, tactical functions).
 */

/** Radial and distance of the aircraft from a reference waypoint. */
function radialDistance(fms: ScriptedFms, ident: string | null): Segment {
  const ref = ident ? fms.coordinates(ident) : undefined;
  if (!ref) return medium("---°/--.-NM");
  return medium(`${fms.angleText(courseDeg(ref, fms.position), ref)}/${fixed(distanceNm(ref, fms.position), 1)}NM`);
}

/** A waypoint ident entry that must be known: a navigation database fix, a Mark On Top or a defined point. */
function knownWaypoint(fms: ScriptedFms, scratch: string): string | "invalid" | "not-in-database" {
  if (!WAYPOINT.test(scratch)) return "invalid";
  return fms.coordinates(scratch) ? scratch : "not-in-database";
}

function tactApprFafAltitude(fms: ScriptedFms) {
  const a = fms.tactAppr;
  return Math.round((a.runwayElevation + (a.fafDistance - a.mapDistance) * 6076.12 * Math.tan((Math.abs(a.vpa) * Math.PI) / 180)) / 10) * 10;
}

/** A velocity as the HOVER page shows it: signed, one decimal. */
const signed = (kt: number) => `${kt < 0 ? "-" : "+"}${Math.abs(kt).toFixed(1)}`;

/** A wind as the RTA page shows it: direction true and speed, as 020T/ 45KT. */
const windText = (wind: { direction: number; speed: number }) => `${three(wind.direction)}T/${String(wind.speed).padStart(3)}KT`;

export const TACTICAL_PAGES: Record<TacticalPageId, Page> = {
  TACT: {
    pages: () => 1,
    render: fms => [
      title("DES+SAR", "1/1"),
      caption(" REF ID", "RAD/DIS "),
      { left: fms.tact.refId ? { text: fms.tact.refId, color: "green" } : boxes(5), right: radialDistance(fms, fms.tact.refId) },
      undefined,
      { left: prompt("<SQUARE"), right: prompt("HOVER>") },
      undefined,
      { left: prompt("<LADDER"), right: prompt("HOLD>") },
      undefined,
      { left: prompt("<SECTOR"), right: fms.aircraftProfile.configuration.options.tacticalApproach.configured ? prompt("TACT APPR>") : undefined },
      undefined,
      { left: prompt("<FLY OVER"), right: prompt("TACTICAL DTO>") },
      undefined,
      { left: dashes(24) },
      { left: prompt("<INDEX"), right: prompt("TIMER>") },
    ],
    lsk: (fms, side, row, scratch) => {
      if (side === "L") {
        switch (row) {
          case 1: {
            if (!scratch) { if (fms.tact.refId) fms.setScratch(fms.tact.refId); return; }
            if (scratch === "DELETE") { fms.tact.refId = null; fms.setScratch(""); return; }
            const ident = knownWaypoint(fms, scratch);
            if (ident === "invalid" || ident === "not-in-database") return ident;
            fms.tact.refId = ident;
            fms.setScratch("");
            return;
          }
          case 2: case 3: case 4: fms.open("SAR", row - 2); return;
          case 5: fms.advisory(`${fms.addMark()} FLY OVER`); return;
          case 6: fms.open("INIT_REF", 1); return;
        }
        return;
      }
      switch (row) {
        case 1: fms.open("HOVER"); return;
        case 2: {
          // A hold at the reference waypoint, or at present position when there is none.
          let fix = fms.tact.refId;
          if (!fix) { fix = "PPOS"; fms.definePoint(fix, { ...fms.position }); }
          const result = fms.defineHold(fix);
          if (result) return result;
          fms.open("HOLD");
          return;
        }
        case 3: fms.open("TACT_APPR"); return;
        case 4: {
          if (!fms.tact.refId) return "not-allowed";
          const result = fms.directTo(fms.tact.refId);
          if (!result) fms.open("LEGS");
          return result;
        }
        case 5: fms.open("TDN"); return;
        case 6: fms.open("TIMER"); return;
      }
    },
  },

  SAR: {
    pages: () => SAR_PATTERNS.length,
    render: (fms, index) => {
      const pattern = SAR_PATTERNS[index];
      const sar = fms.sar;
      const status = sar.pending === pattern ? "MOD" : sar.active === pattern ? "ACT" : undefined;
      const third: Line = pattern === "LADDER"
        ? { left: sar.relativeBearing === null ? dashes(4) : { text: `${fms.angleText(sar.relativeBearing)}` }, right: { text: `${fixed(sar.legLength, 1)}NM` } }
        : pattern === "SECTOR"
          ? { left: sar.relativeBearing === null ? dashes(4) : { text: `${fms.angleText(sar.relativeBearing)}` }, right: { text: `${fixed(sar.diameter, 1)}NM` } }
          : { left: sar.relativeBearing === null ? dashes(4) : { text: `${fms.angleText(sar.relativeBearing)}` } };
      const active = sar.active ? `${sar.active} ${sar.status === "IN PROGRESS" ? "IN PROG" : "ARMED"}` : "NONE";
      return [
        title(`${pattern} SAR`, `${index + 1}/3`, status),
        caption(" ID", "TRK SPACING "),
        { left: { text: sar.id[pattern], color: "green" }, right: { text: `${fixed(sar.trackSpacing, 1)}NM` } },
        caption(" REF ID", "SAR BRG "),
        { left: sar.refId ? { text: sar.refId, color: "green" } : medium("PPOS"), right: { text: `${fms.angleText(sar.sarBearing)}` } },
        caption(" RELATIVE BRG", pattern === "LADDER" ? "LEG LENGTH " : pattern === "SECTOR" ? "DIAMETER " : undefined),
        third,
        caption(" DISTANCE", pattern === "SECTOR" ? "ANGLE " : undefined),
        { left: sar.distance === null ? dashes(6) : { text: `${fixed(sar.distance, 1)}NM` }, right: pattern === "SECTOR" ? { text: `${sar.angle}°` } : undefined },
        caption(" ACTIVE SAR", "MAX GS "),
        { left: medium(active, sar.active ? "green" : "white"), right: medium(`${maxSarGroundSpeed(sar, pattern)}KT`) },
        { left: dashes(24) },
        {
          left: prompt(sar.pending ? "<CANCEL" : "<PPOS"),
          right: sar.active ? prompt("INTERRUPT>") : sar.pending ? undefined : prompt("ACTIVATE>"),
        },
      ];
    },
    lsk: (fms, side, row, scratch, index) => {
      const pattern = SAR_PATTERNS[index];
      const sar = fms.sar;
      const done = () => { fms.setScratch(""); };
      const numeric = (min: number, max: number, set: (n: number) => void) => {
        const n = numberIn(scratch, min, max);
        if (n === null) return "invalid" as const;
        set(n);
        done();
      };
      // Engaged, the search is flown as defined: its parameters change only before (M300 11-15 NOTE); to change it, a
      // direct-to the search waypoint, and define it again.
      const engaged = sar.status === "IN PROGRESS";
      if (row === 6) {
        if (side === "L") {
          if (sar.pending) fms.eraseModification();
          // PPOS: only with no other search pattern waypoint in the active route (A-157; the bench is always airborne).
          else if (fms.activeRoute.legs.some(leg => leg.kind === "wpt" && leg.qualifier === "/S")) return "not-allowed";
          else { sar.refId = null; sar.relativeBearing = null; sar.distance = null; }
          return;
        }
        if (sar.active) fms.interruptSar();
        else if (!sar.pending) fms.activateSar(pattern);
        return;
      }
      if (!scratch) return;
      if (engaged) return "not-allowed";
      if (side === "L") {
        switch (row) {
          case 1:
            if (!WAYPOINT.test(scratch)) return "invalid";
            sar.id[pattern] = scratch;
            return done();
          case 2: {
            if (scratch === "DELETE") { sar.refId = null; return done(); }
            const ident = knownWaypoint(fms, scratch);
            if (ident === "invalid" || ident === "not-in-database") return ident;
            sar.refId = ident;
            return done();
          }
          case 3:
            if (scratch === "DELETE") { sar.relativeBearing = null; return done(); }
            return numeric(0, 360, n => { sar.relativeBearing = fms.angleFromEntry(n); });
          case 4:
            if (scratch === "DELETE") { sar.distance = null; return done(); }
            return numeric(0.1, 99.9, n => { sar.distance = n; });
        }
        return;
      }
      switch (row) {
        // The field ranges of M300 A-153…A-176: track spacing, leg length and diameter 0.1 to 40 NM (chapter 11-6 gives
        // the ladder's track spacing as 0.1 to 4.0; the field definition, A-157, 0.1 to 40, is taken), the SAR bearing
        // 000 to 360, the sector angle 5 to 90 degrees.
        case 1: return numeric(0.1, 40, n => { sar.trackSpacing = n; });
        case 2: return numeric(0, 360, n => { const angle = fms.angleFromEntry(n); if (angle !== null) sar.sarBearing = angle; });
        case 3:
          if (pattern === "LADDER") return numeric(0.1, 40, n => { sar.legLength = n; });
          if (pattern === "SECTOR") return numeric(0.1, 40, n => { sar.diameter = n; });
          return;
        case 4:
          if (pattern === "SECTOR") return numeric(5, 90, n => { sar.angle = n; });
      }
    },
  },

  TACT_APPR: {
    pages: () => 1,
    render: fms => {
      const a = fms.tactAppr;
      return [
        title("TACTICAL APPR", "1/1"),
        caption(" REF WPT ID", "RAD/DIS "),
        { left: a.refId ? { text: a.refId, color: "green" } : boxes(5), right: radialDistance(fms, a.refId) },
        caption(" IAF BRG/DIS", "IAF ALT "),
        { left: { text: `${fms.angleText(a.bearing)}/${fixed(a.iafDistance, 1).padStart(4)}NM` }, right: { text: `${a.iafAltitude}FT` } },
        caption(" FAF DIS", "RWY ELEV "),
        { left: { text: `${fixed(a.fafDistance, 1)}NM` }, right: { text: `${a.runwayElevation}FT` } },
        caption(" MAP DIS", "TRANS LVL "),
        { left: { text: `${fixed(a.mapDistance, 1)}NM` }, right: { text: `FL${String(a.transitionLevel).padStart(3, "0")}` } },
        caption(" VPA", "FAF ALT "),
        { left: { text: `${fixed(a.vpa, 1)}°` }, right: medium(`${tactApprFafAltitude(fms)}FT`) },
        { left: dashes(24) },
        { left: prompt("<DES+SAR"), right: a.refId ? prompt("ACTIVATE>") : undefined },
      ];
    },
    lsk: (fms, side, row, scratch) => {
      const a = fms.tactAppr;
      const done = () => { fms.setScratch(""); };
      if (row === 6) {
        if (side === "L") { fms.open("TACT"); return; }
        if (!a.refId) return;
        // The approach points lie on the chosen bearing from the reference: IAF, FAF, then the missed approach point.
        const ref = fms.coordinates(a.refId)!;
        const points: [string, number, string | undefined][] = [
          ["TIAF", a.iafDistance, String(a.iafAltitude)], ["TFAF", a.fafDistance, `${tactApprFafAltitude(fms)}A`], ["TMAP", a.mapDistance, undefined],
        ];
        for (const [ident, distance] of points) fms.definePoint(ident, offset(ref, a.bearing, distance));
        const legs: Leg[] = points.map(([ident, , altitude]) => ({ kind: "wpt", ident, altitude }));
        fms.replaceLegs([...legs, { kind: "disco" }, ...fms.route.legs]);
        fms.open("LEGS");
        return;
      }
      if (!scratch) return;
      if (side === "L") {
        switch (row) {
          case 1: {
            const ident = knownWaypoint(fms, scratch);
            if (ident === "invalid" || ident === "not-in-database") return ident;
            a.refId = ident;
            return done();
          }
          case 2: {
            const shape = /^(\d{1,3})?(?:\/(\d{1,2}(\.\d)?))?$/.exec(scratch);
            const bearing = shape?.[1] ? numberIn(shape[1], 1, 360) : undefined;
            const distance = shape?.[2] ? numberIn(shape[2], a.fafDistance + 0.1, 30) : undefined;
            if (!shape || bearing === null || distance === null || (bearing === undefined && distance === undefined)) return "invalid";
            if (bearing !== undefined) { const angle = fms.angleFromEntry(bearing); if (angle === null) return "not-allowed"; a.bearing = angle; }
            if (distance !== undefined) a.iafDistance = distance;
            return done();
          }
          case 3: {
            const n = numberIn(scratch, a.mapDistance + 0.1, a.iafDistance - 0.1);
            if (n === null) return "invalid";
            a.fafDistance = n;
            return done();
          }
          case 4: {
            const n = numberIn(scratch, 0, a.fafDistance - 0.1);
            if (n === null) return "invalid";
            a.mapDistance = n;
            return done();
          }
          case 5: {
            const n = numberIn(scratch.replace(/^-/, ""), 2, 6);
            if (n === null) return "invalid";
            a.vpa = -n;
            return done();
          }
        }
        return;
      }
      switch (row) {
        case 2: {
          const n = numberIn(scratch, 500, 10000, /^\d{3,5}$/);
          if (n === null) return "invalid";
          a.iafAltitude = n;
          return done();
        }
        case 3: {
          const n = numberIn(scratch, -1000, 15000, /^-?\d{1,5}$/);
          if (n === null) return "invalid";
          a.runwayElevation = n;
          return done();
        }
        case 4: {
          const n = numberIn(scratch.replace(/^FL/, ""), 30, 450, /^\d{2,3}$/);
          if (n === null) return "invalid";
          a.transitionLevel = n;
          return done();
        }
      }
    },
  },

  RNDZ: {
    pages: () => 1,
    render: fms => {
      const r = fms.rndz;
      const plan = fms.rendezvous();
      const utc = (ms: number) => { const d = new Date(ms); return `${String(d.getUTCHours()).padStart(2, "0")}${String(d.getUTCMinutes()).padStart(2, "0")}Z`; };
      return [
        title("RENDEZVOUS", "1/1", r.active ? "ACT" : undefined),
        caption(" WPT", "TIME "),
        { left: r.wpt ? { text: r.wpt, color: "green" } : boxes(5), right: r.time === null ? boxes(4) : { text: utc(r.time) } },
        // The required true airspeed from the wind triangle over the legs to the fix, and that speed in IAS now (plan E4,
        // R3-04); none when the time has passed or the path to the fix is unknown, with the reason instead.
        caption(" DIST", "REQ TAS/IAS "),
        {
          left: medium(plan?.distance != null ? `${fixed(plan.distance, 1)}NM` : "-----"),
          right: medium(!plan ? "-----" : plan.required === null ? "---/---KT" : `${Math.round(plan.required)}/${Math.round(plan.requiredIas!)}KT`, plan && !plan.achievable ? "amber" : "white"),
        },
        caption(" MIN IAS", "MAX IAS "),
        { left: { text: `${r.minSpeed}KT` }, right: { text: `${r.maxSpeed}KT` } },
        caption(" STATUS", plan?.status === "CONDITIONAL" ? "COND " : undefined),
        {
          left: medium(!plan ? "-----" : plan.achievable ? "ON TIME" : plan.reason ?? "UNACHIEVABLE", plan && !plan.achievable ? "amber" : "green"),
          // A CONDITIONAL prediction to the fix names its assumption (a MANUAL hold exited at its next crossing).
          right: plan?.status === "CONDITIONAL" && plan.reason ? small(plan.reason) : undefined,
        },
        // RTA WIND (M300 A-141): the wind the RTA is computed in, the system wind unless the crew enters one (large), which
        // never changes the system wind; DELETE brings the default back.
        r.wpt ? caption(" RTA WIND") : undefined,
        r.wpt ? { left: r.wind ? { text: windText(r.wind), color: "cyan" } : medium(windText(fms.rtaWind)) } : undefined,
        { left: dashes(24) },
        { left: prompt("<INDEX"), right: plan ? prompt(r.active ? "CANCEL>" : "ACTIVATE>") : undefined },
      ];
    },
    lsk: (fms, side, row, scratch) => {
      const r = fms.rndz;
      if (row === 6) {
        if (side === "L") { fms.open("INIT_REF", 1); return; }
        if (!fms.rendezvous()) return;
        r.active = !r.active;
        r.alerted = false;
        return;
      }
      if (!scratch) return;
      if (side === "L" && row === 1) {
        if (!fms.activeRoute.legs.some(leg => leg.kind === "wpt" && leg.ident === scratch)) return "invalid";
        r.wpt = scratch;
        return void fms.setScratch("");
      }
      if (side === "R" && row === 1) {
        // A time of day, HHMM or HHMMZ: the next occurrence of it.
        const shape = /^([01]\d|2[0-3])([0-5]\d)Z?$/.exec(scratch);
        if (!shape) return "invalid";
        const now = fms.utcTime.getTime();
        const at = new Date(now);
        at.setUTCHours(Number(shape[1]), Number(shape[2]), 0, 0);
        r.time = at.getTime() <= now ? at.getTime() + 86_400_000 : at.getTime();
        return void fms.setScratch("");
      }
      if (side === "L" && row === 5 && r.wpt) {
        if (scratch === "DELETE") { r.wind = null; return void fms.setScratch(""); }
        // Direction true and speed: 0 to 360 degrees and 0 to 200 kt (M300 A-141).
        const wind = /^(\d{3})\/(\d{1,3})$/.exec(scratch);
        if (!wind || Number(wind[1]) > 360 || Number(wind[2]) > 200) return "invalid";
        r.wind = { direction: Number(wind[1]) % 360, speed: Number(wind[2]) };
        return void fms.setScratch("");
      }
      if (row === 3) {
        const speed = numberIn(scratch, 40, 300, /^\d{2,3}$/);
        if (speed === null) return "invalid";
        if (side === "L") { if (speed >= r.maxSpeed) return "invalid"; r.minSpeed = speed; } else { if (speed <= r.minSpeed) return "invalid"; r.maxSpeed = speed; }
        return void fms.setScratch("");
      }
    },
  },

  MOVING_WPT: {
    pages: () => 1,
    render: fms => {
      const moving = Object.entries(fms.movingWaypoints);
      const lines: (Line | undefined)[] = [
        title("MOVING WPT", "1/1"),
        caption(" IDENT", "TRK/SPD "),
        { left: fms.movingDraft.ident ? { text: fms.movingDraft.ident } : boxes(5), right: fms.movingDraft.motion ? { text: `${fms.angleText(Number(fms.movingDraft.motion.split("/")[0]))}/${fms.movingDraft.motion.split("/")[1]}` } : boxes(6) },
        caption(" POSITION"),
        { left: fms.movingDraft.position ? medium(formatPosition(fms.movingDraft.position)) : boxes(15) },
        caption(" MOVING"),
      ];
      // Two lines each, the motion then where it is now: side by side they need 30 columns and overprinted each other.
      moving.slice(0, 2).forEach(([ident, motion], i) => {
        const at = fms.coordinates(ident);
        lines[6 + i * 2] = { left: medium(`${ident} ${fms.angleText(motion.track)}/${motion.speed}KT`, "green") };
        lines[7 + i * 2] = at ? { left: small(formatPosition(at)) } : undefined;
      });
      lines[11] = { left: dashes(24) };
      lines[12] = { left: prompt("<INDEX"), right: fms.movingDraft.ident && fms.movingDraft.position && fms.movingDraft.motion ? prompt("CREATE>") : undefined };
      return lines;
    },
    lsk: (fms, side, row, scratch) => {
      const draft = fms.movingDraft;
      if (row === 6) {
        if (side === "L") { fms.open("INIT_REF", 1); return; }
        const motion = draft.motion ? draft.motion.split("/").map(Number) : null;
        if (!draft.ident || !draft.position || !motion) return;
        fms.defineMoving(draft.ident, draft.position, motion[0], motion[1]);
        fms.movingDraft = { ident: null, position: null, motion: null };
        return;
      }
      if (!scratch) return;
      if (side === "L" && row === 1) {
        if (!WAYPOINT.test(scratch) || fms.coordinates(scratch)) return "invalid";
        draft.ident = scratch;
        return void fms.setScratch("");
      }
      if (side === "R" && row === 1) {
        const motion = /^(\d{3})\/(\d{1,3})$/.exec(scratch);
        if (!motion || Number(motion[1]) > 360 || Number(motion[2]) > 60) return "invalid";
        const angle = fms.angleFromEntry(Number(motion[1]));
        if (angle === null) return "not-allowed";
        draft.motion = `${angle}/${Number(motion[2])}`;
        return void fms.setScratch("");
      }
      if (side === "L" && row === 2) {
        // Where it is now: an existing waypoint, a latitude/longitude or a place/bearing/distance.
        const resolved = fms.resolveWaypoint(scratch);
        if (typeof resolved === "string") return resolved;
        if ("select" in resolved) return "invalid";
        const at = fms.coordinates(resolved.ident);
        if (!at) return "not-in-database";
        draft.position = at;
        return void fms.setScratch("");
      }
    },
  },

  TDN: {
    pages: () => 1,
    render: fms => {
      const t = fms.tdn;
      const angle = fms.tdnAngle();
      return [
        title("TACTICAL DESCENT", "1/1", t.active || t.level ? "ACT" : undefined),
        caption(" TGT ALT", "REF ID "),
        { left: { text: `${t.targetAltitude}FT` }, right: t.refId ? { text: t.refId, color: "green" } : boxes(5) },
        caption(" DIST BEFORE REF", "ANGLE "),
        { left: { text: `${fixed(t.distanceBefore, 1)}NM` }, right: medium(angle === null ? "--.-°" : `${fixed(angle, 1)}°`, angle !== null && angle > t.maxAngle ? "amber" : "white") },
        caption(" MAX ANGLE"),
        { left: medium(`${fixed(t.maxAngle, 1)}°`) },
        undefined, undefined, undefined, undefined,
        { left: dashes(24) },
        { left: prompt("<DES+SAR"), right: prompt(t.active || t.level ? "CANCEL>" : "EXECUTE>") },
      ];
    },
    lsk: (fms, side, row, scratch) => {
      const t = fms.tdn;
      if (row === 6) {
        if (side === "L") { fms.open("TACT"); return; }
        if (t.active || t.level) fms.cancelTdn();
        else fms.executeTdn();
        return;
      }
      if (!scratch) return;
      if (side === "L" && row === 1) {
        const altitude = numberIn(scratch, 100, 10000, /^\d{3,5}$/);
        if (altitude === null) return "invalid";
        t.targetAltitude = altitude;
        return void fms.setScratch("");
      }
      if (side === "R" && row === 1) {
        if (!WAYPOINT.test(scratch)) return "invalid";
        if (!fms.coordinates(scratch)) return "not-in-database";
        t.refId = scratch;
        return void fms.setScratch("");
      }
      if (side === "L" && row === 2) {
        const nm = numberIn(scratch, 0, 30);
        if (nm === null) return "invalid";
        t.distanceBefore = nm;
        return void fms.setScratch("");
      }
    },
  },

  HOVER: {
    pages: () => 1,
    // The CMA HOVER page (M300 11-21…11-22, A-74…A-76): the mark (MRK), the radio height and the hover height the AFCS
    // selected, the true wind and the AFCS's X/Y velocities, and ACTIVATE (only with a valid radio height) or CANCEL.
    render: fms => {
      const hover = fms.hover, mark = hover.mark;
      const status = hover.status === "NONE" ? undefined : hover.status;
      // ACTIVATE for a mark not yet flown: none active, or a new one designated over the active procedure (A-76).
      const canActivate = mark !== null && hover.status !== "MOD" && hover.active?.mark !== mark && fms.radioHeight.status === "NORMAL";
      return [
        title("HOVER", "1/1", status),
        caption(mark ? ` ${mark.ident}` : " MRK", mark?.label ? `${mark.label} ` : undefined),
        { left: mark ? medium(mark.ident, "green") : dashes(5), right: mark ? medium(formatPosition(mark.position), "green") : dashes(15) },
        caption(" RAD ALT", "HOVER HEIGHT "),
        // The radio altimeter's height above the declared surface; dashes when it has none (NCD) or has failed.
        { left: medium(fms.radioHeight.status === "NORMAL" ? `${Math.round(fms.radioHeight.value!)}FT` : "----FT"), right: medium(fms.afcs ? `${fms.afcs.hoverHeight}FT` : "----FT") },
        caption(" TRUE WIND", "VELOCITIES "),
        { left: medium(`${three(fms.wind.direction)}T/${fms.wind.speed}KT`), right: medium(fms.afcs?.vx != null ? `VX ${signed(fms.afcs.vx)}KT` : "VX ---.-KT") },
        { right: medium(fms.afcs?.vy != null ? `VY ${signed(fms.afcs.vy)}KT` : "VY ---.-KT") },
        { left: hover.status === "MOD" ? undefined : prompt("<MARK ON TOP") },
        undefined,
        { left: prompt("<DES+SAR") },
        { left: dashes(24) },
        { left: hover.status === "MOD" ? prompt("<CANCEL") : undefined, right: canActivate ? prompt("ACTIVATE>") : undefined },
      ];
    },
    lsk: (fms, side, row, scratch) => {
      const hover = fms.hover;
      if (row === 1 && hover.status !== "MOD") {
        if (!scratch) return "invalid";
        const position = side === "R" ? parsePosition(scratch) : null;
        const ok = side === "L" ? fms.designateHoverMarkIdent(scratch) : position !== null && fms.designateHoverMark({ ident: "WPT", position, label: null });
        if (!ok) return "invalid";
        fms.setScratch("");
        return;
      }
      if (side === "L" && row === 4 && hover.status !== "MOD") { fms.designateHoverMarkOnTop(); return; }
      if (side === "L" && row === 5) { fms.open("TACT"); return; }
      if (side === "L" && row === 6 && hover.status === "MOD") { fms.cancelHover(); return; }
      if (side === "R" && row === 6 && hover.status !== "MOD" && hover.mark && hover.active?.mark !== hover.mark) {
        const refused = fms.activateHover();
        if (refused) fms.setScratch(refused);
      }
    },
  },
};
