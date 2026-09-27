import {
  SAR_PATTERNS, WAYPOINT, boxes, caption, courseDeg, dashes, distanceNm, fixed, formatPosition, maxSarGroundSpeed, medium,
  numberIn, offset, prompt, three, title, type Leg, type Page, type TacticalPageId,
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
  return medium(`${three(courseDeg(ref, fms.position))}°/${fixed(distanceNm(ref, fms.position), 1)}NM`);
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
      { left: prompt("<SECTOR"), right: prompt("TACT APPR>") },
      undefined,
      { left: prompt("<FLY OVER"), right: prompt("TACTICAL DTO>") },
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
        ? { left: sar.relativeBearing === null ? dashes(4) : { text: `${three(sar.relativeBearing)}°` }, right: { text: `${fixed(sar.legLength, 1)}NM` } }
        : pattern === "SECTOR"
          ? { left: sar.relativeBearing === null ? dashes(4) : { text: `${three(sar.relativeBearing)}°` }, right: { text: `${fixed(sar.diameter, 1)}NM` } }
          : { left: sar.relativeBearing === null ? dashes(4) : { text: `${three(sar.relativeBearing)}°` } };
      const active = sar.active ? `${sar.active} ${sar.status === "IN PROGRESS" ? "IN PROG" : "ARMED"}` : "NONE";
      return [
        title(`${pattern} SAR`, `${index + 1}/3`, status),
        caption(" ID", "TRK SPACING "),
        { left: { text: sar.id[pattern], color: "green" }, right: { text: `${fixed(sar.trackSpacing, 1)}NM` } },
        caption(" REF ID", "SAR BRG "),
        { left: sar.refId ? { text: sar.refId, color: "green" } : medium("PPOS"), right: { text: `${three(sar.sarBearing)}°` } },
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
      if (row === 6) {
        if (side === "L") {
          if (sar.pending) fms.eraseModification();
          else { sar.refId = null; sar.relativeBearing = null; sar.distance = null; }
          return;
        }
        if (sar.active) fms.interruptSar();
        else if (!sar.pending) fms.activateSar(pattern);
        return;
      }
      if (!scratch) return;
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
            return numeric(0, 360, n => { sar.relativeBearing = n; });
          case 4:
            if (scratch === "DELETE") { sar.distance = null; return done(); }
            return numeric(0, 99.9, n => { sar.distance = n; });
        }
        return;
      }
      switch (row) {
        // TRACK SPACING is 0.1 to 40 NM (manual, search patterns).
        case 1: return numeric(0.1, 40, n => { sar.trackSpacing = n; });
        case 2: return numeric(1, 360, n => { sar.sarBearing = n; });
        case 3:
          if (pattern === "LADDER") return numeric(0.5, 99, n => { sar.legLength = n; });
          if (pattern === "SECTOR") return numeric(0.5, 40, n => { sar.diameter = n; });
          return;
        case 4:
          if (pattern === "SECTOR") return numeric(10, 90, n => { sar.angle = n; });
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
        { left: { text: `${three(a.bearing)}°/${fixed(a.iafDistance, 1).padStart(4)}NM` }, right: { text: `${a.iafAltitude}FT` } },
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
            if (bearing !== undefined) a.bearing = bearing;
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

  HOVER: {
    pages: () => 1,
    render: fms => {
      const mark = fms.markList.at(-1);
      return [
        title("HOVER", "1/1"),
        caption(" MARK ON TOP POS", mark ? `${mark.ident} ` : undefined),
        { left: mark ? medium(formatPosition(mark.position), "green") : dashes(15) },
        caption(" RAD ALT", "TRUE WIND "),
        { left: medium("50FT"), right: medium(`${three(fms.wind.direction)}°/${fms.wind.speed}KT`) },
        caption(" PRESENT POS"),
        { left: medium(formatPosition(fms.position)) },
        undefined,
        { left: prompt("<MARK ON TOP") },
        undefined, undefined,
        { left: dashes(24) },
        { left: prompt("<DES+SAR") },
      ];
    },
    lsk: (fms, side, row) => {
      if (side !== "L") return;
      if (row === 4) fms.addMark();
      if (row === 6) fms.open("TACT");
    },
  },
};
