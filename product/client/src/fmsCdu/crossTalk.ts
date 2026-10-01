import type { Route } from "./fmsModel";
import type { Navaid } from "./navData";
import type { AdfSettings, DmeDevice, NavMode, RadioDevice, RadioEvent, RadioFaults, RadioKey, RadioState, RadioRequest, RadioTestState, TestableDevice } from "./radioManagement";

export type FmsSide = 1 | 2;
export type DualMode = "SYNC" | "INDEPENDENT";
/** Modelled computer-to-computer link. Device radio feedback uses a separate RMS port. */
export interface CrossTalkPort {
  readonly side: FmsSide;
  readonly mode: DualMode;
  readonly linked: boolean;
  readonly pendingMode: DualMode | null;
  readonly peerRoute: Route;
  readonly navigationSide: FmsSide | null;
  requestMode(mode: DualMode): void;
  confirmMode(confirm: boolean): void;
  beginEdit(): boolean;
  finishEdit(executed: boolean): void;
  crossfill(secondary: boolean): boolean;
  settingsChanged(): void;
  healthChanged(): void;
  broadcastAlert(text: string): void;
  acknowledgeMessage(text: string): void;
  missedApproachRequested(): void;
  setIndependent(on: boolean): void;
}

export interface RadioManagementPort {
  readonly state: RadioState;
  readonly requests: readonly RadioRequest[];
  tune(key: RadioKey, value: string): void;
  swap(key: "com1" | "com2" | "adf" | "adf2"): void;
  /** Plan F8a: what a radio reports it is on (null while failed or silent), and whether a DME transceiver answers. */
  receiving(device: RadioDevice): string | null;
  /** F16 world stimulus: one physical NDB's availability, shared by both computers, separate from radio health. */
  ndbTransmitting(station: Pick<Navaid, "ident" | "frequency" | "position">): boolean;
  setNdbOffAir(station: Pick<Navaid, "ident" | "frequency" | "position">, off: boolean): void;
  dmeReceiving(device: DmeDevice): boolean;
  /** NAV AUTO/MAN (M300 13-21): the FMS's own tuning (AUTO) never switches a NAV to MAN; a crew entry does. */
  navMode(device: "nav1" | "nav2"): NavMode;
  setNavMode(device: "nav1" | "nav2", mode: NavMode): void;
  autoTune(device: "nav1" | "nav2", value: string): void;
  /** This side's CONTROL LOST alerts and FAILED advisories since the last call. */
  drainEvents(): RadioEvent[];
  /** Plan F8b: the page controls of the NAV and ADF pages (M300 13-21 to 13-25). */
  /** Plan C3: the radio's separate internal states (control path, measurement bus, receiver). */
  faults(device: RadioDevice | DmeDevice): RadioFaults;
  dmeHold(device: DmeDevice): string | null;
  setDmeHold(device: DmeDevice, on: boolean): void;
  adf(device: "adf" | "adf2"): AdfSettings;
  setAdf(device: "adf" | "adf2", settings: Partial<AdfSettings>): void;
  testState(device: TestableDevice): RadioTestState;
  pressTest(device: TestableDevice): void;
}
