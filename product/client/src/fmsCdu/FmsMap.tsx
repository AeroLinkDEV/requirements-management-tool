import type { FmsSide } from "./crossTalk";
import { racetrackOutline, sarTrack, segmentsOutline, type FlightSimulator, type Guidance } from "./flight";
import { arcSweep, bearingDeg, distanceNm, longitudeDelta, offset, type LatLon, type Route } from "./fmsModel";
import type { ScriptedFms } from "./scriptedFms";
import { MAP_CF_EXTENT_NM, MAP_RADIUS_UNITS } from "./mapDrawing";
import "./FmsMap.css";

/**
 * A moving map of the inspected FMS's managed route, independent of AFCS coupling: north up and centred on the
 * aircraft, the active leg in magenta, later legs in white, a pending modification dashed, holds and search patterns
 * in cyan. It reads the scripted FMS and the flight simulation; it has no state of its own beyond the range.
 *
 * One computer per map (#1504, #1502 §19 item 1): everything is drawn from the inspected computer `fms` and its own
 * guidance `sim`. When another computer is guiding the aircraft, the one thing taken from it is a separate lavender marker
 * for the leg it is flying, labelled with its side ("FMS 2 guiding → TOLGU"), so the two are never mixed.
 */

type Computer = { side: FmsSide; fms: ScriptedFms; sim: FlightSimulator };
type Props = { fms: ScriptedFms; sim: FlightSimulator; side: FmsSide; guiding: Computer | null; range: number };

const R = MAP_RADIUS_UNITS; // the map is drawn in a -R..R box; the range ring at R is the selected range

/** What the guiding computer is flying, in words: its active waypoint on a route leg, otherwise its mode. */
function guidingLabel({ side, fms, sim }: Computer, arrow: string) {
  const to = fms.activeRoute.legs[0];
  return sim.guidance.mode === "LNAV" && to?.kind === "wpt" ? `FMS ${side} guiding ${arrow} ${to.ident}` : `FMS ${side} guiding, ${sim.guidance.mode} mode`;
}

/** The managed offset track: the leg moved sideways by the offset distance, independent of AFCS coupling. */
function offsetTrack(g: Guidance, shift: number): LatLon[] | null {
  if (!shift || !g.legFrom || !g.legTo || g.desiredTrack === null) return null;
  const side = g.desiredTrack + (shift > 0 ? 90 : -90);
  return [offset(g.legFrom, side, Math.abs(shift)), offset(g.legTo, side, Math.abs(shift))];
}

/**
 * The path the guiding computer is actually flying, or null when no line would be true: none in heading mode, and none
 * on an arc (RF/AF) leg, whose chord is not the path flown (the legend still names the leg). An offset is drawn offset.
 */
function guidingPath({ fms, sim }: Computer): LatLon[] | null {
  const g = sim.guidance, leg = fms.activeRoute.legs[0];
  if (g.mode === "HDG" || !g.legFrom || !g.legTo) return null;
  if (g.mode === "LNAV" && leg?.kind === "wpt" && (leg.path === "RF" || leg.path === "AF")) return null;
  // The separate guiding marker represents the flown pattern, not an offset of retained HOLD/SAR geometry.
  return (g.mode === "LNAV" ? offsetTrack(g, fms.activeRoute.offset?.nm ?? 0) : null) ?? [g.legFrom, g.legTo];
}

export default function FmsMap({ fms, sim, side, guiding, range }: Props) {
  const centre = fms.position;
  const scale = R / range;
  const project = (p: LatLon) => ({
    x: longitudeDelta(centre.lon, p.lon) * 60 * Math.cos((centre.lat * Math.PI) / 180) * scale,
    y: -(p.lat - centre.lat) * 60 * scale,
  });
  const path = (points: LatLon[]) => points.map((p, i) => { const q = project(p); return `${i ? "L" : "M"}${q.x.toFixed(1)},${q.y.toFixed(1)}`; }).join("");

  /** Polylines of a route from a starting point; a discontinuity breaks the line. */
  const routeLines = (start: LatLon, route: Route) => {
    const legs = route.legs;
    const lines: LatLon[][] = [];
    let current: LatLon[] = [start];
    for (const leg of legs) {
      // A gap, or a conditional leg with no fixed end, breaks the drawn line.
      if (leg.kind !== "wpt") { if (current.length > 1) lines.push(current); current = []; continue; }
      const at = leg.position ?? fms.coordinates(leg.ident, route);
      if (!at) continue;
      // An RF leg is drawn as its arc, not as the chord.
      const previous = current.at(-1);
      if ((leg.path === "RF" || leg.path === "AF") && leg.arc && previous) {
        const sweep = arcSweep(previous, at, leg.arc), radius = distanceNm(leg.arc.centre, at), start = bearingDeg(leg.arc.centre, previous);
        for (let i = 1; i < 12; i += 1) current.push(offset(leg.arc.centre, start + (leg.arc.turn === "R" ? 1 : -1) * (sweep * i) / 12, radius));
      }
      current.push(at);
    }
    if (current.length > 1) lines.push(current);
    return lines;
  };

  const active = fms.activeRoute;
  const activeTo = active.legs[0]?.kind === "wpt" ? fms.coordinates(active.legs[0].ident) : undefined;
  const activeWaypoint = active.legs[0];
  // A CF is the inbound course into its fix, except when the actual committed JN joining path is flown.
  const routeStart = activeTo && activeWaypoint?.kind === "wpt" && activeWaypoint.path === "CF" && activeWaypoint.course !== undefined
    && !(activeWaypoint.ident === "JN" && fms.hoverJoin)
    ? offset(activeTo, activeWaypoint.course + 180, MAP_CF_EXTENT_NM) : fms.activeLegStart;
  const [first, ...later] = routeLines(routeStart, active);
  const waypoints = active.legs.flatMap((leg, i) => {
    if (leg.kind !== "wpt") return [];
    const at = leg.position ?? fms.coordinates(leg.ident);
    return at ? [{ ident: leg.ident, at, active: i === 0 }] : [];
  });
  const modified = fms.routeStatus === "MOD" ? routeLines(fms.position, fms.route) : [];

  const hold = active.hold ?? (fms.routeStatus === "MOD" ? fms.route.hold : undefined);
  const holdFix = hold ? fms.coordinates(hold.fix) : undefined;
  const racetrack = hold && holdFix ? racetrackOutline(holdFix, hold, fms.groundSpeed, sim.tas, fms.wind.speed) : null;

  // Phase 1 of a hover procedure: its joining path, previewed while in MOD, then the committed one.
  const join = fms.hoverJoinPreview ?? fms.hoverJoin;
  const joinPath = join ? segmentsOutline(join.from, join.segments) : null;

  const sarStart = active.legs.find(leg => leg.kind === "wpt" && leg.qualifier === "/S");
  const sarPath = sim.sarPath
    ?? (fms.sar.active && sarStart?.kind === "wpt" && fms.coordinates(sarStart.ident) ? sarTrack(fms.coordinates(sarStart.ident)!, fms.sar, fms.sar.active) : null);

  // Navaids and airports within the range, as a navigation display shows them.
  const nearby = fms.navdb.nearby(centre, range * 1.6).filter(e => e.kind !== "fix").slice(0, 60);

  const drift = distanceNm(fms.truePosition, centre);
  const trueOffset = drift > 0.05 ? project(fms.truePosition) : null;

  const g = sim.guidance;
  const [firstLeg, ...laterFirst] = first ?? [];
  // Heading selection does not hide a valid managed leg. In a hold or search pattern the
  // guidance leg is active; the route resumes from the fix. Invalid managed guidance stays withdrawn.
  const onRoute = g.mode === "LNAV" || g.mode === "HDG" && g.desiredTrack !== null && !sim.holdProgress && !sim.sarPath;
  // The offset track actually flown, parallel to the active leg.
  const offsetLeg = offsetTrack(g, active.offset?.nm ?? 0);
  const activeLeg = onRoute && firstLeg && activeTo ? [firstLeg, laterFirst[0]] : null;

  // The other computer, when it is the one guiding the aircraft: only its flown path and its label.
  const other = guiding && guiding.side !== side ? guiding : null;
  const otherLeg = other ? guidingPath(other) : null;
  const otherTo = otherLeg ? project(otherLeg[1]) : null;

  return (
    <svg className="fmsMap" viewBox={`${-R * 1.6} ${-R - 20} ${R * 3.2} ${2 * R + 40}`} role="img"
      aria-label={`Navigation map, FMS ${side} inspected${guiding?.side === side ? " and guiding" : ""}, ${range} NM range, ${g.mode} mode${activeTo && active.legs[0]?.kind === "wpt" ? `, active waypoint ${active.legs[0].ident}` : ""}${other ? `; ${guidingLabel(other, "to")}` : ""}`}>
      <defs>
        <clipPath id="fmsMapClip"><rect x={-R * 1.6} y={-R - 20} width={R * 3.2} height={2 * R + 40} /></clipPath>
      </defs>
      <circle className="ring" r={R} />
      <circle className="ring half" r={R / 2} />
      <text className="ringLabel" x={4} y={-R + 12}>{range}</text>
      <text className="ringLabel" x={4} y={-R / 2 + 12}>{range / 2}</text>
      <text className="north" x={0} y={-R - 6}>N</text>

      <g clipPath="url(#fmsMapClip)">
        {nearby.map(entry => {
          const q = project(entry.position);
          return (
            <g key={`${entry.kind}:${entry.ident}:${entry.position.lat}`} transform={`translate(${q.x.toFixed(1)},${q.y.toFixed(1)})`} className={entry.kind === "airport" ? "airport" : "navaid"}>
              {entry.kind === "airport" ? <circle r={4} /> : <path d="M-4,0 L-2,-3.5 L2,-3.5 L4,0 L2,3.5 L-2,3.5 Z" />}
              <text x={6} y={-4}>{entry.ident}</text>
            </g>
          );
        })}
        {/* Under this computer's own paths, so where both computers fly the same leg only its magenta shows. */}
        {otherLeg && otherTo && other ? (
          <g className="guiding" data-testid="guiding-leg">
            <path d={path(otherLeg)} />
            <circle cx={otherTo.x.toFixed(1)} cy={otherTo.y.toFixed(1)} r={3.5} />
            <text x={(otherTo.x + 6).toFixed(1)} y={(otherTo.y + 14).toFixed(1)}>FMS {other.side}</text>
          </g>
        ) : null}
        {racetrack ? <path className="hold" d={path(racetrack)} /> : null}
        {sarPath ? <path className="sar" d={path(sarPath)} /> : null}
        {joinPath ? <path className={fms.hoverJoinPreview ? "modified" : "active"} d={path(joinPath)} data-testid="hover-join" /> : null}
        {modified.map((line, i) => <path key={`m${i}`} className="modified" d={path(line)} />)}
        {first && first.length > 2 ? <path className="later" d={path(first.slice(1))} /> : null}
        {later.map((line, i) => <path key={`l${i}`} className="later" d={path(line)} />)}
        {activeLeg ? <path className="active" d={path(activeLeg)} /> : null}
        {offsetLeg ? <path className="offset" d={path(offsetLeg)} /> : null}
        {g.legFrom && g.legTo && !onRoute ? <path className="active" d={path([g.legFrom, g.legTo])} /> : null}
        {waypoints.map(({ ident, at, active: isActive }) => {
          const q = project(at);
          return (
            <g key={ident} transform={`translate(${q.x.toFixed(1)},${q.y.toFixed(1)})`} className={`wpt${isActive ? " activeWpt" : ""}`}>
              <path d="M0,-5 L1.4,-1.4 L5,0 L1.4,1.4 L0,5 L-1.4,1.4 L-5,0 L-1.4,-1.4 Z" />
              <text x={7} y={4}>{ident}</text>
            </g>
          );
        })}
      </g>

      {/* The vantage and the guiding legend, after the clipped symbols so no navaid label can cover them. */}
      {/* "GUIDING" goes on a second line so the label never reaches the north marker. */}
      <text className="computer" x={-R * 1.6 + 6} y={-R - 4} data-testid="map-computer">
        FMS {side} · INSPECTED{guiding?.side === side ? <tspan x={-R * 1.6 + 6} dy={15}>GUIDING</tspan> : null}
      </text>
      {other ? (
        <g className="guiding legend" data-testid="guiding-label">
          <line x1={-R * 1.6 + 6} y1={R + 12} x2={-R * 1.6 + 22} y2={R + 12} />
          <text x={-R * 1.6 + 26} y={R + 16}>{guidingLabel(other, "→")}</text>
        </g>
      ) : null}

      {/* Where the aircraft really is, when the FMS position has drifted from it (a simulator view, not a display). */}
      {trueOffset ? (
        <g transform={`translate(${trueOffset.x.toFixed(1)},${trueOffset.y.toFixed(1)})`} className="truePosition">
          <path d="M-4,-4 L4,4 M-4,4 L4,-4" />
          <text x={6} y={4}>TRUE</text>
        </g>
      ) : null}

      {/* The aircraft, at the centre, pointing along its track, with a short track line ahead. */}
      <g transform={`rotate(${fms.track})`}>
        <line className="trackLine" x1={0} y1={-10} x2={0} y2={-R} />
        <path className="aircraft" d="M0,-9 L2,-2 L9,2 L9,4 L2,2 L1.5,7 L4,9 L4,10 L0,9 L-4,10 L-4,9 L-1.5,7 L-2,2 L-9,4 L-9,2 L-2,-2 Z" />
      </g>
    </svg>
  );
}
