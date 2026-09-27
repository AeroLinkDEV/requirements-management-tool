import { alert } from "./alerts";
import type { ConditionId } from "./conditions";
import { DATALINK_PAGES, DEMO_SMS, DEMO_UPLINKS } from "./datalinkPages";
import { CORE_PAGES } from "./fmsPages";
import {
  NAV_DATABASE, START_POSITION, courseDeg, distanceNm, holdEntry, maxSarGroundSpeed, offset,
  type Hold, type HoldEntry, type LatLon, type Leg, type LskResult, type Message, type Page, type PageId, type Route, type Sar,
  type SarPattern, type Uplink,
} from "./fmsModel";
import { COLUMNS, compose, type CduBackend, type CduScreen, type Lamp, type Line } from "./screen";
import { TACTICAL_PAGES } from "./tacticalPages";
import type { CduFunction } from "./variants";

/**
 * A scripted CMA-9000 for engineers to exercise the panel before the real operational program is connected.
 *
 * It follows the Operator's Manual rules for the keys (scratchpad entry, CLR, DELETE, +/-, line select entry and
 * copy, MOD/ACT with EXEC and ERASE, PREV/NEXT, BRT, the MSG annunciator) and for the pages it models: direct-to,
 * holds with their standard entry, the VNAV approach path, the search patterns, the tactical approach and the
 * timer. Courses and distances come from a small demonstration navigation database, and the aircraft only moves
 * when the bench sequences it to the next waypoint. It is labelled as a simulation on its IDENT page and is not a
 * navigation computer.
 */

const PAGES: Record<PageId, Page> = { ...CORE_PAGES, ...TACTICAL_PAGES, ...DATALINK_PAGES };

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

  readonly groundSpeed = 120;
  readonly altitude = 3000;
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
    const route = this.active;
    if (route.legs[0]?.kind === "disco") route.legs.shift();
    const leg = route.legs[0];
    if (!leg || leg.kind !== "wpt") { this.alert(alert("END OF ROUTE")); this.emit(); return; }
    const at = this.coordinates(leg.ident);
    const hold = route.hold;
    if (hold && hold.fix === leg.ident && hold.status !== "EXIT ARMED") {
      if (hold.status === "ARMED") {
        this.enteredHold = this.holdEntryFor(route);
        hold.status = "IN PROGRESS";
        if (hold.speed > MAX_HOLDING_SPEED) this.alert(alert("HIGH HOLDING SPEED"));
      }
      if (at) this.here = at;
      this.emit();
      return;
    }
    if (hold && hold.fix === leg.ident) { route.hold = undefined; this.enteredHold = null; }
    if (leg.qualifier === "/S" && this.sar.active) {
      this.sar.status = "IN PROGRESS";
      if (at) this.here = at;
      this.emit();
      return;
    }
    if (at) this.here = at;
    this.sequenced = leg.ident;
    route.legs.shift();
    const pending = this.modified?.legs[0];
    if (pending?.kind === "wpt" && pending.ident === leg.ident) this.modified?.legs.shift();
    if (!route.legs.some(next => next.kind === "wpt")) this.alert(alert("END OF ROUTE"));
    this.emit();
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
    return this.points[ident] ?? this.marks.find(mark => mark.ident === ident)?.position ?? NAV_DATABASE[ident];
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
