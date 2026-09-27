import { COLUMNS, compose, type CduBackend, type CduScreen, type Lamp, type Line, type Segment } from "./screen";
import type { CduFunction } from "./variants";

/**
 * A scripted CMA-9000 for engineers to exercise the panel before the real operational program is connected.
 *
 * It follows the Operator's Manual rules for the keys that do not depend on navigation computation (scratchpad
 * entry, CLR, DELETE, +/-, line select entry and copy, MOD/ACT with EXEC and ERASE, PREV/NEXT, BRT, the MSG
 * annunciator) and shows representative pages whose values come from a fixed demonstration flight plan. It is
 * labelled as a simulation on its IDENT page. Nothing here computes real navigation.
 */

type Leg = { ident: string; course: number; distance: number; altitude?: string };
type Route = { origin: string; dest: string; coRoute: string; flightNo: string; runway?: string; legs: Leg[]; hold?: string };

type PageId =
  | "MENU" | "INIT_REF" | "IDENT" | "POS" | "MSG_RECALL" | "RTE" | "DEP_ARR" | "DEPARTURES" | "ARRIVALS" | "LEGS"
  | "PROG" | "RADIO" | "FUEL" | "HOLD" | "FIX" | "PREDEF" | "VNAV" | "TACT" | "ATC" | "FMC_COMM" | "ANS";

type Message = { text: string; alert: boolean };

type LskResult = void | "invalid" | "not-allowed";

type Page = {
  pages: (fms: ScriptedFms) => number;
  render: (fms: ScriptedFms, index: number) => (Line | undefined)[];
  /** row 1..6, side L or R, with the current scratchpad text. */
  lsk?: (fms: ScriptedFms, side: "L" | "R", row: number, scratch: string, index: number) => LskResult;
};

const pad = (value: string, length: number) => value.padEnd(length);
const fixed = (value: number, digits: number) => value.toFixed(digits);
const hhmm = (date: Date) => `${String(date.getUTCHours()).padStart(2, "0")}${String(date.getUTCMinutes()).padStart(2, "0")}.${Math.floor(date.getUTCSeconds() / 6)}Z`;
const title = (text: string, page?: string, status?: "ACT" | "MOD"): Line => ({
  left: [
    ...(status ? [{ text: status, color: "cyan" as const, inverse: status === "MOD" }, { text: " " }] : []),
    { text, color: "cyan" },
  ],
  right: page ? { text: page, color: "white", size: "small" } : undefined,
});
const caption = (left?: string, right?: string, center?: string): Line => ({
  left: left ? { text: left, color: "green", size: "small" } : undefined,
  right: right ? { text: right, color: "green", size: "small" } : undefined,
  center: center ? { text: center, color: "green", size: "small" } : undefined,
});
const prompt = (text: string): Segment => ({ text, color: "cyan" });
const boxes = (count: number): Segment => ({ text: "□".repeat(count), color: "amber" });
const dashes = (count: number): Segment => ({ text: "-".repeat(count), color: "white" });

const ICAO = /^[A-Z]{4}$/;
const WAYPOINT = /^[A-Z0-9]{2,5}$/;

const demoRoute = (): Route => ({
  origin: "CYOW", dest: "CYUL", coRoute: "OWUL1", flightNo: "LIFE21",
  legs: [
    { ident: "MUN", course: 164, distance: 10.3, altitude: "3000" },
    { ident: "RDG", course: 28, distance: 58.8, altitude: "4500" },
    { ident: "TOLGU", course: 71, distance: 22.4, altitude: "4500" },
    { ident: "FERDI", course: 88, distance: 18.9, altitude: "3000" },
    { ident: "RW24R", course: 236, distance: 9.6, altitude: "1400" },
    { ident: "CYUL", course: 236, distance: 2.1 },
  ],
});

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
  private marks: { ident: string; position: string }[] = [];
  private level = 6;
  private readonly maxLevel = 10;
  private brighten = true;
  private lastBrt = -Infinity;
  private squawkIdentUntil = 0;
  readonly position = "N4518.6W07540.9";

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
    const lamps = new Set<Lamp>();
    if (this.unacknowledged) lamps.add("MSG");
    if (this.modified) lamps.add("EXEC");
    return lamps;
  }

  screen(): CduScreen {
    const page = PAGES[this.page];
    const count = Math.max(1, page.pages(this));
    this.index = Math.min(this.index, count - 1);
    const lines = page.render(this, this.index);
    lines[13] = this.scratchLine();
    return compose(lines);
  }

  press(fn: CduFunction, options: { held?: boolean } = {}) {
    this.handle(fn, options);
    this.emit();
  }

  /** Adds a system alert message, as the real FMS does for an alert condition. Used by the test bench. */
  raiseAlert(text: string) {
    const message = { text: text.toUpperCase().slice(0, COLUMNS), alert: true };
    this.recall.unshift(message);
    this.message = message;
    this.unacknowledged = true;
    this.emit();
  }

  // ------------------------------------------------------------------ state read by pages

  get route(): Route { return this.modified ?? this.active; }
  get routeStatus(): "ACT" | "MOD" { return this.modified ? "MOD" : "ACT"; }
  get now() { return this.clock(); }
  get radioState() { return this.radios; }
  get fuelState() { return this.fuel; }
  get markList() { return this.marks; }
  get recallList() { return this.recall; }
  get squawkIdent() { return this.clock().getTime() < this.squawkIdentUntil; }

  // ------------------------------------------------------------------ state changed by pages

  open(page: PageId, index = 0) { this.page = page; this.index = index; }

  /** Starts (or continues) a modification of the active route. */
  modify(change: (route: Route) => void) {
    const route = this.modified ?? structuredClone(this.active);
    change(route);
    this.modified = route;
  }

  /** The ERASE prompt: discards the modification; navigation never left the active route. */
  eraseModification() { this.modified = null; }

  setScratch(text: string) { this.scratch = text.slice(0, COLUMNS); }
  setRadio(key: keyof ScriptedFms["radios"], value: string) { this.radios[key] = value; }
  setFuel(key: keyof ScriptedFms["fuel"], value: number) { this.fuel[key] = value; }

  addMark() {
    const ident = `MRK${String(this.marks.length + 1).padStart(2, "0")}`;
    this.marks.push({ ident, position: this.position });
    return ident;
  }

  squawk() { this.squawkIdentUntil = this.clock().getTime() + 18_000; }

  // ------------------------------------------------------------------ keys

  private emit() {
    this.changes += 1;
    for (const listener of this.listeners) listener();
  }

  private advisory(text: string) { this.message = { text, alert: false }; }

  private scratchLine(): Line {
    if (this.message) return { left: { text: this.message.text, color: this.message.alert ? "amber" : "white" } };
    return { left: { text: this.scratch, color: "white" } };
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
      case "EXEC":
        if (this.modified) { this.active = this.modified; this.modified = null; }
        return;
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
      case "ANS": return this.open("ANS");
      case "MSG": return this.open("MSG_RECALL");
      case "HOLD":
        // Manual item 17: with no hold in the plan, LEGS with "/H" in the scratchpad for the holding fix.
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
    else if (result === "not-allowed") this.advisory(scratch === "DELETE" ? "INVALID DELETE" : "NOT ALLOWED");
  }
}

// ---------------------------------------------------------------------------------------------- pages

const simulatedPage = (name: string, lines: string[]): Page => ({
  pages: () => 1,
  render: () => [
    title(name),
    undefined,
    ...lines.map(text => ({ left: { text, color: "white" as const, size: "medium" as const } })),
    undefined,
    { center: { text: "SIMULATED PAGE", color: "amber", size: "small" } },
  ],
});

const PAGES: Record<PageId, Page> = {
  MENU: {
    pages: () => 1,
    render: () => [
      title("MCDU MENU", "1/1"),
      undefined,
      { left: prompt("<FMS"), right: { text: "ACT", color: "green", size: "small" } },
      undefined,
      { left: { text: "<ACARS", color: "white", size: "medium" } },
      undefined,
      { left: { text: "<SATCOM", color: "white", size: "medium" } },
      undefined, undefined, undefined, undefined, undefined,
      { center: { text: "SUBSYSTEMS SIMULATED", color: "amber", size: "small" } },
    ],
    lsk: (fms, side, row) => { if (side === "L" && row === 1) fms.open("INIT_REF"); },
  },

  INIT_REF: {
    pages: () => 1,
    render: () => [
      title("INIT/REF INDEX", "1/1"),
      undefined,
      { left: prompt("<IDENT"), right: prompt("NAV DATA>") },
      undefined,
      { left: prompt("<POS"), right: prompt("PREDEF WPT>") },
      undefined,
      { left: prompt("<FUEL"), right: prompt("MSG RECALL>") },
      undefined,
      { left: prompt("<RTE"), right: prompt("RADIO>") },
    ],
    lsk: (fms, side, row) => {
      const target: Record<string, PageId> = { L1: "IDENT", L2: "POS", L3: "FUEL", L4: "RTE", R1: "IDENT", R2: "PREDEF", R3: "MSG_RECALL", R4: "RADIO" };
      const page = target[`${side}${row}`];
      if (page) fms.open(page);
    },
  },

  IDENT: {
    pages: () => 1,
    render: () => [
      title("IDENT", "1/1"),
      caption(" MODEL", "OP PROGRAM "),
      { left: { text: "CMA-9000", color: "white", size: "medium" }, right: { text: "AEROLINK SIM", color: "white", size: "medium" } },
      caption(" NAV DATA", "ACTIVE "),
      { left: { text: "NA-2610", color: "white", size: "medium" }, right: { text: "01OCT-28OCT", color: "white", size: "medium" } },
      undefined,
      { center: { text: "SIMULATION - NOT FOR", color: "amber", size: "small" } },
      { center: { text: "NAVIGATION", color: "amber", size: "small" } },
      undefined, undefined, undefined,
      { left: dashes(24) },
      { left: prompt("<INDEX"), right: prompt("POS INIT>") },
    ],
    lsk: (fms, side, row) => {
      if (row === 6) fms.open(side === "L" ? "INIT_REF" : "POS");
    },
  },

  POS: {
    pages: () => 1,
    render: fms => [
      title("POS INIT", "1/1"),
      caption(" FMS POS"),
      { left: { text: fms.position, color: "white", size: "medium" } },
      caption(" GPS POS"),
      { left: { text: fms.position, color: "white", size: "medium" } },
      caption(" UTC", "SET POS "),
      { left: { text: hhmm(fms.now), color: "white", size: "medium" }, right: boxes(15) },
      undefined, undefined, undefined, undefined,
      { left: dashes(24) },
      { left: prompt("<INDEX"), right: prompt("RTE>") },
    ],
    lsk: (fms, side, row, scratch) => {
      if (row === 6) { fms.open(side === "L" ? "INIT_REF" : "RTE"); return; }
      if (side === "R" && row === 3) return /^[NS]\d{4}\.\d[EW]\d{5}\.\d$/.test(scratch) ? void fms.setScratch("") : "invalid";
      if (side === "L" && (row === 1 || row === 2) && !scratch) fms.setScratch(fms.position);
    },
  },

  MSG_RECALL: {
    pages: fms => Math.max(1, Math.ceil(fms.recallList.length / 5)),
    render: (fms, index) => {
      const count = Math.max(1, Math.ceil(fms.recallList.length / 5));
      const lines: (Line | undefined)[] = [title("MESSAGE RECALL", `${index + 1}/${count}`)];
      const page = fms.recallList.slice(index * 5, index * 5 + 5);
      if (!page.length) lines[2] = { center: { text: "NO MESSAGES", color: "white", size: "medium" } };
      page.forEach((message, i) => { lines[2 + i * 2] = { left: { text: message.text, color: message.alert ? "amber" : "white" } }; });
      return lines;
    },
  },

  RTE: {
    pages: () => 2,
    render: (fms, index) => {
      const route = fms.route;
      if (index === 0)
        return [
          title("RTE 1", "1/2", fms.routeStatus),
          caption(" ORIGIN", "DEST "),
          { left: { text: route.origin }, right: { text: route.dest } },
          caption(" CO ROUTE", "FLT NO "),
          { left: { text: route.coRoute }, right: { text: route.flightNo } },
          caption(" RUNWAY"),
          { left: route.runway ? { text: route.runway } : dashes(5) },
          undefined, undefined, undefined, undefined,
          { left: dashes(24) },
          fms.routeStatus === "MOD" ? { left: prompt("<ERASE"), right: prompt("LEGS>") } : { right: prompt("LEGS>") },
        ];
      const lines: (Line | undefined)[] = [title("RTE 1", "2/2", fms.routeStatus), caption(" VIA", "TO ")];
      route.legs.slice(0, 5).forEach((leg, i) => {
        lines[2 + i * 2] = { left: { text: "DIRECT", color: "white", size: "medium" }, right: { text: leg.ident } };
      });
      lines[12] = { right: prompt("LEGS>") };
      return lines;
    },
    lsk: (fms, side, row, scratch, index) => {
      if (row === 6) {
        if (side === "R") fms.open("LEGS");
        else if (fms.routeStatus === "MOD") fms.eraseModification();
        return;
      }
      if (index !== 0) return;
      const field = side === "L" ? (row === 1 ? "origin" : row === 2 ? "coRoute" : row === 3 ? "runway" : null)
        : row === 1 ? "dest" : row === 2 ? "flightNo" : null;
      if (!field) return;
      if (!scratch) { fms.setScratch(fms.route[field] ?? ""); return; }
      if (scratch === "DELETE") return "not-allowed";
      if ((field === "origin" || field === "dest") && !ICAO.test(scratch)) return "invalid";
      if (field === "runway" && !/^RW\d{2}[LRC]?$/.test(scratch)) return "invalid";
      if (scratch.length > 10) return "invalid";
      fms.modify(route => { route[field] = scratch; });
      fms.setScratch("");
    },
  },

  DEP_ARR: {
    pages: () => 1,
    render: fms => [
      title("DEP/ARR INDEX", "1/1"),
      undefined,
      { left: prompt("<DEP"), center: { text: fms.route.origin, color: "white" }, right: { text: "ARR>", color: "white", size: "small" } },
      undefined,
      { center: { text: fms.route.dest, color: "white" }, right: prompt("ARR>") },
    ],
    lsk: (fms, side, row) => {
      if (side === "L" && row === 1) fms.open("DEPARTURES");
      if (side === "R" && (row === 1 || row === 2)) fms.open("ARRIVALS");
    },
  },

  DEPARTURES: {
    pages: () => 1,
    render: fms => [
      title(`${fms.route.origin} DEPARTURES`, "1/1"),
      caption(" SIDS", "RUNWAYS "),
      ...["07", "14", "25", "32"].flatMap(runway => [
        { right: { text: `${fms.route.runway === `RW${runway}` ? "<SEL> " : ""}RW${runway}`, color: fms.route.runway === `RW${runway}` ? "green" as const : "white" as const } },
        undefined,
      ]),
      undefined, undefined, undefined,
      { left: prompt("<INDEX") },
    ],
    lsk: (fms, side, row) => {
      if (side === "L" && row === 6) { fms.open("DEP_ARR"); return; }
      const runway = ["07", "14", "25", "32"][row - 1];
      if (side === "R" && runway) fms.modify(route => { route.runway = `RW${runway}`; });
    },
  },

  ARRIVALS: simulatedPage("ARRIVALS", ["STARS AND APPROACHES", "ARE NOT MODELLED IN", "THIS SIMULATION"]),

  LEGS: {
    pages: fms => Math.max(1, Math.ceil(fms.route.legs.length / 5)),
    render: (fms, index) => {
      const route = fms.route;
      const count = Math.max(1, Math.ceil(route.legs.length / 5));
      const lines: (Line | undefined)[] = [title("RTE 1 LEGS", `${index + 1}/${count}`, fms.routeStatus)];
      route.legs.slice(index * 5, index * 5 + 5).forEach((leg, i) => {
        const active = index === 0 && i === 0;
        lines[1 + i * 2] = { left: { text: ` ${String(leg.course).padStart(3, "0")}°`, color: "white", size: "small" }, center: { text: `${fixed(leg.distance, 1)}NM`, color: "white", size: "small" } };
        lines[2 + i * 2] = {
          left: { text: pad(leg.ident, 5), color: active ? "magenta" : "green", inverse: active },
          right: { text: leg.altitude ?? "-----", color: "white", size: "medium" },
        };
      });
      lines[11] = { left: dashes(24) };
      lines[12] = fms.routeStatus === "MOD" ? { left: prompt("<ERASE"), right: prompt("RTE DATA>") } : { right: prompt("RTE DATA>") };
      return lines;
    },
    lsk: (fms, side, row, scratch, index) => {
      if (row === 6) {
        if (side === "L" && fms.routeStatus === "MOD") fms.eraseModification();
        if (side === "R") fms.open("RTE", 1);
        return;
      }
      if (side !== "L") return;
      const at = index * 5 + row - 1;
      const leg = fms.route.legs[at];
      if (!scratch) { if (leg) fms.setScratch(leg.ident); return; }
      if (scratch === "DELETE") {
        if (!leg || at === fms.route.legs.length - 1) return "not-allowed";
        fms.modify(route => { route.legs.splice(at, 1); });
        fms.setScratch("");
        return;
      }
      if (scratch === "/H" || scratch.endsWith("/H")) {
        const fix = scratch === "/H" ? leg?.ident : scratch.slice(0, -2);
        if (!fix || !WAYPOINT.test(fix)) return "invalid";
        fms.modify(route => { route.hold = fix; });
        fms.setScratch("");
        fms.open("HOLD");
        return;
      }
      if (!WAYPOINT.test(scratch)) return "invalid";
      fms.modify(route => { route.legs.splice(at, 0, { ident: scratch, course: leg?.course ?? 0, distance: 0 }); });
      fms.setScratch("");
    },
  },

  PROG: {
    pages: () => 4,
    render: (fms, index) => {
      const [to, next] = fms.route.legs;
      const now = fms.now.getTime();
      const eta = (miles: number) => hhmm(new Date(now + (miles / 120) * 3_600_000));
      if (index === 0)
        return [
          title("PROGRESS", "1/4", "ACT"),
          { left: { text: ` ${String(to?.course ?? 0).padStart(3, "0")}°`, color: "white", size: "small" }, center: { text: "DTG", color: "green", size: "small" }, right: { text: "ETA ", color: "green", size: "small" } },
          { left: { text: pad(to?.ident ?? "-----", 5), color: "magenta", inverse: true }, right: { text: `${fixed(to?.distance ?? 0, 1)}NM ${eta(to?.distance ?? 0)}`, color: "white", size: "medium" } },
          { left: { text: ` ${String(next?.course ?? 0).padStart(3, "0")}°`, color: "white", size: "small" } },
          { left: { text: pad(next?.ident ?? "-----", 5), color: "green" }, right: { text: `${fixed((to?.distance ?? 0) + (next?.distance ?? 0), 1)}NM ${eta((to?.distance ?? 0) + (next?.distance ?? 0))}`, color: "white", size: "medium" } },
          caption("TRUE WIND", "TK/GS "),
          { left: { text: " 270°/ 12KT", color: "white", size: "medium" }, right: { text: "164°/120KT", color: "white", size: "medium" } },
          caption(undefined, "TKE/XTK "),
          { right: { text: "L002°/R0.02NM", color: "white", size: "medium" } },
          caption("RNP/ANP"),
          { left: { text: "1.00/0.05NM", color: "white", size: "medium" } },
          caption("NAV MODE"),
          { left: { text: "<GPS", color: "cyan" } },
        ];
      if (index === 1)
        return [
          title("PROGRESS", "2/4", "ACT"),
          caption(" FUEL QTY", "FUEL FLOW "),
          { left: { text: `${fms.fuelState.quantity}KG`, color: "white", size: "medium" }, right: { text: `${fms.fuelState.flow}KG/H`, color: "white", size: "medium" } },
          caption(" DEST", "EFOB "),
          { left: { text: fms.route.dest, color: "green" }, right: { text: `${Math.max(0, fms.fuelState.quantity - 260)}KG`, color: "white", size: "medium" } },
        ];
      if (index === 2)
        return [
          title("PROGRESS", "3/4", "ACT"),
          caption(" GPS", "HIL "),
          { left: { text: "NAV 9 SAT", color: "white", size: "medium" }, right: { text: "0.03NM", color: "white", size: "medium" } },
          caption(" SBAS", "INTEGRITY "),
          { left: { text: "WAAS", color: "white", size: "medium" }, right: { text: "OK", color: "green", size: "medium" } },
        ];
      return [
        title("PROGRESS", "4/4", "ACT"),
        caption(" OFFSET"),
        { left: dashes(5) },
        caption(" ALT", "VS "),
        { left: { text: "3000FT", color: "white", size: "medium" }, right: { text: "+0FPM", color: "white", size: "medium" } },
      ];
    },
  },

  RADIO: {
    pages: () => 2,
    render: (fms, index) => {
      const r = fms.radioState;
      if (index === 0)
        return [
          title("RADIO", "1/2"),
          caption(" COM1", "STBY "),
          { left: { text: r.com1, color: "green" }, right: { text: r.com1Stby } },
          caption(" COM2", "STBY "),
          { left: { text: r.com2, color: "green" }, right: { text: r.com2Stby } },
          caption(" NAV1", "NAV2 "),
          { left: { text: r.nav1 }, right: { text: r.nav2 } },
        ];
      return [
        title("RADIO", "2/2"),
        caption(" ADF"),
        { left: { text: r.adf } },
        caption(" TPDR", "MODE "),
        { left: { text: r.tpdr, color: fms.squawkIdent ? "green" : "white", inverse: fms.squawkIdent }, right: { text: "ALT", color: "green" } },
      ];
    },
    lsk: (fms, side, row, scratch, index) => {
      const fields: Record<string, [keyof ScriptedFms["radioState"], RegExp, (v: string) => boolean]> = {
        "0L1": ["com1", /^1[1-3]\d\.\d{2,3}$/, v => Number(v) >= 118 && Number(v) < 137],
        "0R1": ["com1Stby", /^1[1-3]\d\.\d{2,3}$/, v => Number(v) >= 118 && Number(v) < 137],
        "0L2": ["com2", /^1[1-3]\d\.\d{2,3}$/, v => Number(v) >= 118 && Number(v) < 137],
        "0R2": ["com2Stby", /^1[1-3]\d\.\d{2,3}$/, v => Number(v) >= 118 && Number(v) < 137],
        "0L3": ["nav1", /^1[01]\d\.\d{1,2}$/, v => Number(v) >= 108 && Number(v) < 118],
        "0R3": ["nav2", /^1[01]\d\.\d{1,2}$/, v => Number(v) >= 108 && Number(v) < 118],
        "1L1": ["adf", /^\d{3,4}(\.\d)?$/, v => Number(v) >= 190 && Number(v) <= 1750],
        "1L2": ["tpdr", /^[0-7]{4}$/, () => true],
      };
      const field = fields[`${index}${side}${row}`];
      if (!field) return;
      const [key, shape, range] = field;
      if (!scratch) {
        // Pressing the standby field with an empty scratchpad swaps active and standby, as a transfer key would.
        if (key === "com1Stby" || key === "com2Stby") {
          const activeKey = key === "com1Stby" ? "com1" : "com2";
          const standby = fms.radioState[key];
          fms.setRadio(key, fms.radioState[activeKey]);
          fms.setRadio(activeKey, standby);
        } else fms.setScratch(fms.radioState[key]);
        return;
      }
      if (!shape.test(scratch) || !range(scratch)) return "invalid";
      const value = key.startsWith("com") ? Number(scratch).toFixed(3) : key.startsWith("nav") ? Number(scratch).toFixed(2) : scratch;
      fms.setRadio(key, value);
      fms.setScratch("");
    },
  },

  FUEL: {
    pages: () => 1,
    render: fms => [
      title("FUEL", "1/1"),
      caption(" FUEL QTY", "FLOW "),
      { left: { text: `${fms.fuelState.quantity}KG` }, right: { text: `${fms.fuelState.flow}KG/H`, color: "white", size: "medium" } },
      caption(" RESERVE", "ENDURANCE "),
      { left: { text: `${fms.fuelState.reserve}KG` }, right: { text: `${fixed((fms.fuelState.quantity - fms.fuelState.reserve) / fms.fuelState.flow, 1)}H`, color: "white", size: "medium" } },
    ],
    lsk: (fms, side, row, scratch) => {
      const key = side === "L" ? (row === 1 ? "quantity" : row === 2 ? "reserve" : null) : row === 1 ? "flow" : null;
      if (!key) return;
      if (!scratch) { fms.setScratch(String(fms.fuelState[key])); return; }
      if (!/^\d{1,5}$/.test(scratch) || Number(scratch) <= 0) return "invalid";
      fms.setFuel(key, Number(scratch));
      fms.setScratch("");
    },
  },

  HOLD: {
    pages: () => 1,
    render: fms => [
      title("HOLD", "1/1", fms.routeStatus),
      caption(" FIX", "QUAD/RADIAL "),
      { left: { text: fms.route.hold ?? "-----", color: "green" }, right: { text: "--/---°", color: "white", size: "medium" } },
      caption(" INBD CRS/DIR", "LEG TIME "),
      { left: { text: "164°/R TURN", color: "white", size: "medium" }, right: { text: "1.0MIN", color: "white", size: "medium" } },
      undefined, undefined, undefined, undefined, undefined, undefined,
      { left: dashes(24) },
      fms.routeStatus === "MOD" ? { left: prompt("<ERASE"), right: prompt("LEGS>") } : { right: prompt("LEGS>") },
    ],
    lsk: (fms, side, row) => {
      if (row !== 6) return;
      if (side === "R") fms.open("LEGS");
      else if (fms.routeStatus === "MOD") fms.eraseModification();
    },
  },

  FIX: {
    pages: () => 1,
    render: fms => [
      title("FIX INFO", "1/1"),
      caption(" REF", "RAD/DIS "),
      { left: boxes(5), right: { text: "---°/---NM", color: "white", size: "medium" } },
      caption(" PRESENT POS"),
      { left: { text: fms.position, color: "white", size: "medium" } },
    ],
    lsk: (fms, side, row, scratch) => {
      if (side === "L" && row === 1 && scratch) return WAYPOINT.test(scratch) ? void fms.setScratch("") : "invalid";
    },
  },

  PREDEF: {
    pages: () => 2,
    render: (fms, index) => {
      const lines: (Line | undefined)[] = [title("PREDEF WPT", `${index + 1}/2`)];
      if (index === 0) {
        lines[2] = { left: { text: "NO PREDEFINED", color: "white", size: "medium" } };
        lines[3] = { left: { text: "WAYPOINTS", color: "white", size: "medium" } };
        return lines;
      }
      lines[1] = caption(" MARK ON TOP");
      fms.markList.slice(-5).forEach((mark, i) => {
        lines[2 + i * 2] = { left: { text: mark.ident, color: "green" }, right: { text: mark.position, color: "white", size: "medium" } };
      });
      if (!fms.markList.length) lines[2] = { left: { text: "NONE", color: "white", size: "medium" } };
      return lines;
    },
    lsk: (fms, side, row, scratch, index) => {
      const mark = index === 1 ? fms.markList.slice(-5)[row - 1] : undefined;
      if (side === "L" && mark && !scratch) fms.setScratch(mark.ident);
    },
  },

  VNAV: simulatedPage("VNAV", ["VERTICAL PATH AND", "CONSTRAINTS ARE NOT", "MODELLED YET"]),
  TACT: simulatedPage("TACTICAL", ["SAR PATTERNS, TACTICAL", "APPROACHES AND MOVING", "WAYPOINTS: NOT MODELLED"]),
  ATC: simulatedPage("ATC/CPDLC", ["NO DATALINK CONNECTION", "IN SIMULATION"]),
  FMC_COMM: simulatedPage("FMC COMM", ["NO DATALINK CONNECTION", "IN SIMULATION"]),
  ANS: simulatedPage("GSM/SMS", ["NO INCOMING CALL", "NO NEW MESSAGE"]),
};
