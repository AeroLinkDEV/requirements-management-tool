import { FlightSimulator } from "./flight";
import { distanceNm } from "./fmsModel";
import type { CrossTalkPort, DualMode, FmsSide } from "./crossTalk";
import { RadioManagementSystem } from "./radioManagement";
import { ScriptedFms } from "./scriptedFms";
import { ACTIVE_PROFILE, type AircraftProfile } from "./profile";
import type { GpsReceiver } from "./gps";
import type { UserDatabaseStore, UserScope } from "./userDatabase";

/** Two computers, one aircraft and receiver environment. This is a bench link, not an installed bus adapter. */
export class DualFmsSystem {
  readonly computers: readonly [ScriptedFms, ScriptedFms];
  readonly flights: readonly [FlightSimulator, FlightSimulator];
  readonly rms: RadioManagementSystem;
  private operation: DualMode = "SYNC";
  private link = true;
  private pending: { side: FmsSide; mode: DualMode } | null = null;
  private editor: FmsSide | null = null;
  private driver: FmsSide = 1;
  private navSide: FmsSide | null = null;
  private phaseDifferentSince: number | null = null;
  private disagreement = false;
  private settings: string[];
  private readonly clock: () => Date;
  constructor(clock: () => Date, options: { profile?: AircraftProfile; secondaryProfile?: AircraftProfile; userDatabase?: { store: UserDatabaseStore; scope: UserScope } } = {}) {
    this.clock = clock;
    const profile = options.profile ?? ACTIVE_PROFILE;
    const one = new ScriptedFms(clock, options);
    const two = new ScriptedFms(clock, { profile: options.secondaryProfile ?? profile, preferredGps: 1, sensors: { read: () => one.navigationInputs },
      receivers: one.gps as readonly [GpsReceiver, GpsReceiver], ...(options.userDatabase ? { userDatabase: {
        store: options.userDatabase.store, scope: { ...options.userDatabase.scope, profileId: `${options.userDatabase.scope.profileId}:fms2` },
      } } : {}) });
    this.computers = [one, two]; this.flights = [new FlightSimulator(one, undefined, () => this.prepareGuidance(1)), new FlightSimulator(two, undefined, () => this.prepareGuidance(2))];
    const parameters = profile.parameters;
    this.rms = new RadioManagementSystem(() => clock().getTime(), () => this.link, () => this.notify(),
      parameters.rmsFeedbackDelay.value, parameters.rmsFeedbackTimeout.value);
    this.settings = this.computers.map(unit => JSON.stringify(unit.computerSettings));
    this.computers.forEach((unit, index) => unit.attachComputerPorts(this.port((index + 1) as FmsSide), this.rms.port((index + 1) as FmsSide)));
    // A radio fault a scenario step injects on either computer lands on the shared radios (F14).
    for (const unit of this.computers) {
      unit.radioFaultSink = (device, faults) => this.rms.setFaults(device, faults);
      // A station off the air is off for both computers' receivers.
      unit.stationOffAirSink = (ident, off) => this.computers.forEach(computer => computer.applyStationOffAir(ident, off));
    }
    two.setReceiverCommandAuthority(false);
    // A mismatched persisted second computer starts independent; synchronization must be a conscious crew decision.
    if (this.compatibility(1, false)) this.operation = "INDEPENDENT";
    this.reconcile();
  }
  get mode() { return this.operation; }
  get linked() { return this.link; }
  get guidanceSide() { return this.driver; }
  get navigationSide() { return this.navSide; }
  get simulator() { return this.flights[this.driver - 1]; }
  private notify() { this.computers.forEach(unit => unit.notifyComputerState()); }
  private unit(side: FmsSide) { return this.computers[side - 1]; }
  private peer(side: FmsSide) { return this.computers[2 - side]; }
  private port(side: FmsSide): CrossTalkPort {
    // The port's property getters have their own receiver, so retain the owning system explicitly.
    // eslint-disable-next-line @typescript-eslint/no-this-alias
    const system = this;
    return {
      side,
      get mode() { return system.operation; }, get linked() { return system.link; },
      get pendingMode() { return system.pending?.side === side ? system.pending.mode : null; },
      get peerRoute() { return structuredClone(system.peer(side).activeRoute); },
      get navigationSide() { return system.navSide; },
      requestMode(mode) {
        if (system.pending && system.pending.side !== side) { system.unit(side).advisory("!CDU ENTRY CONFLICT"); system.notify(); return; }
        system.pending = { side, mode }; system.notify();
      },
      confirmMode(confirm) { system.confirm(side, confirm); },
      beginEdit() {
        if (system.operation === "INDEPENDENT" || !system.link) return true;
        if (system.editor !== null && system.editor !== side) return false;
        system.editor = side; return true;
      },
      finishEdit(executed) {
        if (executed && system.operation === "SYNC" && system.link) {
          system.peer(side).receiveComputerPlan(system.unit(side).computerPlan, false);
          system.flights[2 - side].receiveSynchronizedProgress(system.flights[side - 1]);
        }
        if (system.editor === side) system.editor = null;
        system.notify();
      },
      crossfill(secondary) { return system.crossfill(side, secondary); },
      settingsChanged() { system.settingsChanged(side); },
      healthChanged() { system.reconcile(); system.notify(); },
      broadcastAlert(text) {
        if (system.operation === "SYNC" && system.link && !system.peer(side).hasCondition("fmsFail")) system.peer(side).receiveComputerAlert(text);
      },
      acknowledgeMessage(text) {
        if (system.operation === "SYNC" && system.link) system.peer(side).acknowledgeComputerMessage(text);
      },
      missedApproachRequested() {
        if (system.operation === "SYNC" && system.link) system.peer(side).requestMissedApproach(true);
      },
      setIndependent(on) {
        // Bench injection is a link failure; clearing the fault restores communications, never silently overwrites a route.
        system.setLinkAvailable(!on);
      },
    };
  }
  private compatibility(side: FmsSide, operational = true): string | null {
    const own = this.unit(side), peer = this.peer(side);
    if (!this.link || own.hasCondition("fmsFail") || peer.hasCondition("fmsFail")) return "COMMUNICATION LOST";
    const program = (unit: ScriptedFms) => `${unit.aircraftProfile.operationalProgram}:${unit.aircraftProfile.approachPolicy}`;
    if (program(own) !== program(peer)) return "OP PROGRAM DIFFER";
    if (JSON.stringify(own.navdb.exportData()) !== JSON.stringify(peer.navdb.exportData())) return "NAV DATA DIFFER";
    if (JSON.stringify(own.computerSettings.userDb) !== JSON.stringify(peer.computerSettings.userDb)) return "USER DATA DIFFER";
    if (operational && (own.activeRoute.hold?.status === "IN PROGRESS" || own.activeRoute.hold?.status === "EXIT ARMED")) return "ACTIVE HOLD";
    if (operational && own.approachArmed && own.approachType !== null && own.approachType !== "ILS") return "GPS APPROACH";
    if (operational && this.computers.some(unit => unit.missedApproachActive)) return "MISSED APPROACH";
    if (operational && this.computers.some(unit => unit.routeStatus === "MOD")) return "MODIFICATION IN PROGRESS";
    return null;
  }
  private refuse(side: FmsSide, reason: string) {
    this.unit(side).raiseAlert("UNABLE FMS-FMS SYNC"); this.unit(side).recordFault(`FMS SYNC: ${reason}`);
  }
  private confirm(side: FmsSide, confirm: boolean) {
    const request = this.pending;
    if (!request || request.side !== side) return;
    this.pending = null;
    if (!confirm) { this.notify(); return; }
    if (request.mode === "INDEPENDENT") this.independent("CREW SELECTED");
    else {
      const refused = this.compatibility(side);
      if (refused) { this.refuse(side, refused); this.notify(); return; }
      const own = this.unit(side), peer = this.peer(side);
      peer.receiveComputerSettings(own.computerSettings); peer.receiveComputerPlan(own.computerPlan, false);
      this.flights[2 - side].receiveSynchronizedProgress(this.flights[side - 1]);
      this.settings = this.computers.map(unit => JSON.stringify(unit.computerSettings));
      this.operation = "SYNC"; this.editor = null; this.phaseDifferentSince = null; this.navSide = side;
      this.reconcile(); this.notify();
    }
  }
  private independent(reason: string) {
    const changed = this.operation !== "INDEPENDENT";
    this.operation = "INDEPENDENT";
    if (changed) for (const unit of this.computers) {
      unit.raiseAlert("FMS INDEPENDENT OP"); unit.recordFault(`X-SIDE SYNC LOST: ${reason}`);
    }
    this.editor = null; this.navSide = null; this.pending = null; this.phaseDifferentSince = null; this.notify();
  }
  setLinkAvailable(available: boolean) {
    if (this.link === available) return;
    this.link = available;
    if (!available) this.independent("LINK LOST");
    this.notify();
  }
  selectGuidance(side: FmsSide) {
    if (this.driver === side) return;
    const previous = this.unit(this.driver), next = this.unit(side);
    next.observeAircraft(previous);
    this.flights[side - 1].adoptAircraftMotion(this.flights[this.driver - 1]);
    this.computers.forEach((unit, i) => unit.setReceiverCommandAuthority(i === side - 1));
    this.driver = side; this.notify();
  }
  crossfill(side: FmsSide, secondary = false): boolean {
    const refused = this.compatibility(side, false);
    if (refused) { this.refuse(side, refused); return false; }
    if (this.operation !== "INDEPENDENT") { this.unit(side).advisory("XFILL REQUIRES INDEPENDENT"); return false; }
    // Sending side's executed active/inactive route becomes the receiving MOD. Receiving EXEC is the only activation.
    return this.peer(side).receiveComputerPlan(this.unit(side).computerPlan, true, secondary);
  }
  private settingsChanged(side: FmsSide) {
    const own = this.unit(side), next = JSON.stringify(own.computerSettings);
    if (next === this.settings[side - 1]) return;
    this.settings[side - 1] = next;
    if (this.operation === "SYNC" && this.link && !own.hasCondition("fmsFail") && !this.peer(side).hasCondition("fmsFail")) {
      this.peer(side).receiveComputerSettings(own.computerSettings);
      this.settings[2 - side] = JSON.stringify(this.peer(side).computerSettings);
    }
  }
  private reconcile() {
    const [one, two] = this.computers, parameters = one.aircraftProfile.parameters;
    this.rms.tick();
    if (this.operation === "SYNC") {
      if (!this.link || this.computers.some(unit => unit.hasCondition("fmsFail"))) this.independent("COMPUTER UNAVAILABLE");
      else {
        const different = one.localFlightPhase !== two.localFlightPhase;
        this.phaseDifferentSince = different ? this.phaseDifferentSince ?? this.clock().getTime() : null;
        if (this.phaseDifferentSince !== null && (this.clock().getTime() - this.phaseDifferentSince) / 1000 > parameters.dualPhaseDisagreementTime.value)
          this.independent("PHASE DISAGREEMENT");
      }
    }
    if (this.operation === "SYNC") {
      const solutions = this.computers.map(unit => unit.localNavigationSolution);
      const usable = solutions.map((solution, index) => ({ solution, side: (index + 1) as FmsSide }))
        .filter(({ solution, side }) => !this.unit(side).hasCondition("fmsFail") && solution.mode !== "DR" && !solution.uncertain
          && this.computers.every(unit => solution.mode !== "GPS" || unit.gpsNavSelected));
      let selected = usable.find(candidate => candidate.side === this.navSide) ?? usable[0];
      const rank = { GPS: 0, "DME/DME": 1, "VOR/DME": 2, KALMAN: 3, DVS: 4, DR: 5 };
      for (const candidate of usable) if (selected && (rank[candidate.solution.mode] < rank[selected.solution.mode]
        || candidate.solution.mode === selected.solution.mode && candidate.solution.anp !== null
          && (selected.solution.anp === null || (selected.solution.anp - candidate.solution.anp) * 1852 >= parameters.dualSensorHysteresis.value))) selected = candidate;
      if (selected) {
        this.navSide = selected.side;
        this.computers.forEach(unit => unit.receiveSystemNavigation(selected.solution, this.unit(selected.side).navigationWindEstimate));
      }
    } else {
      const [a, b] = this.computers.map(unit => unit.localNavigationSolution);
      const disagree = !one.hasCondition("fmsFail") && !two.hasCondition("fmsFail") && a.mode === "GPS" && b.mode === "GPS"
        && distanceNm(a.position, b.position) > parameters.dualPositionDisagreement.value;
      if (disagree && !this.disagreement) this.computers.forEach(unit => unit.raiseAlert("GPS-GPS POS DISAGREE"));
      this.disagreement = disagree;
    }
  }
  private prepareGuidance(side: FmsSide) {
    // Each guidance computation consumes the selected system measurement, not a transient local estimate.
    this.peer(side).refreshSensorInput(); this.reconcile();
  }
  /** Only the selected flight controller integrates physics. The other computes guidance/sequence against that aircraft. */
  step(dt: number) {
    let remaining = dt;
    while (remaining > 1e-6) {
      const h = Math.min(1, remaining), selected = this.unit(this.driver), other = this.peer(this.driver);
      if (this.driver === 2) { this.unit(1).observeAircraft(selected); this.unit(1).refreshSensorInput(); }
      this.simulator.step(h);
      other.observeAircraft(selected); this.flights[2 - this.driver].observe(h, this.simulator);
      this.reconcile(); this.flights.forEach((flight, index) => flight.refreshGuidance(index === this.driver - 1 ? undefined : this.simulator)); remaining -= h;
    }
    this.notify();
  }
  tick() {
    const selected = this.unit(this.driver), other = this.peer(this.driver);
    if (this.driver === 2) this.unit(1).observeAircraft(selected);
    this.unit(1).refreshSensorInput(); this.unit(1).tick();
    this.unit(2).observeAircraft(selected); this.unit(2).refreshSensorInput(); this.unit(2).tick();
    other.observeAircraft(selected); this.settingsChanged(1); this.settingsChanged(2); this.reconcile();
    this.flights.forEach((flight, index) => flight.refreshGuidance(index === this.driver - 1 ? undefined : this.simulator)); this.notify();
  }
}
