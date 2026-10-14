import type { AircraftProfile } from "../profile";
import { profileById, profileFingerprint } from "../profile";
import type { StartStateId } from "../kbtvDemo";
import { ScenarioRunner, type Scenario } from "../scenario";
import { variantById } from "../variants";
import type { UserDatabaseStore, UserScope } from "../userDatabase";
import type { ActionSource, KernelUnits } from "./actions";
import { FmsKernel } from "./kernel";
import { dualComposition, singleComposition } from "./legacyPlantAdapter";
import { SubmissionPort } from "./submission";

export type StartMode = "WARM" | "COLD";
export type SessionStart = { readonly id: StartStateId; readonly outcome: { readonly refused: string } | { readonly ready: true } };
export type SessionSetup = {
  readonly composition: "single" | "dual";
  readonly utc0Ms: number;
  readonly profileId: string;
  readonly secondaryProfileId?: string;
  readonly variantId: string;
  readonly initialFreeze: boolean;
  readonly startModes: readonly StartMode[];
  readonly onGround: boolean;
  readonly startState: StartStateId | null;
  readonly startSource: ActionSource;
  readonly scenario: Scenario | null;
};
export type InitialUserBytes = { readonly scope: UserScope; readonly text: string | null };
export type InitialObservations = {
  readonly profiles: readonly { id: string; version: number; fingerprint: string }[];
  readonly surface: string;
  readonly rtcOffsetsMs: readonly number[];
  readonly seeds: readonly { receiver: number; constellation: number }[];
  readonly navigationCycles: readonly { active: string; inactive: string | null }[];
  /** D5 7.2 magnetic database identity per unit: the model's own name, epoch and CRC, and whether its check passed. */
  readonly magneticModels: readonly { name: string; epoch: number; crc: string; valid: boolean }[];
};
export type SessionFactoryOptions = {
  readonly userDatabase?: { readonly store: UserDatabaseStore; readonly scope: UserScope };
  /** Real fresh replay only. No live session is passed this option. */
  readonly verification?: boolean;
  /** A zero-frame private verification stops before the first ACTION, including the runner's t=0 poll. */
  readonly beforeInitialAction?: boolean;
};

function profile(id: string): AircraftProfile {
  const value = profileById(id);
  if (!value) throw new Error(`unknown recording profile ${id}`);
  return value;
}

/** The bench and its verifier use this same production construction path, never the verifier's current user store. */
export function createSession(setup: SessionSetup, options: SessionFactoryOptions = {}) {
  if (!Number.isSafeInteger(setup.utc0Ms)) throw new Error("invalid recording UTC0");
  const one = profile(setup.profileId), two = profile(setup.secondaryProfileId ?? setup.profileId);
  const count = setup.composition === "dual" ? 2 : 1;
  if (setup.startModes.length !== count || setup.startModes.some(mode => mode !== "WARM" && mode !== "COLD")) throw new Error("invalid recording start modes");
  const users: InitialUserBytes[] = options.userDatabase
    ? Array.from({ length: count }, (_, index) => {
      const scope = { ...options.userDatabase!.scope, profileId: options.userDatabase!.scope.profileId + (index ? ":fms2" : "") };
      return { scope, text: options.userDatabase!.store.read(scope) };
    })
    : [one, two].slice(0, count).map(value => ({ scope: { userId: "local", profileId: value.id }, text: null }));
  const composition = setup.composition === "dual"
    ? dualComposition(setup.utc0Ms, { profile: one, secondaryProfile: two, ...(options.userDatabase ? { userDatabase: options.userDatabase } : {}) })
    : singleComposition(setup.utc0Ms, { profile: one, ...(options.userDatabase ? { userDatabase: options.userDatabase } : {}) });
  const { plant, settleStart } = composition;
  const units: KernelUnits = plant.units;
  // COLD is the actual power-on path at F_0. Constructor WARM and the shared GPS receivers are left unchanged.
  units.computers.forEach((unit, index) => { if (setup.startModes[index] === "COLD") unit.powerOn("COLD", setup.onGround); });
  const initial: InitialObservations = {
    profiles: units.computers.map(unit => ({ id: unit.aircraftProfile.id, version: unit.aircraftProfile.version, fingerprint: profileFingerprint(unit.aircraftProfile) })),
    surface: units.computers[0].surface.id,
    rtcOffsetsMs: units.computers.map(unit => unit.utcTime.getTime() - setup.utc0Ms),
    seeds: units.computers[0].gps.map(receiver => ({ receiver: receiver.seed, constellation: receiver.constellationSeed })),
    navigationCycles: units.computers.map(unit => ({ active: unit.activeCycle.id, inactive: unit.inactiveCycle?.id ?? null })),
    magneticModels: units.computers.map(unit => {
      const { name, epoch, crc } = unit.magvar.database;
      return { name, epoch, crc, valid: unit.magvar.valid };
    }),
  };
  const kernel = new FmsKernel(plant, null, { flightFreeze: setup.initialFreeze, verification: options.verification });
  const port = new SubmissionPort(kernel), fms = units.computers[0], flight = units.flights[0];
  let started = null as SessionStart | null;
  let runner: ScenarioRunner | null = null;
  if (!options.beforeInitialAction) {
    if (setup.startState) {
      const id = setup.startState;
      port.submit({ kind: "ios.place", startState: id }, setup.startSource,
        event => { started = { id, outcome: event.outcome.status === "refused" ? { refused: event.outcome.reason } : { ready: true } }; });
    }
    const chosen = variantById(setup.variantId);
    runner = setup.scenario ? new ScenarioRunner(setup.scenario, fms, { variant: `${chosen.id} (${chosen.label})`, cycle: fms.activeCycle.id }, flight, settleStart) : null;
    if (runner) kernel.run(runner);
  }
  return { setup: structuredClone(setup), initial, users, persistentUserScope: options.userDatabase?.scope ?? null,
    system: "system" in composition ? composition.system : null, fms, flight, plant, units, kernel, port, runner, started };
}
export type RecordingSession = ReturnType<typeof createSession>;
