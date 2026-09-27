import { alert } from "./alerts";
import type { ConditionId } from "./conditions";
import { DATALINK_PAGES, DEMO_SMS, DEMO_UPLINKS } from "./datalinkPages";
import { CORE_PAGES } from "./fmsPages";
import {
  START_POSITION, WAYPOINT, bearingIntersection, courseDeg, distanceNm, holdEntry, maxSarGroundSpeed, offset,
  type Hold, type HoldEntry, type LatLon, type Leg, type LskResult, type Message, type Page, type PageId, type Route, type Sar,
  type SarPattern, type Uplink,
} from "./fmsModel";
import { DEMO_COMPANY_ROUTES, DEMO_NAV_DATA, NavDatabase, type NavData, type NavEntry, type StoredRoute } from "./navData";
import { PLANNING_PAGES } from "./planningPages";
import { composeRoute, enrouteLegs, findProcedure } from "./procedures";
import { COLUMNS, compose, type CduBackend, type CduScreen, type Lamp, type Line } from "./screen";
import { TACTICAL_PAGES } from "./tacticalPages";
import type { CduFunction } from "./variants";

/**
 * A scripted CMA-9000 for engineers to exercise the panel before the real operational program is connected.
 *
 * It follows the Operator's Manual rules for the keys (scratchpad entry, CLR, DELETE, +/-, line select entry and
 * copy, MOD/ACT with EXEC and ERASE, PREV/NEXT, BRT, the MSG annunciator) and for the pages it models: direct-to,
 * holds with their standard entry, the VNAV approach path, the search patterns, the tactical approach and the
 * timer. Courses and distances come from a small demonstration navigation database. The aircraft is flown by the
 * flight simulation (flight.ts), which reports its state here and calls arrive() at each waypoint passage. It is
 * labelled as a simulation on its IDENT page and is not a navigation computer.
 */

const PAGES: Record<PageId, Page> = { ...CORE_PAGES, ...PLANNING_PAGES, ...TACTICAL_PAGES, ...DATALINK_PAGES };

export type WaypointResolution = { ident: string } | { select: string } | "invalid" | "not-in-database";

const wpt = (ident: string, altitude?: string): Leg => ({ kind: "wpt", ident, altitude });

const demoRoute = (): Route => ({
  origin: "CYOW", dest: "CYUL", coRoute: "OWUL1", flightNo: "LIFE21",
  legs: [wpt("MUN", "3000"), wpt("RDG", "4500"), wpt("TOLGU", "4500"), wpt("FERDI", "1500A"), wpt("RW24R", "168"), wpt("CYUL")],
});

/** The ICAO maximum holding speed up to 14 000 ft. */
const MAX_HOLDING_SPEED = 230;

export type LegGeometry = { course: number; distance: number } | null;

export class ScriptedFms implements CduBackend {
  private listeners = new Set<() => void>();
  private changes = 0;
  private page: PageId = "IDENT";
  private index = 0;
  private scratch = "";
  private message: Message | null = null;
  private unacknowledged = false;
  private recall: Message[] = [];
  private active: Route = demoRoute();
  private modified: Route | null = null;
  private radios = { com1: "121.500", com1Stby: "126.700", com2: "119.100", com2Stby: "133.600", nav1: "113.90", nav2: "116.70", adf: "0350", tpdr: "1200" };
  private fuel = { quantity: 1850, flow: 540, reserve: 400 };
  private marks: { ident: string; position: LatLon }[] = [];
  private points: Record<string, LatLon> = {};
  private level = 6;
  private readonly maxLevel = 10;
  private brighten = true;
  private lastBrt = -Infinity;
  private squawkIdentUntil = 0;
  private here: LatLon = { ...START_POSITION };
  private injected = new Set<ConditionId>();
  private offsetNm: number | null = null;
  private call: { state: "none" | "ringing" | "active"; from: string; since: number } = { state: "none", from: "", since: 0 };
  private messages: { from: string; text: string; at: Date; read: boolean }[] = [];
  private uplinkList: Uplink[] = [];
  private uplinksSent = 0;
  private enteredHold: HoldEntry | null = null;
  private sequenced: string | null = null;

  private db = new NavDatabase(DEMO_NAV_DATA);
  /** Which of several same-ident entries the crew chose on SELECT DESIRED WPT. */
  private chosen: Record<string, number> = {};
  private selectPending: { ident: string; apply: (ident: string) => LskResult; back: { page: PageId; index: number } } | null = null;
  private pilot: { ident: string; position: LatLon; definition: string }[] = [];
  private companyRoutes: StoredRoute[] = structuredClone(DEMO_COMPANY_ROUTES);
  private secondaryRoute: Route | null = null;
  /** The ident shown on REF NAV DATA, and an airway chosen on RTE 2 waiting for its TO fix. */
  navDataQuery: string | null = null;
  pendingVia: string | null = null;
  private aircraft = { track: courseDeg(START_POSITION, { lat: 45.2150, lon: -75.3900 }), groundSpeed: 120, altitude: 3000, verticalSpeed: 0 };
  /** Where the active leg starts: the last waypoint passed, or present position when a direct-to was executed. */
  private legStart: LatLon = { ...START_POSITION };
  private directPending = false;

  get groundSpeed() { return this.aircraft.groundSpeed; }
  get altitude() { return this.aircraft.altitude; }
  get track() { return this.aircraft.track; }
  get verticalSpeed() { return this.aircraft.verticalSpeed; }
  get activeLegStart() { return this.legStart; }
  /** The active route, whatever a pending modification shows on the pages. Guidance flies this one. */
  get activeRoute(): Route { return this.active; }

  readonly wind = { direction: 270, speed: 12 };
  /** Entries on the VNAV approach page. The FAF altitude sets the vertical path angle to the threshold. */
  readonly vnav = { mda: 560, fafAltitude: 1500, runwayElevation: 118, destTemp: null as number | null, qnh: null as string | null };
  readonly timer = { alarmAt: null as number | null, countdownEnd: null as number | null };
  readonly sar: Sar = {
    id: { SQUARE: "SQR01", LADDER: "LAD01", SECTOR: "SEC01" }, refId: null, relativeBearing: null, distance: null,
    trackSpacing: 2.0, legLength: 4.0, diameter: 4.0, angle: 30, sarBearing: 90, pending: null, active: null, status: null,
  };
  readonly tact = { refId: null as string | null };
  readonly tactAppr = {
    refId: null as string | null, bearing: 90, iafDistance: 8.0, fafDistance: 4.0, mapDistance: 0.5, vpa: -3.0,
    iafAltitude: 2500, runwayElevation: 118, transitionLevel: 180,
  };
  readonly datalink = { routeRequest: "NONE" as "NONE" | "RECEIVED" | "LOADED", windRequest: "NONE" as "NONE" | "RECEIVED", posReport: null as Date | null };

  private readonly clock: () => Date;

  constructor(clock: () => Date = () => new Date()) { this.clock = clock; }

  // ------------------------------------------------------------------ CduBackend

  subscribe(listener: () => void) {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }

  brightness() { return this.level / this.maxLevel; }

  revision() { return this.changes; }

  lamps(): ReadonlySet<Lamp> {
    // A failed FMS drives nothing but its FAIL annunciator.
    if (this.injected.has("fmsFail")) return new Set<Lamp>(["FAIL"]);
    const lamps = new Set<Lamp>();
    if (this.unacknowledged) lamps.add("MSG");
    if (this.modified) lamps.add("EXEC");
    const lampFor: Partial<Record<ConditionId, Lamp>> = {
      gpsLost: "POS", rnpExceeded: "RNP", npa: "NPA", offset: "OFST", independent: "IND", gsmCall: "GSM", sms: "SMS",
      atcUplink: "ATC", tx1: "TX1", tx2: "TX2", vuhf: "V/UHF", hf: "HF", menuRequest: "MENU",
    };
    for (const [condition, lamp] of Object.entries(lampFor) as [ConditionId, Lamp][])
      if (this.hasCondition(condition)) lamps.add(lamp);
    return lamps;
  }

  screen(): CduScreen {
    if (this.injected.has("fmsFail")) return compose([]);
    const page = PAGES[this.page];
    const count = Math.max(1, page.pages(this));
    this.index = Math.min(this.index, count - 1);
    const lines = page.render(this, this.index);
    lines[13] = this.scratchLine();
    return compose(lines);
  }

  press(fn: CduFunction, options: { held?: boolean } = {}) {
    if (this.injected.has("fmsFail")) return;
    this.handle(fn, options);
    this.emit();
  }

  // ------------------------------------------------------------------ test bench

  /** Adds a system alert message, as the real FMS does for an alert condition. */
  raiseAlert(text: string) {
    this.alert(text);
    this.emit();
  }

  hasCondition(id: ConditionId): boolean {
    switch (id) {
      case "offset": return this.offsetNm !== null;
      case "gsmCall": return this.call.state !== "none";
      case "sms": return this.messages.some(message => !message.read);
      case "atcUplink": return this.uplinkList.some(uplink => uplink.response === "OPEN");
      default: return this.injected.has(id);
    }
  }

  setCondition(id: ConditionId, on: boolean) {
    if (on === this.hasCondition(id)) return;
    switch (id) {
      case "offset": this.offsetNm = on ? -2.0 : null; break;
      case "gsmCall": this.call = on ? { state: "ringing", from: "+1 613 555 0142", since: this.now.getTime() } : { state: "none", from: "", since: 0 }; break;
      case "sms":
        if (on) this.messages.unshift({ ...DEMO_SMS[this.messages.length % DEMO_SMS.length], at: this.now, read: false });
        else for (const message of this.messages) message.read = true;
        break;
      case "atcUplink":
        if (on) {
          const text = DEMO_UPLINKS[this.uplinksSent % DEMO_UPLINKS.length];
          this.uplinksSent += 1;
          this.uplinkList.unshift({ id: this.uplinksSent, at: this.now, text, response: "OPEN" });
        } else this.uplinkList = this.uplinkList.filter(uplink => uplink.response !== "OPEN");
        break;
      default:
        if (on) this.injected.add(id); else this.injected.delete(id);
        if (on && id === "gpsLost") this.alert(alert("GPS NAV LOST"));
        if (on && id === "rnpExceeded") this.alert(alert("CHECK ANP"));
        if (on && id === "independent") this.alert(alert("INDEPENDENT OP"));
        // The FMS restarts on its IDENT page when it comes back.
        if (!on && id === "fmsFail") { this.open("IDENT"); this.message = null; }
    }
    this.emit();
  }

  /**
   * Flies the aircraft to the active waypoint and sequences it, as crossing the waypoint would. A hold or a search
   * pattern at that waypoint is entered instead, and each further call flies one more circuit until the pilot exits.
   */
  sequence() {
    if (this.injected.has("fmsFail")) return;
    if (this.active.legs[0]?.kind === "disco") this.active.legs.shift();
    const leg = this.active.legs[0];
    const at = leg?.kind === "wpt" ? this.coordinates(leg.ident) : undefined;
    if (at) this.here = { ...at };
    this.arrive();
    this.emit();
  }

  /**
   * The aircraft has reached the active waypoint. It is sequenced, unless it is the fix of a hold that is not armed
   * to exit (the hold is entered, or another circuit begins) or the start of the active search pattern. Returns what
   * the aircraft flies next; the flight simulation calls this at each waypoint passage.
   */
  arrive(): "route" | "hold" | "sar" | "end" {
    const route = this.active;
    const leg = route.legs[0];
    if (!leg || leg.kind !== "wpt") { this.alert(alert("END OF ROUTE")); return "end"; }
    const hold = route.hold;
    if (hold && hold.fix === leg.ident && hold.status !== "EXIT ARMED") {
      if (hold.status === "ARMED") {
        this.enteredHold = this.holdEntryFor(route);
        hold.status = "IN PROGRESS";
        if (hold.speed > MAX_HOLDING_SPEED) this.alert(alert("HIGH HOLDING SPEED"));
      }
      return "hold";
    }
    if (hold && hold.fix === leg.ident) { route.hold = undefined; this.enteredHold = null; }
    if (leg.qualifier === "/S" && this.sar.active) {
      this.sar.status = "IN PROGRESS";
      return "sar";
    }
    this.pass(leg.ident);
    return route.legs.some(next => next.kind === "wpt") ? "route" : "end";
  }

  /** The search pattern has been flown to its end: the route continues after the search pattern waypoint. */
  completeSar() {
    const leg = this.active.legs[0];
    this.sar.active = null;
    this.sar.status = null;
    if (leg?.kind === "wpt" && leg.qualifier === "/S") this.pass(leg.ident);
  }

  private pass(ident: string) {
    const route = this.active;
    this.legStart = this.coordinates(ident) ?? { ...this.here };
    this.sequenced = ident;
    const passed = route.legs.shift();
    // Passing the runway starts the missed approach; its hold is armed so the aircraft holds at the end of it.
    const missedHold = findProcedure(this.db, route, "APPROACH")?.missedHold;
    if (passed?.kind === "wpt" && passed.source === "APPR" && /^RW\d{2}/.test(ident) && missedHold && !route.hold) {
      route.hold = { fix: missedHold.fix, turn: missedHold.turn, inbound: missedHold.inbound, legTime: 1, legDistance: null, exit: "MANUAL", speed: 180, altitude: missedHold.altitude, status: "ARMED" };
      for (const leg of route.legs) if (leg.kind === "wpt" && leg.ident === missedHold.fix) leg.qualifier = "/H";
    }
    const pending = this.modified?.legs[0];
    if (pending?.kind === "wpt" && pending.ident === ident) this.modified?.legs.shift();
    if (!route.legs.some(next => next.kind === "wpt")) this.alert(alert("END OF ROUTE"));
  }

  /** The flight simulation reports the aircraft's state after each step. */
  setAircraft(state: Partial<{ position: LatLon; track: number; groundSpeed: number; altitude: number; verticalSpeed: number }>) {
    if (state.position) this.here = state.position;
    const { position: _position, ...rest } = state;
    Object.assign(this.aircraft, rest);
  }

  /** Advances time-driven state: the timer alarms, the call duration and the clocks on the display. */
  tick() {
    const now = this.now.getTime();
    if (this.timer.alarmAt !== null && now >= this.timer.alarmAt) { this.timer.alarmAt = null; this.alert(alert("TIMER ALARM")); }
    if (this.timer.countdownEnd !== null && now >= this.timer.countdownEnd) { this.timer.countdownEnd = null; this.alert(alert("TIMER ALARM")); }
    this.emit();
  }

  // ------------------------------------------------------------------ state read by pages

  get route(): Route { return this.modified ?? this.active; }
  get routeStatus(): "ACT" | "MOD" { return this.modified ? "MOD" : "ACT"; }
  get now() { return this.clock(); }
  get position(): LatLon { return this.here; }
  get radioState() { return this.radios; }
  get fuelState() { return this.fuel; }
  get markList() { return this.marks; }
  get recallList() { return this.recall; }
  get squawkIdent() { return this.clock().getTime() < this.squawkIdentUntil; }
  get lateralOffset() { return this.offsetNm; }
  get callState() { return this.call; }
  get smsList() { return this.messages; }
  get uplinks() { return this.uplinkList; }
  get holdEntryFlown() { return this.enteredHold; }
  /** The last waypoint sequenced: once past the FAF, the approach still measures its path from it. */
  get lastSequenced() { return this.sequenced; }

  /** A waypoint's position: a search or tactical point, a Mark On Top, or the navigation database. */
  coordinates(ident: string): LatLon | undefined {
    const own = this.points[ident] ?? this.marks.find(mark => mark.ident === ident)?.position;
    if (own) return own;
    if (/^RW\d{2}[LRC]?$/.test(ident)) {
      const route = this.route;
      return (this.db.runway(ident, route.dest) ?? this.db.runway(ident, route.origin) ?? this.db.runway(ident))?.threshold;
    }
    return this.entryFor(ident)?.position;
  }

  /** The database entry an ident means: the only one, the one chosen on SELECT DESIRED WPT, or the nearest. */
  entryFor(ident: string): NavEntry | undefined {
    const entries = this.db.find(ident);
    if (entries.length <= 1) return entries[0];
    const chosen = this.chosen[ident];
    if (chosen !== undefined && entries[chosen]) return entries[chosen];
    return [...entries].sort((a, b) => distanceNm(this.here, a.position) - distanceNm(this.here, b.position))[0];
  }

  get navdb() { return this.db; }
  get pilotWaypoints() { return this.pilot; }
  get storedRoutes() { return this.companyRoutes; }
  get secondary() { return this.secondaryRoute; }
  get selection() { return this.selectPending; }

  /** Merges loaded navigation data (ARINC 424) over the database. */
  loadNavData(data: NavData) {
    this.db = this.db.merge(data);
    this.emit();
  }

  /**
   * Resolves a waypoint entry. A known ident is itself (a duplicate ident needs SELECT DESIRED WPT first). A latitude
   * and longitude (N4530.0W07530.0), a place/bearing/distance (RDG045/10) or a place-bearing/place-bearing
   * (RDG045/MUN090) entry creates a pilot waypoint, named WPTnn or after the place.
   */
  resolveWaypoint(text: string): WaypointResolution {
    if (WAYPOINT.test(text)) {
      if (!this.coordinates(text)) return "not-in-database";
      const own = this.points[text] !== undefined || this.marks.some(mark => mark.ident === text);
      if (!own && this.db.find(text).length > 1 && this.chosen[text] === undefined) return { select: text };
      return { ident: text };
    }
    const latLon = /^([NS])(\d{2})(\d{2}(?:\.\d)?)?([EW])(\d{3})(\d{2}(?:\.\d)?)?$/.exec(text);
    if (latLon) {
      const lat = Number(latLon[2]) + Number(latLon[3] ?? 0) / 60, lon = Number(latLon[5]) + Number(latLon[6] ?? 0) / 60;
      if (lat > 90 || lon > 180 || Number(latLon[3] ?? 0) >= 60 || Number(latLon[6] ?? 0) >= 60) return "invalid";
      return { ident: this.createPilot("WPT", { lat: latLon[1] === "S" ? -lat : lat, lon: latLon[4] === "W" ? -lon : lon }, text) };
    }
    const pbd = /^([A-Z0-9]{2,5})(\d{3})\/(\d{1,3}(?:\.\d)?)$/.exec(text);
    if (pbd) {
      const place = this.coordinates(pbd[1]);
      const bearing = Number(pbd[2]), distance = Number(pbd[3]);
      if (!place) return "not-in-database";
      if (bearing < 1 || bearing > 360 || distance <= 0) return "invalid";
      return { ident: this.createPilot(pbd[1].slice(0, 3), offset(place, bearing, distance), text) };
    }
    const pbpb = /^([A-Z0-9]{2,5})(\d{3})\/([A-Z0-9]{2,5})(\d{3})$/.exec(text);
    if (pbpb) {
      const p1 = this.coordinates(pbpb[1]), p2 = this.coordinates(pbpb[3]);
      if (!p1 || !p2) return "not-in-database";
      const crossing = bearingIntersection(p1, Number(pbpb[2]), p2, Number(pbpb[4]));
      if (!crossing) return "invalid";
      return { ident: this.createPilot(pbpb[1].slice(0, 3), crossing, text) };
    }
    return "invalid";
  }

  /** Stores a pilot waypoint under the next free name for its prefix (WPT01, RDG01...). */
  createPilot(prefix: string, position: LatLon, definition: string) {
    let n = 1;
    let ident = `${prefix}${String(n).padStart(2, "0")}`;
    while (this.coordinates(ident)) { n += 1; ident = `${prefix}${String(n).padStart(2, "0")}`; }
    this.points[ident] = position;
    this.pilot.push({ ident, position, definition });
    return ident;
  }

  /** Resolves a waypoint entry and uses it, going through SELECT DESIRED WPT first for a duplicate ident. */
  enterWaypoint(text: string, apply: (ident: string) => LskResult): LskResult {
    const resolved = this.resolveWaypoint(text);
    if (typeof resolved === "string") return resolved;
    if ("select" in resolved) {
      this.selectPending = { ident: resolved.select, apply, back: { page: this.page, index: this.index } };
      this.open("SELECT_WPT");
      return;
    }
    return apply(resolved.ident);
  }

  /** SELECT DESIRED WPT: the crew picks one of the same-ident entries, and the entry that asked continues. */
  chooseEntry(index: number): LskResult {
    const pending = this.selectPending;
    if (!pending || !this.db.find(pending.ident)[index]) return;
    this.chosen[pending.ident] = index;
    this.selectPending = null;
    this.open(pending.back.page, pending.back.index);
    return pending.apply(pending.ident);
  }

  /** Selects (or with null, removes) a SID, STAR or approach and its transition, rebuilding the route as a MOD. */
  selectProcedure(kind: "SID" | "STAR" | "APPROACH", ident: string | null, transition?: string) {
    this.modify(route => {
      const enroute = enrouteLegs(route);
      const choice = ident ? { ident, transition } : undefined;
      if (kind === "SID") route.sid = choice;
      else if (kind === "STAR") route.star = choice;
      else route.approach = choice;
      route.legs = composeRoute(route, this.db, enroute);
    });
  }

  selectRunway(ident: string) {
    this.modify(route => {
      route.runway = route.runway === ident ? undefined : ident;
      const sid = findProcedure(this.db, route, "SID");
      if (sid && route.runway && !sid.runways.includes(route.runway)) {
        const enroute = enrouteLegs(route);
        route.sid = undefined;
        route.legs = composeRoute(route, this.db, enroute);
      }
    });
  }

  /** Loads a company route into the active route (as a MOD) or into the secondary flight plan. */
  loadCompanyRoute(name: string, target: "active" | "secondary" = "active"): boolean {
    const stored = this.companyRoutes.find(route => route.name === name);
    if (!stored) return false;
    const build = (route: Route) => {
      route.origin = stored.origin;
      route.dest = stored.dest;
      route.coRoute = stored.name;
      route.sid = route.star = route.approach = undefined;
      route.hold = undefined;
      route.legs = [...stored.legs.map(leg => ({ kind: "wpt" as const, ...leg })), { kind: "wpt", ident: stored.dest }];
    };
    if (target === "active") this.modify(build);
    else { const route = structuredClone(this.secondaryRoute ?? this.active); build(route); this.secondaryRoute = route; }
    return true;
  }

  /** SAVE: stores the route's enroute legs under its CO ROUTE name, replacing a stored route of that name. */
  saveCompanyRoute() {
    const route = this.route;
    const legs = enrouteLegs(route).flatMap(leg => (leg.kind === "wpt" ? [{ ident: leg.ident, via: leg.via, altitude: leg.altitude }] : []));
    this.companyRoutes = [...this.companyRoutes.filter(stored => stored.name !== route.coRoute), { name: route.coRoute, origin: route.origin, dest: route.dest, legs }];
  }

  copyActiveToSecondary() { this.secondaryRoute = structuredClone({ ...this.active, hold: undefined }); }

  /** ACTIVATE on SEC FPLN: the secondary flight plan becomes a modification of the active route. */
  activateSecondary() {
    const secondary = this.secondaryRoute;
    if (!secondary) return false;
    this.modify(route => { Object.assign(route, structuredClone(secondary), { hold: undefined }); });
    return true;
  }

  /** Where new enroute legs go: before the arrival, the approach and missed approach, or the destination. */
  enrouteEnd(route: Route = this.route) {
    const legs = route.legs;
    const arrival = legs.findIndex(leg => leg.kind === "wpt" && (leg.source === "STAR" || leg.source === "APPR" || leg.source === "MISSED"));
    if (arrival >= 0) return arrival;
    const last = legs.at(-1);
    return last?.kind === "wpt" && last.ident === route.dest ? legs.length - 1 : legs.length;
  }

  /** Course and distance into each leg, from present position. There is no computed leg after a discontinuity. */
  legGeometry(route: Route = this.route): LegGeometry[] {
    let from: LatLon | null = this.here;
    return route.legs.map(leg => {
      if (leg.kind === "disco") { from = null; return null; }
      const to = this.coordinates(leg.ident) ?? null;
      const result = from && to ? { course: courseDeg(from, to), distance: distanceNm(from, to) } : null;
      from = to;
      return result;
    });
  }

  /** The entry the aircraft will fly (or flew) into the hold, from the track that arrives at the holding fix. */
  holdEntryFor(route: Route = this.route): HoldEntry | null {
    const hold = route.hold;
    if (!hold) return null;
    if (hold.status === "IN PROGRESS" || hold.status === "EXIT ARMED") return this.enteredHold;
    const at = route.legs.findIndex(leg => leg.kind === "wpt" && leg.ident === hold.fix);
    const track = at >= 0 ? this.legGeometry(route)[at]?.course : undefined;
    return track === undefined ? null : holdEntry(track, hold.inbound, hold.turn);
  }

  // ------------------------------------------------------------------ state changed by pages

  open(page: PageId, index = 0) { this.page = page; this.index = index; }

  /** Starts (or continues) a modification of the active route. */
  modify(change: (route: Route) => void) {
    const route = this.modified ?? structuredClone(this.active);
    change(route);
    this.modified = route;
  }

  /** The ERASE prompt: discards the modification; navigation never left the active route. */
  eraseModification() {
    this.modified = null;
    this.sar.pending = null;
    this.directPending = false;
  }

  setScratch(text: string) { this.scratch = text.slice(0, COLUMNS); }
  setRadio(key: keyof ScriptedFms["radios"], value: string) { this.radios[key] = value; }
  setFuel(key: keyof ScriptedFms["fuel"], value: number) { this.fuel[key] = value; }
  setOffset(nm: number | null) { this.offsetNm = nm; }

  /** The FAF altitude constraint, on the VNAV page and on the FAF leg of both the active and modified routes. */
  setFafAltitude(altitude: number) {
    this.vnav.fafAltitude = altitude;
    for (const route of [this.active, this.modified]) {
      const runwayAt = route?.legs.findIndex(leg => leg.kind === "wpt" && /^RW\d{2}/.test(leg.ident)) ?? -1;
      const faf = route?.legs[runwayAt - 1];
      if (faf?.kind === "wpt") faf.altitude = `${altitude}A`;
    }
  }
  definePoint(ident: string, position: LatLon) { this.points[ident] = position; }
  advisory(text: string) { this.message = { text, alert: false }; }

  alert(text: string) {
    const message = { text: text.toUpperCase().slice(0, COLUMNS), alert: true };
    this.recall.unshift(message);
    this.message = message;
    this.unacknowledged = true;
  }

  addMark() {
    const ident = `MRK${String(this.marks.length + 1).padStart(2, "0")}`;
    this.marks.push({ ident, position: { ...this.here } });
    return ident;
  }

  squawk() { this.squawkIdentUntil = this.clock().getTime() + 18_000; }

  /**
   * DIRECT-TO from present position. A waypoint already in the route deletes the legs before it; any other known
   * waypoint goes first, followed by a route discontinuity, as the manual shows.
   */
  directTo(ident: string): LskResult {
    const at = this.route.legs.findIndex(leg => leg.kind === "wpt" && leg.ident === ident);
    this.directPending = true;
    if (at >= 0) { this.modify(route => { route.legs.splice(0, at); }); return; }
    if (!this.coordinates(ident)) return "not-in-database";
    this.modify(route => { route.legs.unshift({ kind: "wpt", ident }, { kind: "disco" }); });
  }

  /** Defines a hold at a fix as a modification. A fix that is not in the route becomes the next waypoint. */
  defineHold(fix: string): LskResult {
    if (!this.coordinates(fix)) return "not-in-database";
    this.modify(route => {
      let at = route.legs.findIndex(leg => leg.kind === "wpt" && leg.ident === fix);
      if (at < 0) { route.legs.unshift({ kind: "wpt", ident: fix }); at = 0; }
      for (const leg of route.legs) if (leg.kind === "wpt" && leg.qualifier === "/H") delete leg.qualifier;
      const leg = route.legs[at];
      if (leg.kind === "wpt") leg.qualifier = "/H";
      // The inbound course defaults to the course of the leg into the fix.
      const inbound = this.legGeometry(route)[at]?.course ?? 360;
      route.hold = { fix, turn: "RIGHT", inbound, legTime: 1.0, legDistance: null, exit: "MANUAL", speed: 220, altitude: "5000A", status: "INACTIVE" };
    });
  }

  changeHold(change: (hold: Hold) => void) {
    this.modify(route => { if (route.hold) change(route.hold); });
  }

  eraseHold() {
    this.modify(route => {
      route.hold = undefined;
      for (const leg of route.legs) if (leg.kind === "wpt" && leg.qualifier === "/H") delete leg.qualifier;
    });
  }

  /** ACTIVATE> on a search pattern page: the pattern start goes in the modification, flown direct from PPOS. */
  activateSar(pattern: SarPattern) {
    const reference = (this.sar.refId && this.coordinates(this.sar.refId)) || this.here;
    const start = this.sar.distance ? offset(reference, this.sar.relativeBearing ?? 0, this.sar.distance) : reference;
    const ident = this.sar.id[pattern];
    this.definePoint(ident, start);
    this.modify(route => {
      route.legs = route.legs.filter(leg => !(leg.kind === "wpt" && leg.qualifier === "/S"));
      route.legs.unshift({ kind: "wpt", ident, qualifier: "/S" });
    });
    this.sar.pending = pattern;
    if (this.groundSpeed > maxSarGroundSpeed(this.sar, pattern)) this.alert(alert("HIGH SAR SPEED"));
  }

  /** INTERRUPT> leaves the active search pattern at once and continues with the rest of the route. */
  interruptSar() {
    this.active.legs = this.active.legs.filter(leg => !(leg.kind === "wpt" && leg.qualifier === "/S"));
    if (this.modified) this.modified.legs = this.modified.legs.filter(leg => !(leg.kind === "wpt" && leg.qualifier === "/S"));
    this.sar.active = null;
    this.sar.status = null;
  }

  /** Loads a route as a modification; used by the FMC COMM route uplink and the tactical approach. */
  replaceLegs(legs: Leg[]) { this.modify(route => { route.legs = structuredClone(legs); }); }

  answerCall() { this.call = { ...this.call, state: "active", since: this.now.getTime() }; }
  endCall() { this.call = { state: "none", from: "", since: 0 }; }
  readMessages() { for (const message of this.messages) message.read = true; }

  // ------------------------------------------------------------------ keys

  private emit() {
    this.changes += 1;
    for (const listener of this.listeners) listener();
  }

  private scratchLine(): Line {
    if (this.message) return { left: { text: this.message.text, color: this.message.alert ? "amber" : "white" } };
    return { left: { text: this.scratch, color: "white" } };
  }

  private execute() {
    const route = this.modified;
    if (!route) return;
    if (route.hold?.status === "INACTIVE") route.hold.status = "ARMED";
    if (this.sar.pending) { this.sar.active = this.sar.pending; this.sar.status = "ARMED"; this.sar.pending = null; }
    // A new active waypoint, or a direct-to, starts the active leg at present position.
    const first = (legs: Leg[]) => { const leg = legs[0]; return leg?.kind === "wpt" ? leg.ident : null; };
    if (this.directPending || first(route.legs) !== first(this.active.legs)) this.legStart = { ...this.here };
    this.directPending = false;
    this.active = route;
    this.modified = null;
  }

  private handle(fn: CduFunction, { held }: { held?: boolean }) {
    if (fn.startsWith("CHAR_")) return this.type(fn.slice(5));
    switch (fn) {
      case "SP": return this.type(" ");
      case "SLASH": return this.type("/");
      case "DOT": return this.type(".");
      case "PLUSMINUS": {
        this.clearMessageForEntry();
        const last = this.scratch.at(-1);
        if (last === "-") this.scratch = `${this.scratch.slice(0, -1)}+`;
        else if (last === "+") this.scratch = `${this.scratch.slice(0, -1)}-`;
        else this.type("-");
        return;
      }
      case "CLR": return this.clear(held === true);
      case "BRT": return this.brt();
      case "NEXT": case "PREV": {
        const count = Math.max(1, PAGES[this.page].pages(this));
        this.index = (this.index + (fn === "NEXT" ? 1 : count - 1)) % count;
        return;
      }
      case "EXEC": return this.execute();
      case "MENU": return this.open("MENU");
      case "INIT_REF": return this.open("INIT_REF");
      case "RTE": return this.open("RTE");
      case "DEP_ARR": return this.open("DEP_ARR");
      case "LEGS": return this.open("LEGS");
      case "PROG": return this.open("PROG");
      case "RADIO": return this.open("RADIO");
      case "TPDR": return this.open("RADIO", 1);
      case "FUEL": return this.open("FUEL");
      case "FIX": return this.open("FIX");
      case "VNAV": return this.open("VNAV");
      case "TACT": return this.open("TACT");
      case "ATC": return this.open("ATC");
      case "FMC_COMM": return this.open("FMC_COMM");
      case "MSG": return this.open("MSG_RECALL");
      case "ANS":
        // Item 20: ANS answers an incoming call and hangs up an active one; otherwise it shows the newest SMS.
        if (this.call.state === "ringing") this.answerCall();
        else if (this.call.state === "active") this.endCall();
        else this.readMessages();
        return this.open("ANS");
      case "HOLD":
        // Item 17: with no hold in the plan, LEGS with "/H" in the scratchpad for the holding fix.
        if (this.route.hold) return this.open("HOLD");
        this.open("LEGS");
        this.scratch = "/H";
        this.message = null;
        return;
      case "MARK":
        this.addMark();
        return this.open("PREDEF", 1);
      case "SQK_IDT":
        this.squawk();
        return this.advisory("TPDR IDENT");
    }
    const lsk = /^LSK([1-6])([LR])$/.exec(fn);
    if (lsk) return this.lineSelect(lsk[2] as "L" | "R", Number(lsk[1]));
  }

  private clearMessageForEntry() {
    if (this.message) { this.message = null; if (this.scratch === "DELETE") this.scratch = ""; }
  }

  private type(ch: string) {
    this.clearMessageForEntry();
    if (this.scratch === "DELETE") this.scratch = "";
    if (this.scratch.length < COLUMNS) this.scratch += ch;
  }

  private clear(held: boolean) {
    if (this.message) {
      // CLR clears alert and advisory messages from the scratchpad, which also acknowledges the alert.
      this.message = null;
      this.unacknowledged = false;
      return;
    }
    if (this.scratch === "DELETE") { this.scratch = ""; return; }
    if (!this.scratch) { this.scratch = "DELETE"; return; }
    this.scratch = held ? "" : this.scratch.slice(0, -1);
  }

  private brt() {
    // Manual item 13: after 5 s idle the first press always brightens; each press then alternates.
    const now = this.clock().getTime();
    if (now - this.lastBrt > 5000) this.brighten = true;
    else this.brighten = !this.brighten;
    this.lastBrt = now;
    this.level = Math.max(1, Math.min(this.maxLevel, this.level + (this.brighten ? 1 : -1)));
  }

  private lineSelect(side: "L" | "R", row: number) {
    const page = PAGES[this.page];
    if (!page.lsk) return;
    const scratch = this.message ? "" : this.scratch;
    const result = page.lsk(this, side, row, scratch, this.index);
    if (result === "invalid") this.advisory("INVALID ENTRY");
    else if (result === "not-in-database") this.advisory("NOT IN DATA BASE");
    else if (result === "not-allowed") this.advisory(scratch === "DELETE" ? "INVALID DELETE" : "NOT ALLOWED");
  }
}
