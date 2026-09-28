import {
  ICAO, WAYPOINT, boxes, caption, conditionalLabel, dashes, formatPosition, medium, prompt, small, title,
  type Leg, type LskResult, type Page, type PlanningPageId, type Route,
} from "./fmsModel";
import type { Line } from "./screen";
import type { Procedure } from "./navData";
import type { ScriptedFms } from "./scriptedFms";

/**
 * Flight planning pages: RTE (origin, destination, company route, and the VIA/TO airway entry), DEP/ARR with
 * DEPARTURES and ARRIVALS, REF NAV DATA, SELECT DESIRED WPT and the secondary flight plan.
 */

const back = (target: string) => prompt(`<${target}`);
const selected = (label: string, on: boolean, side: "L" | "R") =>
  on ? { text: side === "L" ? `${label} <SEL>` : `<SEL> ${label}`, color: "green" as const } : { text: label };

/** A route shown as VIA/TO segments: consecutive legs on one airway or procedure are one segment. */
type RouteSegment = { via: string; to: string; from: number; to_: number; disco: boolean };

export function routeSegments(route: Route): RouteSegment[] {
  const label = (leg: Exclude<Leg, { kind: "disco" }>) =>
    leg.via ?? (leg.source === "SID" ? route.sid?.ident : leg.source === "STAR" ? route.star?.ident : leg.source === "APPR" ? route.approach?.ident : leg.source === "MISSED" ? "MISSED" : undefined) ?? "DIRECT";
  const segments: RouteSegment[] = [];
  route.legs.forEach((leg, i) => {
    if (leg.kind === "disco") { segments.push({ via: "", to: "DISCONTINUITY", from: i, to_: i, disco: true }); return; }
    const via = label(leg);
    const to = leg.kind === "cond" ? conditionalLabel(leg) : leg.ident;
    const last = segments.at(-1);
    if (last && !last.disco && last.via === via && via !== "DIRECT") { last.to = to; last.to_ = i; return; }
    segments.push({ via, to, from: i, to_: i, disco: false });
  });
  return segments;
}

/** The fix the next enroute entry continues from: the last waypoint before the arrival, or the origin. */
function continueFrom(fms: ScriptedFms) {
  const legs = fms.route.legs.slice(0, fms.enrouteEnd(fms.route));
  const last = [...legs].reverse().find(leg => leg.kind === "wpt");
  return last?.kind === "wpt" ? last.ident : fms.route.origin;
}

function procedureLabel(p: Procedure) {
  return p.kind === "APPROACH" ? `${p.approachType ?? ""} ${p.runways[0]?.slice(2) ?? ""}`.trim() : p.ident;
}

/** Rows of a procedure list: the procedures, or once one is chosen, it and its transitions. */
function procedureRows(procedures: Procedure[], choice: { ident: string; transition?: string } | undefined) {
  const chosen = choice ? procedures.find(p => p.ident === choice.ident) : undefined;
  if (!chosen) return procedures.map(p => ({ kind: "proc" as const, ident: p.ident, label: procedureLabel(p), on: false }));
  return [
    { kind: "proc" as const, ident: chosen.ident, label: procedureLabel(chosen), on: true },
    ...Object.keys(chosen.transitions).map(t => ({ kind: "trans" as const, ident: t, label: t, on: choice?.transition === t })),
  ];
}

const listPages = (...lengths: number[]) => Math.max(1, ...lengths.map(n => Math.ceil(n / 5)));

export const PLANNING_PAGES: Record<PlanningPageId, Page> = {
  RTE: {
    pages: fms => 1 + listPages(routeSegments(fms.route).length + 1),
    render: (fms, index) => {
      const route = fms.route;
      const count = 1 + listPages(routeSegments(route).length + 1);
      const footer: Line = fms.routeStatus === "MOD" ? { left: back("ERASE"), right: prompt("LEGS>") } : { right: prompt("LEGS>") };
      if (index === 0)
        return [
          title("RTE 1", `1/${count}`, fms.routeStatus),
          caption(" ORIGIN", "DEST "),
          { left: { text: route.origin }, right: { text: route.dest } },
          caption(" CO ROUTE", "FLT NO "),
          { left: { text: route.coRoute }, right: { text: route.flightNo } },
          caption(" RUNWAY"),
          { left: route.runway ? { text: route.runway } : dashes(5) },
          undefined, undefined, undefined,
          { right: prompt("SAVE ROUTE>") },
          { left: dashes(24) },
          footer,
        ];
      const segments = routeSegments(route);
      const lines: (Line | undefined)[] = [title("RTE 1", `${index + 1}/${count}`, fms.routeStatus), caption(" VIA", "TO ")];
      for (let row = 0; row < 5; row += 1) {
        const at = (index - 1) * 5 + row;
        const segment = segments[at];
        if (segment) lines[2 + row * 2] = segment.disco ? { center: small("DISCONTINUITY") } : { left: medium(segment.via), right: { text: segment.to } };
        else if (at === segments.length)
          lines[2 + row * 2] = fms.pendingVia ? { left: { text: fms.pendingVia }, right: boxes(5) } : { left: dashes(5), right: dashes(5) };
      }
      lines[11] = { left: dashes(24) };
      lines[12] = footer;
      return lines;
    },
    lsk: (fms, side, row, scratch, index): LskResult => {
      if (row === 6) {
        if (side === "R") fms.open("LEGS");
        else if (fms.routeStatus === "MOD") fms.eraseModification();
        return;
      }
      if (index === 0) {
        if (side === "R" && row === 5) { fms.saveCompanyRoute(); fms.advisory("ROUTE SAVED"); return; }
        const field = side === "L" ? (row === 1 ? "origin" : row === 2 ? "coRoute" : row === 3 ? "runway" : null)
          : row === 1 ? "dest" : row === 2 ? "flightNo" : null;
        if (!field) return;
        if (!scratch) { fms.setScratch(fms.route[field] ?? ""); return; }
        if (scratch === "DELETE") return "not-allowed";
        if ((field === "origin" || field === "dest") && !ICAO.test(scratch)) return "invalid";
        if ((field === "origin" || field === "dest") && !fms.navdb.airport(scratch)) return "not-in-database";
        if (field === "runway" && !/^RW\d{2}[LRC]?$/.test(scratch)) return "invalid";
        if (scratch.length > 10) return "invalid";
        // A company route name loads the stored route.
        if (field === "coRoute" && fms.loadCompanyRoute(scratch)) { fms.setScratch(""); return; }
        fms.modify(route => { route[field] = scratch; });
        fms.setScratch("");
        return;
      }
      const segments = routeSegments(fms.route);
      const at = (index - 1) * 5 + row - 1;
      const segment = segments[at];
      if (segment) {
        if (!scratch) { fms.setScratch(side === "L" ? segment.via : segment.to); return; }
        if (scratch !== "DELETE") return "not-allowed";
        fms.modify(route => { route.legs.splice(segment.from, segment.to_ - segment.from + 1); });
        fms.setScratch("");
        return;
      }
      if (at !== segments.length || !scratch) return;
      if (side === "L") {
        // VIA: an airway the previous fix is on; the TO fix then completes the segment.
        if (scratch === "DIRECT") { fms.pendingVia = null; fms.setScratch(""); return; }
        const airway = fms.navdb.airway(scratch);
        if (!airway) return "not-in-database";
        if (!airway.fixes.includes(continueFrom(fms))) return "invalid";
        fms.pendingVia = scratch;
        fms.setScratch("");
        return;
      }
      return fms.enterWaypoint(scratch, ident => {
        const via = fms.pendingVia;
        let legs: Leg[];
        if (via) {
          const fixes = fms.navdb.airwaySegment(via, continueFrom(fms), ident);
          if (!fixes) return "invalid";
          legs = fixes.map(fix => ({ kind: "wpt", ident: fix, via }));
        } else legs = [{ kind: "wpt", ident }];
        fms.modify(route => { route.legs.splice(fms.enrouteEnd(route), 0, ...legs); });
        fms.pendingVia = null;
        fms.setScratch("");
      });
    },
  },

  DEP_ARR: {
    pages: () => 1,
    render: fms => [
      title("DEP/ARR INDEX", "1/1"),
      undefined,
      { left: prompt("<DEP"), center: { text: fms.route.origin }, right: small("ARR>") },
      undefined,
      { center: { text: fms.route.dest }, right: prompt("ARR>") },
    ],
    lsk: (fms, side, row) => {
      if (side === "L" && row === 1) fms.open("DEPARTURES");
      if (side === "R" && (row === 1 || row === 2)) fms.open("ARRIVALS");
    },
  },

  DEPARTURES: {
    pages: fms => {
      const sids = fms.navdb.proceduresFor(fms.route.origin, "SID");
      return listPages(procedureRows(sids, fms.route.sid).length, fms.navdb.airport(fms.route.origin)?.runways.length ?? 0);
    },
    render: (fms, index) => {
      const route = fms.route;
      const sids = fms.navdb.proceduresFor(route.origin, "SID").filter(p => !route.runway || p.runways.includes(route.runway) || route.sid?.ident === p.ident);
      const left = procedureRows(sids, route.sid);
      const runways = fms.navdb.airport(route.origin)?.runways ?? [];
      const count = listPages(left.length, runways.length);
      const lines: (Line | undefined)[] = [title(`${route.origin} DEPARTURES`, `${index + 1}/${count}`), caption(" SIDS", "RUNWAYS ")];
      for (let row = 0; row < 5; row += 1) {
        const l = left[index * 5 + row], r = runways[index * 5 + row];
        if (l?.kind === "trans" && left[index * 5 + row - 1]?.kind !== "trans") lines[1 + row * 2] = { ...lines[1 + row * 2], left: small(" TRANS", "green") };
        lines[2 + row * 2] = {
          left: l ? selected(l.label, l.on, "L") : undefined,
          right: r ? selected(r.ident, route.runway === r.ident, "R") : undefined,
        };
      }
      lines[11] = { left: dashes(24) };
      lines[12] = fms.routeStatus === "MOD" ? { left: back("ERASE"), right: prompt("LEGS>") } : { left: back("INDEX"), right: prompt("LEGS>") };
      return lines;
    },
    lsk: (fms, side, row, _scratch, index) => {
      const route = fms.route;
      if (row === 6) {
        if (side === "R") fms.open("LEGS");
        else if (fms.routeStatus === "MOD") fms.eraseModification();
        else fms.open("DEP_ARR");
        return;
      }
      if (side === "R") {
        const runway = fms.navdb.airport(route.origin)?.runways[index * 5 + row - 1];
        if (runway) fms.selectRunway(runway.ident);
        return;
      }
      const sids = fms.navdb.proceduresFor(route.origin, "SID").filter(p => !route.runway || p.runways.includes(route.runway) || route.sid?.ident === p.ident);
      const item = procedureRows(sids, route.sid)[index * 5 + row - 1];
      if (!item) return;
      if (item.kind === "proc") fms.selectProcedure("SID", item.on ? null : item.ident);
      else fms.selectProcedure("SID", route.sid!.ident, item.on ? undefined : item.ident);
    },
  },

  ARRIVALS: {
    pages: fms => {
      const route = fms.route;
      return listPages(procedureRows(fms.navdb.proceduresFor(route.dest, "STAR"), route.star).length,
        procedureRows(fms.navdb.proceduresFor(route.dest, "APPROACH"), route.approach).length);
    },
    render: (fms, index) => {
      const route = fms.route;
      const left = procedureRows(fms.navdb.proceduresFor(route.dest, "STAR"), route.star);
      const right = procedureRows(fms.navdb.proceduresFor(route.dest, "APPROACH"), route.approach);
      const count = listPages(left.length, right.length);
      const lines: (Line | undefined)[] = [title(`${route.dest} ARRIVALS`, `${index + 1}/${count}`), caption(" STARS", "APPROACHES ")];
      for (let row = 0; row < 5; row += 1) {
        const l = left[index * 5 + row], r = right[index * 5 + row];
        const firstTrans = (list: typeof left, i: number) => list[i]?.kind === "trans" && list[i - 1]?.kind !== "trans";
        if (firstTrans(left, index * 5 + row) || firstTrans(right, index * 5 + row))
          lines[1 + row * 2] = caption(firstTrans(left, index * 5 + row) ? " TRANS" : undefined, firstTrans(right, index * 5 + row) ? "TRANS " : undefined);
        lines[2 + row * 2] = { left: l ? selected(l.label, l.on, "L") : undefined, right: r ? selected(r.label, r.on, "R") : undefined };
      }
      lines[11] = { left: dashes(24) };
      lines[12] = fms.routeStatus === "MOD" ? { left: back("ERASE"), right: prompt("LEGS>") } : { left: back("INDEX"), right: prompt("LEGS>") };
      return lines;
    },
    lsk: (fms, side, row, _scratch, index) => {
      const route = fms.route;
      if (row === 6) {
        if (side === "R") fms.open("LEGS");
        else if (fms.routeStatus === "MOD") fms.eraseModification();
        else fms.open("DEP_ARR");
        return;
      }
      const kind = side === "L" ? "STAR" : "APPROACH";
      const choice = side === "L" ? route.star : route.approach;
      const item = procedureRows(fms.navdb.proceduresFor(route.dest, kind), choice)[index * 5 + row - 1];
      if (!item) return;
      if (item.kind === "proc") fms.selectProcedure(kind, item.on ? null : item.ident);
      else fms.selectProcedure(kind, choice!.ident, item.on ? undefined : item.ident);
    },
  },

  NAV_DATA: {
    pages: fms => Math.max(1, fms.navDataQuery ? fms.navdb.find(fms.navDataQuery).length : 1),
    render: (fms, index) => {
      const entries = fms.navDataQuery ? fms.navdb.find(fms.navDataQuery) : [];
      const entry = entries[index];
      const lines: (Line | undefined)[] = [title("REF NAV DATA", `${index + 1}/${Math.max(1, entries.length)}`), caption(" IDENT", "TYPE ")];
      lines[2] = { left: entry ? { text: entry.ident } : boxes(5), right: entry ? medium(entry.kind === "navaid" ? entry.type : entry.kind === "airport" ? "AIRPORT" : "WAYPOINT") : undefined };
      if (entry) {
        const position = formatPosition(entry.position);
        lines[3] = caption(" LATITUDE", "LONGITUDE ");
        lines[4] = { left: medium(position.slice(0, 7)), right: medium(position.slice(7)) };
        if (entry.kind === "navaid") {
          lines[5] = caption(" FREQ");
          lines[6] = { left: { text: entry.frequency } };
          lines[7] = caption(" NAME");
          lines[8] = { left: medium(entry.name.slice(0, 24)) };
        }
        if (entry.kind === "airport") {
          lines[5] = caption(" ELEV");
          lines[6] = { left: { text: `${entry.elevation}FT` } };
          lines[7] = caption(" NAME");
          lines[8] = { left: medium(entry.name.slice(0, 24)) };
          lines[9] = caption(" RUNWAYS");
          lines[10] = { left: medium(entry.runways.map(r => r.ident.slice(2)).join(" ").slice(0, 24)) };
        }
      }
      // An ident not in the database can be defined here, in the temporary database, by its position.
      if (!entry && fms.navDataQuery) {
        lines[2] = { left: { text: fms.navDataQuery }, right: small("NOT IN DATA BASE", "amber") };
        lines[3] = caption(" DEFINE POSITION");
        lines[4] = { left: boxes(15) };
      }
      lines[11] = { left: small(` NAV DATA ${fms.activeCycle.id}`, "green") };
      lines[12] = { left: back("INDEX") };
      return lines;
    },
    lsk: (fms, side, row, scratch) => {
      if (side === "L" && row === 6) { fms.open("INIT_REF"); return; }
      if (side === "L" && row === 2 && scratch && fms.navDataQuery && !fms.navdb.find(fms.navDataQuery).length) {
        const resolved = /^[NS]\d{2}/.test(scratch) ? fms.resolveWaypoint(scratch) : "invalid";
        if (typeof resolved === "string" || "select" in resolved) return "invalid";
        const at = fms.coordinates(resolved.ident)!;
        fms.forgetPilot(resolved.ident);
        fms.defineTemporary(fms.navDataQuery, at);
        fms.setScratch("");
        return;
      }
      if (side !== "L" || row !== 1 || !scratch) return;
      if (!WAYPOINT.test(scratch)) return "invalid";
      fms.navDataQuery = scratch;
      fms.setScratch("");
    },
  },

  SELECT_WPT: {
    pages: () => 1,
    render: fms => {
      const pending = fms.selection;
      const entries = pending ? fms.navdb.find(pending.ident) : [];
      const lines: (Line | undefined)[] = [title("SELECT DESIRED WPT", "1/1")];
      entries.slice(0, 6).forEach((entry, i) => {
        lines[1 + i * 2] = caption(` ${entry.kind === "navaid" ? `${entry.type} ${entry.frequency}` : entry.kind === "airport" ? "AIRPORT" : "WAYPOINT"}`);
        lines[2 + i * 2] = { left: { text: `<${entry.ident}`, color: "cyan" }, right: medium(formatPosition(entry.position)) };
      });
      return lines;
    },
    lsk: (fms, _side, row) => fms.chooseEntry(row - 1),
  },

  SEC_FPLN: {
    pages: () => 1,
    render: fms => {
      const sec = fms.secondary;
      const wpts = sec?.legs.filter((leg): leg is Extract<Leg, { kind: "wpt" }> => leg.kind === "wpt") ?? [];
      return [
        title("SEC FPLN", "1/1"),
        caption(" ORIGIN", "DEST "),
        { left: sec ? { text: sec.origin } : dashes(4), right: sec ? { text: sec.dest } : dashes(4) },
        caption(" CO ROUTE", "LEGS "),
        { left: sec ? { text: sec.coRoute } : boxes(8), right: medium(String(wpts.length)) },
        caption(" FIRST WPT", "LAST WPT "),
        { left: { text: wpts[0]?.ident ?? "-----", color: "green" }, right: { text: wpts.at(-1)?.ident ?? "-----", color: "green" } },
        undefined,
        { left: prompt("<COPY ACTIVE") },
        undefined, undefined,
        { left: dashes(24) },
        { left: back("INDEX"), right: sec ? prompt("ACTIVATE>") : undefined },
      ];
    },
    lsk: (fms, side, row, scratch) => {
      if (side === "L" && row === 2 && scratch) {
        if (!fms.loadCompanyRoute(scratch, "secondary")) return "not-in-database";
        fms.setScratch("");
        return;
      }
      if (side === "L" && row === 4) { fms.copyActiveToSecondary(); return; }
      if (side === "L" && row === 6) { fms.open("INIT_REF", 1); return; }
      if (side === "R" && row === 6 && fms.activateSecondary()) fms.open("LEGS");
    },
  },
};
