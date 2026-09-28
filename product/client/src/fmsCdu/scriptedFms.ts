import { alert } from "./alerts";
import type { ConditionId } from "./conditions";
import { DATALINK_PAGES, DEMO_SMS, DEMO_UPLINKS } from "./datalinkPages";
import { CORE_PAGES } from "./fmsPages";
import {
  START_POSITION, WAYPOINT, arcLength, bearingIntersection, courseDeg, distanceNm, fromLocal, toLocal, holdEntry, maxSarGroundSpeed, offset,
  type Hold, type HoldEntry, type LatLon, type Leg, type LskResult, type Message, type Offset, type Page, type PageId, type Route, type Sar,
  type SarPattern, type Uplink,
} from "./fmsModel";
import { DEMO_COMPANY_ROUTES, DEMO_NAV_DATA, NavDatabase, type NavData, type NavEntry, type StoredRoute } from "./navData";
import { coldTemperatureCorrection, computeProfile, parseConstraint, type Profile } from "./vnav";
import { IRS_DRIFT_NM_PER_HOUR, RNP_DEFAULTS, selectSources, sourceError, type FlightPhase, type NavMode } from "./navigation";
import { NAV_PAGES } from "./navPages";
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

const PAGES: Record<PageId, Page> = { ...CORE_PAGES, ...PLANNING_PAGES, ...NAV_PAGES, ...TACTICAL_PAGES, ...DATALINK_PAGES };

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
  /** Where the aircraft really is; here is where the FMS believes it is. */
  private truth: LatLon = { ...START_POSITION };
  /** The FMS position error, NM east and north of the true position. */
  private error = { x: 0, y: 0 };
  private nav = {
    mode: "GPS" as NavMode, anp: 0.05, dmes: [] as string[], vor: null as string | null, rnpManual: null as number | null,
    unableSince: null as number | null, unableAlerted: false, integrityAlerted: false, approachIntegrityAlerted: false, armAlerted: false,
  };
  private armedApproach = false;
  /** Waypoints that move (a ship, a formation lead): position advanced by track and speed as time passes. */
  private moving: Record<string, { track: number; speed: number }> = {};
  private faults: { at: Date; text: string }[] = [];
  private selfTest: { startedAt: number | null; result: "PASS" | "FAIL" | null } = { startedAt: null, result: null };
  /** The other FMS: in dual operation every executed route is cross-loaded to it; in independent operation not. */
  private crossRoute: Route = demoRoute();
  /** Navigation database cycles: the active one first. The demonstration data is the same in both. */
  private cycles = [
    { id: "DEMO-2609", from: Date.UTC(2026, 8, 3), to: Date.UTC(2026, 8, 30, 23, 59) },
    { id: "DEMO-2610", from: Date.UTC(2026, 9, 1), to: Date.UTC(2026, 9, 28, 23, 59) },
  ];
  private outOfDateAlerted = false;
  /** The MOVING WPT page's entries before CREATE. */
  movingDraft = { ident: null as string | null, position: null as LatLon | null, motion: null as string | null };
  /** RNDZ: arrive at a waypoint at a time, flying the speed that needs within the limits. */
  readonly rndz = { wpt: null as string | null, time: null as number | null, minSpeed: 60, maxSpeed: 160, active: false, alerted: false };
  /** TDN: a tactical descent to an altitude a distance before a reference, if the angle is flyable. */
  readonly tdn = { targetAltitude: 500, refId: null as string | null, distanceBefore: 1.0, maxAngle: 6, active: false, level: false };
  private perf = { notEnoughAlerted: false, unableAlertedFor: null as string | null };
  private inhibited: string[] = [];
  private gpsSelected = true;
  private injected = new Set<ConditionId>();
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
  private aircraft = { track: courseDeg(START_POSITION, { lat: 45.2150, lon: -75.3900 }), groundSpeed: 120, altitude: 3000, verticalSpeed: 0, crossTrack: 0, trackError: 0 };
  /** Where the active leg starts: the last waypoint passed, or present position when a direct-to was executed. */
  private legStart: LatLon = { ...START_POSITION };
  private directPending = false;
  private directBypassed: string[] = [];

  get groundSpeed() { return this.aircraft.groundSpeed; }
  get altitude() { return this.aircraft.altitude; }
  get track() { return this.aircraft.track; }
  get verticalSpeed() { return this.aircraft.verticalSpeed; }
  /** Guidance deviations the flight simulation reports: cross-track NM (positive right) and track error degrees. */
  get crossTrack() { return this.aircraft.crossTrack; }
  get trackError() { return this.aircraft.trackError; }
  get activeLegStart() { return this.legStart; }
  /** The active route, whatever a pending modification shows on the pages. Guidance flies this one. */
  get activeRoute(): Route { return this.active; }

  readonly wind = { direction: 270, speed: 12 };
  /** Entries on the VNAV approach page. The FAF altitude sets the vertical path angle to the threshold. */
  readonly vnav = {
    mda: 560, fafAltitude: 1500, runwayElevation: 118, destTemp: null as number | null, qnh: null as string | null,
    /** The planned cruise, the descent path angle, and DES NOW (an early descent to capture the path). */
    cruiseAltitude: 4500, cruiseSpeed: 120, pathAngle: 3.0, desNow: false,
  };
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

  constructor(clock: () => Date = () => new Date()) {
    this.clock = clock;
    this.updateNavigation(0);
  }

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
      offset: "OFST", independent: "IND", gsmCall: "GSM", sms: "SMS",
      atcUplink: "ATC", tx1: "TX1", tx2: "TX2", vuhf: "V/UHF", hf: "HF", menuRequest: "MENU",
    };
    for (const [condition, lamp] of Object.entries(lampFor) as [ConditionId, Lamp][])
      if (this.hasCondition(condition)) lamps.add(lamp);
    // Navigation annunciators follow the navigation state: POS in dead reckoning, RNP when ANP exceeds RNP, NPA on a
    // non-precision approach. The bench can still force RNP and NPA on.
    if (this.nav.mode === "DR") lamps.add("POS");
    if (this.rnpExceeded) lamps.add("RNP");
    if (this.hasCondition("npa") || this.nonPrecisionApproach) lamps.add("NPA");
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
      case "offset": return this.active.offset !== undefined;
      case "gsmCall": return this.call.state !== "none";
      case "sms": return this.messages.some(message => !message.read);
      case "atcUplink": return this.uplinkList.some(uplink => uplink.response === "OPEN");
      default: return this.injected.has(id);
    }
  }

  setCondition(id: ConditionId, on: boolean) {
    if (on === this.hasCondition(id)) return;
    switch (id) {
      // Injected as an executed offset, as though the crew had entered and executed it.
      case "offset": if (on) this.active.offset = { nm: -2.0 }; else this.active.offset = undefined; break;
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
        if (on && ["fmsFail", "gpsLost", "gpsIntegrity", "dmeOutage", "independent"].includes(id)) {
          this.recordFault({ fmsFail: "FMS FAILURE", gpsLost: "GPS LOST", gpsIntegrity: "GPS INTEGRITY LOST", dmeOutage: "DME OUTAGE", independent: "X-SIDE SYNC LOST" }[id as string] ?? id);
        }
        // Leaving independent operation resynchronises the other FMS to this one.
        if (!on && id === "independent") this.crossRoute = structuredClone(this.active);
        if (on && id === "rnpExceeded") this.alert(alert("CHECK ANP"));
        if (id === "gpsLost" || id === "gpsIntegrity" || id === "dmeOutage") this.updateNavigation(0);
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
    if (at) { this.truth = { ...at }; this.here = this.withError(this.truth); }
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
    if (!leg || leg.kind === "disco") { this.alert(alert("END OF ROUTE")); return "end"; }
    // A conditional leg ends where its event happened: the next leg starts from here.
    if (leg.kind === "cond") { this.passLeg(null); return "route"; }
    const hold = route.hold;
    // A hold with a one-turn exit (HF) leaves at the first fix crossing after its entry.
    if (hold && hold.fix === leg.ident && hold.status === "IN PROGRESS" && hold.exit === "1 TURN") hold.status = "EXIT ARMED";
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
    this.passLeg(leg.ident);
    return route.legs.some(next => next.kind !== "disco") ? "route" : "end";
  }

  /** The search pattern has been flown to its end: the route continues after the search pattern waypoint. */
  completeSar() {
    const leg = this.active.legs[0];
    this.sar.active = null;
    this.sar.status = null;
    if (leg?.kind === "wpt" && leg.qualifier === "/S") this.passLeg(leg.ident);
  }

  /** Sequences the active leg: a waypoint (by ident) or, with null, a conditional leg that has ended. */
  private passLeg(ident: string | null) {
    const route = this.active;
    this.legStart = (ident && this.coordinates(ident)) || { ...this.here };
    if (ident) this.sequenced = ident;
    const passed = route.legs.shift();
    // A direct-to-fix leg is flown from wherever the aircraft is when it becomes active.
    const next = route.legs[0];
    if (next?.kind === "wpt" && next.path === "DF") this.legStart = { ...this.here };
    // The offset ends at its end waypoint, where the aircraft returns to the route.
    if (ident && route.offset?.end === ident) route.offset = undefined;
    if (!ident) {
      const pendingCond = this.modified?.legs[0];
      if (pendingCond?.kind === "cond") this.modified?.legs.shift();
      if (!route.legs.some(l => l.kind !== "disco")) this.alert(alert("END OF ROUTE"));
      return;
    }
    // Passing the runway starts the missed approach; its hold is armed so the aircraft holds at the end of it.
    if (passed?.kind === "wpt" && passed.source === "APPR" && /^RW\d{2}/.test(ident)) this.armMissedHold(route);
    const pending = this.modified?.legs[0];
    if (pending?.kind === "wpt" && pending.ident === ident) this.modified?.legs.shift();
    if (!route.legs.some(next => next.kind === "wpt")) this.alert(alert("END OF ROUTE"));
  }

  /** The flight simulation reports the aircraft's state after each step. */
  setAircraft(state: Partial<{ position: LatLon; track: number; groundSpeed: number; altitude: number; verticalSpeed: number; crossTrack: number; trackError: number }>) {
    // The simulation reports where the aircraft really is; the FMS position is that plus its navigation error.
    if (state.position) { this.truth = state.position; this.here = this.withError(this.truth); }
    const { position: _position, ...rest } = state;
    Object.assign(this.aircraft, rest);
  }

  // ------------------------------------------------------------------ navigation sensors (navigation.ts)

  private withError(at: LatLon) {
    const nm = Math.hypot(this.error.x, this.error.y);
    if (nm < 1e-6) return { ...at };
    return offset(at, (Math.atan2(this.error.x, this.error.y) * 180) / Math.PI, nm);
  }

  /**
   * Chooses the navigation source and updates the FMS position error and ANP, then checks ANP against RNP. In
   * dead reckoning the error grows with inertial drift; when a better source returns the position jumps back,
   * which the FMS reports as a POSITION SHIFT.
   */
  updateNavigation(dt: number) {
    const inputs = {
      gpsAvailable: !this.injected.has("gpsLost") && this.gpsSelected,
      gpsIntegrity: !this.injected.has("gpsIntegrity"),
      dmeAvailable: !this.injected.has("dmeOutage"),
      inhibited: this.inhibited,
    };
    const previous = this.nav.mode;
    const selection = selectSources(this.db.nearby(this.truth, 160), this.truth, this.altitude, inputs);
    if (selection.mode === "DR") {
      const drift = sourceError("DR");
      const grow = (IRS_DRIFT_NM_PER_HOUR * dt) / 3600;
      this.error = { x: this.error.x + grow * Math.sin((drift.bearing * Math.PI) / 180), y: this.error.y + grow * Math.cos((drift.bearing * Math.PI) / 180) };
    } else {
      const target = sourceError(selection.mode);
      const next = { x: target.nm * Math.sin((target.bearing * Math.PI) / 180), y: target.nm * Math.cos((target.bearing * Math.PI) / 180) };
      if (Math.hypot(next.x - this.error.x, next.y - this.error.y) > 0.5) this.alert(alert("POSITION SHIFT"));
      this.error = next;
    }
    this.here = this.withError(this.truth);
    const errorNm = Math.hypot(this.error.x, this.error.y);
    this.nav = {
      ...this.nav, mode: selection.mode, dmes: selection.dmes.map(d => d.ident), vor: selection.vor?.ident ?? null,
      anp: selection.mode === "DR" ? Math.max(0.1, errorNm * 1.3 + 0.05) : selection.baseAnp,
    };
    if (previous === "GPS" && selection.mode !== "GPS") this.alert(alert("GPS NAV LOST"));
    if (selection.mode === "GPS" && !inputs.gpsIntegrity && !this.nav.integrityAlerted) { this.nav.integrityAlerted = true; this.alert(alert("GPS POS UNCERTAIN")); }
    if (inputs.gpsIntegrity) this.nav.integrityAlerted = false;

    // ANP above RNP for longer than the time to alert of the phase: CHECK ANP, once per episode.
    const now = this.now.getTime();
    const { alertSeconds } = RNP_DEFAULTS[this.flightPhase];
    if (this.nav.anp > this.requiredRnp) {
      this.nav.unableSince ??= now;
      if (!this.nav.unableAlerted && now - this.nav.unableSince >= alertSeconds * 1000) { this.nav.unableAlerted = true; this.alert(alert("CHECK ANP")); }
    } else { this.nav.unableSince = null; this.nav.unableAlerted = false; }

    const faf = findProcedure(this.db, this.active, "APPROACH")?.faf;
    const first = this.active.legs[0];
    const fafPosition = faf ? this.coordinates(faf) : undefined;
    const nearFaf = first?.kind === "wpt" && first.ident === faf && fafPosition !== undefined && distanceNm(this.here, fafPosition) <= 2;
    if (nearFaf && !this.armedApproach) {
      if (!this.nav.armAlerted) { this.nav.armAlerted = true; this.alert(alert("ARM APPROACH")); }
    } else if (!nearFaf) this.nav.armAlerted = false;

    // On an RNAV approach, a position without GPS integrity is not good enough to continue.
    const approach = findProcedure(this.db, this.active, "APPROACH");
    const rnavApproach = approach?.approachType === "RNAV" && this.flightPhase === "APPROACH";
    if (rnavApproach && (selection.mode !== "GPS" || !inputs.gpsIntegrity)) {
      if (!this.nav.approachIntegrityAlerted) { this.nav.approachIntegrityAlerted = true; this.alert(alert("NO APPR INTEGRITY")); }
    } else this.nav.approachIntegrityAlerted = false;
  }

  /** The phase that sets the default RNP: approach on an approach leg, terminal within 30 NM of either airport. */
  get flightPhase(): FlightPhase {
    const leg = this.active.legs[0];
    if (leg && leg.kind !== "disco" && leg.source === "APPR") return "APPROACH";
    const near = (icao: string) => { const airport = this.db.airport(icao); return airport !== undefined && distanceNm(this.here, airport.position) <= 30; };
    return near(this.active.origin) || near(this.active.dest) ? "TERMINAL" : "EN ROUTE";
  }

  // ------------------------------------------------------------------ vertical profile and predictions (vnav.ts)

  /** Ground speed on a course, from the true airspeed and the wind. */
  groundSpeedOn(course: number, tas = this.plannedSpeed) {
    return Math.max(30, tas - this.wind.speed * Math.cos(((this.wind.direction - course) * Math.PI) / 180));
  }

  /** The cold temperature correction to the FAF altitude, from the destination temperature on VNAV (0 at or above ISA). */
  get coldCorrection() {
    const { destTemp, fafAltitude, runwayElevation } = this.vnav;
    return destTemp === null ? 0 : coldTemperatureCorrection(fafAltitude - runwayElevation, destTemp, runwayElevation);
  }

  get fafAltitudeCorrected() { return this.vnav.fafAltitude + this.coldCorrection; }

  /** The speed limit at the active fix or in the hold, if any. */
  private get speedLimit() {
    const leg = this.active.legs[0];
    const constraint = leg?.kind === "wpt" ? leg.speed : undefined;
    const hold = this.active.hold?.status === "IN PROGRESS" ? this.active.hold.speed : undefined;
    return Math.min(constraint ?? Infinity, hold ?? Infinity);
  }

  /** The planned speed: cruise, within the speed limit. The predictions (and so the rendezvous) use it. */
  get plannedSpeed() { return Math.min(this.vnav.cruiseSpeed, this.speedLimit); }

  /** The true airspeed flown: the planned speed, or during an active rendezvous the speed that arrives on time. */
  get targetSpeed() {
    const rendezvous = this.rndz.active ? this.rendezvous()?.speed : undefined;
    return rendezvous === undefined ? this.plannedSpeed : Math.min(rendezvous, this.speedLimit);
  }

  /**
   * The planned vertical profile and predictions along the active route: altitude, ETA and fuel at each waypoint,
   * the top and end of descent, and the first climb constraint that cannot be met. The E/D is the runway.
   */
  profile(route: Route = this.active): Profile {
    const geometry = this.legGeometry(route);
    // The descent meets the approach: the final approach fix is crossed at its (temperature-corrected) altitude.
    const runwayAt = route.legs.findIndex(leg => leg.kind === "wpt" && /^RW\d{2}/.test(leg.ident));
    const waypoints = route.legs.flatMap((leg, i) => {
      if (leg.kind !== "wpt") return [];
      const g = geometry[i];
      const constraint = i === runwayAt - 1 ? { kind: "AT" as const, altitude: this.fafAltitudeCorrected } : parseConstraint(leg.altitude);
      return [{
        ident: leg.ident, legDistance: g?.distance ?? 0, groundSpeed: this.groundSpeedOn(g?.course ?? this.track),
        constraint, endOfDescent: i === runwayAt,
      }];
    });
    return computeProfile({
      waypoints, altitude: this.altitude, cruiseAltitude: this.vnav.cruiseAltitude, climbRate: 1000, pathAngle: this.vnav.pathAngle,
      fuel: this.fuel.quantity, fuelFlow: this.fuel.flow, now: this.now.getTime(),
    });
  }

  /**
   * Burns fuel for dt seconds and checks the plan: FUEL RESERVE when fuel on board reaches the reserve, NOT ENOUGH
   * FUEL when the prediction at the destination is below it, UNABLE NEXT ALTITUDE (the manual’s wording is not in
   * the alert list, so the advisory is used) when a climb constraint cannot be made.
   */
  updatePerformance(dt: number) {
    for (const [ident, motion] of Object.entries(this.moving)) {
      const at = this.points[ident];
      if (at) this.points[ident] = offset(at, motion.track, (motion.speed * dt) / 3600);
    }
    const rendezvous = this.rndz.active ? this.rendezvous() : null;
    if (rendezvous && !rendezvous.achievable && !this.rndz.alerted) { this.rndz.alerted = true; this.alert(alert("RENDEZVOUS UNACHIEVABLE")); }
    if (rendezvous?.achievable) this.rndz.alerted = false;
    // At the target altitude the descent ends and the aircraft levels there until the crew cancels it.
    if (this.tdn.active && this.altitude <= this.tdn.targetAltitude + 20) { this.tdn.active = false; this.tdn.level = true; }
    const before = this.fuel.quantity;
    this.fuel.quantity = Math.max(0, before - (this.fuel.flow * dt) / 3600);
    if (before > this.fuel.reserve && this.fuel.quantity <= this.fuel.reserve) this.alert(alert("FUEL RESERVE"));
    const profile = this.profile();
    const atDestination = profile.points.at(-1)?.fuel;
    if (atDestination !== undefined && atDestination < this.fuel.reserve) {
      if (!this.perf.notEnoughAlerted) { this.perf.notEnoughAlerted = true; this.alert(alert("NOT ENOUGH FUEL")); }
    } else this.perf.notEnoughAlerted = false;
    if (profile.unableNext && profile.unableNext !== this.perf.unableAlertedFor) { this.perf.unableAlertedFor = profile.unableNext; this.advisory("UNABLE NEXT ALT"); }
    if (!profile.unableNext) this.perf.unableAlertedFor = null;
  }

  // ------------------------------------------------------------------ tactical: rendezvous, moving waypoints, TDN

  /**
   * The rendezvous: the distance along the route to the waypoint, the time left, and the speed that arrives on time.
   * Achievable when that speed is within the limits; the flown speed is then held within them.
   */
  rendezvous(): { distance: number; required: number; speed: number; achievable: boolean; eta: number } | null {
    const { wpt, time } = this.rndz;
    if (!wpt || time === null) return null;
    const point = this.profile().points.find(p => p.ident === wpt);
    if (!point) return null;
    const hours = (time - this.now.getTime()) / 3_600_000;
    const required = hours > 0 ? point.distance / hours : Infinity;
    const speed = Math.min(this.rndz.maxSpeed, Math.max(this.rndz.minSpeed, required));
    return { distance: point.distance, required, speed, achievable: required >= this.rndz.minSpeed && required <= this.rndz.maxSpeed, eta: point.eta };
  }

  /** Defines a moving waypoint at a position, moving on a track at a speed. */
  defineMoving(ident: string, position: LatLon, track: number, speed: number) {
    this.points[ident] = position;
    this.moving[ident] = { track, speed };
    this.pilot = [...this.pilot.filter(p => p.ident !== ident), { ident, position, definition: `MOVING ${String(track).padStart(3, "0")}/${speed}KT` }];
  }

  get movingWaypoints() { return this.moving; }

  /** The TDN path angle from present altitude to the target altitude at the point before the reference. */
  tdnAngle(): number | null {
    const ref = this.tdn.refId ? this.coordinates(this.tdn.refId) : undefined;
    if (!ref) return null;
    const run = distanceNm(this.here, ref) - this.tdn.distanceBefore;
    const drop = this.altitude - this.tdn.targetAltitude;
    if (run <= 0 || drop <= 0) return null;
    return (Math.atan(drop / (run * 6076.12)) * 180) / Math.PI;
  }

  /** EXECUTE TDN: begins the tactical descent, or says TDN NOT POSSIBLE when the angle is too steep or undefined. */
  executeTdn(): boolean {
    const angle = this.tdnAngle();
    if (angle === null || angle > this.tdn.maxAngle) { this.alert(alert("TDN NOT POSSIBLE")); return false; }
    this.tdn.active = true;
    this.tdn.level = false;
    return true;
  }

  /** CANCEL TDN: ends the descent, or the level-off at its altitude, and hands the altitude back to VNAV. */
  cancelTdn() {
    this.tdn.active = false;
    this.tdn.level = false;
  }

  // ------------------------------------------------------------------ navigation database cycles

  get activeCycle() { return this.cycles[0]; }
  get inactiveCycle() { return this.cycles[1] ?? null; }

  /** Swaps the active and inactive cycles, as the crew does on IDENT when a new cycle becomes effective. */
  swapCycles() {
    if (this.cycles.length < 2) return;
    this.cycles = [this.cycles[1], this.cycles[0]];
    this.outOfDateAlerted = false;
  }

  /** Removes a pilot waypoint that was only a step in defining another (a WPTnn made from a position entry). */
  forgetPilot(ident: string) {
    delete this.points[ident];
    this.pilot = this.pilot.filter(p => p.ident !== ident);
  }

  /** Defines a waypoint in the temporary database, from REF NAV DATA: an ident of its own at a position. */
  defineTemporary(ident: string, position: LatLon) {
    this.points[ident] = position;
    this.pilot = [...this.pilot.filter(p => p.ident !== ident), { ident, position, definition: "TEMP DB" }];
  }

  // ------------------------------------------------------------------ maintenance and dual operation

  get faultLog() { return this.faults; }
  get selfTestState() { return this.selfTest; }
  get otherFms() { return this.crossRoute; }

  /** Whether the other FMS holds the same active route: always in dual operation, not necessarily when independent. */
  get crossSideInSync() {
    const signature = (route: Route) => JSON.stringify(route.legs.map(leg => (leg.kind === "wpt" ? leg.ident : leg.kind)));
    return signature(this.crossRoute) === signature(this.active);
  }

  /** SELF TEST: runs for five seconds, then passes unless a fault condition is present. */
  startSelfTest() { this.selfTest = { startedAt: this.now.getTime(), result: null }; }

  private recordFault(text: string) { this.faults = [{ at: this.now, text }, ...this.faults].slice(0, 20); }

  /** On an approach that is not an ILS: the NPA annunciator. */
  get nonPrecisionApproach() {
    return this.flightPhase === "APPROACH" && findProcedure(this.db, this.active, "APPROACH")?.approachType !== "ILS";
  }

  /**
   * The approach the crew is flying, as the FMA and VNAV page name it: an ILS; an RNAV approach to LPV minima on
   * GPS with integrity (SBAS is assumed available); and no approach guidance without GPS integrity.
   */
  get approachType(): "ILS" | "LPV" | "NO APPR" | null {
    const approach = findProcedure(this.db, this.active, "APPROACH") ?? findProcedure(this.db, this.route, "APPROACH");
    if (!approach) return null;
    if (approach.approachType === "ILS") return "ILS";
    return this.nav.mode !== "GPS" || this.injected.has("gpsIntegrity") ? "NO APPR" : "LPV";
  }

  get approachArmed() { return this.armedApproach; }

  /** APPR: arms the approach; it becomes active on the final approach. */
  armApproach(on = true) { this.armedApproach = on; this.emit(); }

  /**
   * TOGA: a go-around before the runway. The rest of the approach is dropped and the missed approach becomes the
   * active route from present position, its hold armed.
   */
  goAround() {
    const route = this.active;
    const missed = route.legs.findIndex(leg => leg.kind !== "disco" && leg.source === "MISSED");
    // Only from the approach: once the missed approach is being flown there is nothing left to go around from.
    if (missed <= 0) return false;
    route.legs.splice(0, missed);
    this.legStart = { ...this.here };
    this.armedApproach = false;
    this.armMissedHold(route);
    this.emit();
    return true;
  }

  private armMissedHold(route: Route) {
    const missedHold = findProcedure(this.db, route, "APPROACH")?.missedHold;
    if (!missedHold || route.hold) return;
    route.hold = { fix: missedHold.fix, turn: missedHold.turn, inbound: missedHold.inbound, legTime: 1, legDistance: null, exit: "MANUAL", speed: 180, altitude: missedHold.altitude, status: "ARMED" };
    for (const leg of route.legs) if (leg.kind === "wpt" && leg.ident === missedHold.fix) leg.qualifier = "/H";
  }

  /** RNP in force: the entry the crew made, or the default for the phase of flight. */
  get requiredRnp() { return this.nav.rnpManual ?? RNP_DEFAULTS[this.flightPhase].rnp; }
  get navState() { return this.nav; }
  get truePosition() { return this.truth; }
  get inhibitedNavaids() { return this.inhibited; }
  get gpsNavSelected() { return this.gpsSelected; }
  /** Whether ANP has exceeded RNP (the RNP annunciator), computed or injected. */
  get rnpExceeded() { return this.injected.has("rnpExceeded") || this.nav.anp > this.requiredRnp; }

  /** A manual RNP (PROGRESS), or null to return to the default for the phase. */
  setRnp(rnp: number | null) {
    this.nav.rnpManual = rnp;
    if (rnp !== null && rnp > RNP_DEFAULTS[this.flightPhase].rnp) this.alert(alert("VERIFY RNP VALUE"));
    this.updateNavigation(0);
  }

  /** NAV OPTIONS: navaids excluded from position updating, and GPS selected in or out. */
  setInhibited(idents: string[]) { this.inhibited = idents.slice(0, 3); this.updateNavigation(0); }
  selectGps(on: boolean) { this.gpsSelected = on; this.updateNavigation(0); }

  /** Advances time-driven state: the timer alarms, the call duration and the clocks on the display. */
  tick() {
    const now = this.now.getTime();
    // A database past the end of its cycle is flagged once; swapping to the next cycle clears it.
    if (now > this.activeCycle.to && !this.outOfDateAlerted) { this.outOfDateAlerted = true; this.alert(alert("DATABASE OUT OF DATE")); }
    if (this.selfTest.startedAt !== null && this.selfTest.result === null && now - this.selfTest.startedAt >= 5000) {
      const failing = ["fmsFail", "gpsLost", "dmeOutage"].some(id => this.injected.has(id as ConditionId));
      this.selfTest = { ...this.selfTest, result: failing ? "FAIL" : "PASS" };
    }
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
  /** The lateral offset the pages show: the modification's if there is one, otherwise the active route's. */
  get lateralOffset() { return this.route.offset; }
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
    this.cycles = [{ id: data.cycle.id, from: this.now.getTime(), to: this.now.getTime() + 28 * 86_400_000 }, ...this.cycles];
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
    const arrival = legs.findIndex(leg => leg.kind !== "disco" && (leg.source === "STAR" || leg.source === "APPR" || leg.source === "MISSED"));
    if (arrival >= 0) return arrival;
    const last = legs.at(-1);
    return last?.kind === "wpt" && last.ident === route.dest ? legs.length - 1 : legs.length;
  }

  /** Course and distance into each leg, from present position. There is no computed leg after a discontinuity. */
  legGeometry(route: Route = this.route): LegGeometry[] {
    let from: LatLon | null = this.here;
    return route.legs.map(leg => {
      // After a gap or a conditional leg the start of the next leg is not known in advance.
      if (leg.kind !== "wpt") { from = null; return null; }
      const to = this.coordinates(leg.ident) ?? null;
      let result = from && to ? { course: courseDeg(from, to), distance: distanceNm(from, to) } : null;
      if (result && from && to && leg.path === "RF" && leg.arc) result = { course: result.course, distance: arcLength(from, to, leg.arc) };
      if (result && leg.path === "CF" && leg.course !== undefined) result = { ...result, course: leg.course };
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
    this.directBypassed = [];
  }

  setScratch(text: string) { this.scratch = text.slice(0, COLUMNS); }
  setRadio(key: keyof ScriptedFms["radios"], value: string) { this.radios[key] = value; }
  setFuel(key: keyof ScriptedFms["fuel"], value: number) { this.fuel[key] = value; }
  /** Enters, changes or (with null) deletes the lateral offset, as a modification to execute. */
  setOffset(change: Partial<Offset> | null) {
    this.modify(route => {
      if (change === null) route.offset = undefined;
      else if (change.nm !== undefined || route.offset) route.offset = { ...(route.offset ?? { nm: 0 }), ...change };
    });
  }

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
    if (at < 0 && !this.coordinates(ident)) return "not-in-database";
    this.directPending = true;
    // A direct-to must not silently lose the points it bypasses (the Cali lesson): they are kept for ABEAM PTS.
    this.directBypassed = at > 0 ? this.route.legs.slice(0, at).flatMap(leg => (leg.kind === "wpt" ? [leg.ident] : [])) : [];
    if (at >= 0) {
      this.modify(route => {
        route.legs.splice(0, at);
        const first = route.legs[0];
        // Direct from present position: whatever path the leg had (a published course, an arc) no longer applies.
        if (first?.kind === "wpt") route.legs[0] = { ...first, path: undefined, course: undefined, arc: undefined };
      });
      return;
    }
    this.modify(route => { route.legs.unshift({ kind: "wpt", ident }, { kind: "disco" }); });
  }

  /** Whether the pending modification is a direct-to, for the INTC CRS and ABEAM PTS prompts. */
  get directModification() { return this.directPending && this.modified !== null; }
  get bypassedByDirect() { return this.directBypassed; }

  /** INTC CRS: fly the entered course into the active waypoint instead of direct from present position. */
  interceptCourse(course: number) {
    this.modify(route => {
      const leg = route.legs[0];
      if (leg?.kind === "wpt") route.legs[0] = { ...leg, path: "CF", course };
    });
  }

  /** ABEAM PTS: each waypoint the direct-to bypassed becomes a point abeam it on the new direct track. */
  abeamPoints() {
    const target = this.route.legs[0];
    const to = target?.kind === "wpt" ? this.coordinates(target.ident) : undefined;
    if (!to || !this.directBypassed.length) return;
    const direct = toLocal(this.here, to);
    const points = this.directBypassed.flatMap(ident => {
      const at = this.coordinates(ident);
      if (!at) return [];
      const p = toLocal(this.here, at);
      const along = (p.x * direct.x + p.y * direct.y) / (direct.x ** 2 + direct.y ** 2);
      if (along <= 0 || along >= 1) return [];
      const position = fromLocal(this.here, { x: direct.x * along, y: direct.y * along });
      return [{ kind: "wpt" as const, ident: this.createPilot(ident.slice(0, 3), position, `ABEAM ${ident}`) }];
    });
    this.modify(route => { route.legs.splice(0, 0, ...points); });
    this.directBypassed = [];
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
    this.directBypassed = [];
    this.active = route;
    this.modified = null;
    // In dual operation the executed route is cross-loaded to the other FMS.
    if (!this.injected.has("independent")) this.crossRoute = structuredClone(route);
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
