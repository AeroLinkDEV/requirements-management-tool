import type { Route } from "./fmsModel";
import type { RadioKey, RadioState, RadioRequest } from "./radioManagement";

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
  swap(key: "com1" | "com2"): void;
}
