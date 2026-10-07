/** What a kernel computation runs in: ScriptedFms.compute (#1518). */
export interface Computing { compute<T>(work: () => T): T }

/**
 * What each public method of a simulation unit is (#1517 I1b, the structural guard). Every public method is
 * classified, so a new one cannot be left out: a role table is declared `satisfies MethodRoles<C>`, and a method it does
 * not name fails `npm run typecheck`.
 *
 * - action: a state change the crew, the bench or a scenario makes. asComputations makes it one kernel computation, and
 *   outside code reaches it only through the kernel's submit().
 * - self-computing: a state change that runs its own computation (press, setCondition...), reached the same way.
 * - query: reads state and changes none. Only queries are in the unit's read-only view, which is all the bench's
 *   components are given.
 * - kernel-internal: the step's and the compositions' own machinery (tick, step, ports, settling...), called by the
 *   kernel, the adapter or another unit, never by the bench.
 */
export type MethodRole = "action" | "self-computing" | "query" | "kernel-internal";

/** The public methods of C: its members whose type is a function. */
export type MethodName<C> = { [K in keyof C]-?: C[K] extends (...args: never[]) => unknown ? K : never }[keyof C];

/** A complete role table for C's public methods: one entry for each, and no other. */
export type MethodRoles<C> = { readonly [K in MethodName<C>]: MethodRole };

/** The names a role table gives a role other than query: what a read-only view leaves out. */
type Mutator<R> = { [K in keyof R]: R[K] extends "query" ? never : K }[keyof R];

/**
 * C's read-only view under its role table: its queries and its properties, with every action, self-computing and
 * kernel-internal method left out, so a component given the view cannot change the simulation (it calls submit()).
 */
export type ViewOf<C, R> = Readonly<Omit<C, Mutator<R> & keyof C>>;

/** The names a role table classifies as actions: the methods asComputations wraps. */
export function actionsOf<C>(roles: MethodRoles<C>): (keyof C)[] {
  return (Object.keys(roles) as (keyof C & keyof MethodRoles<C>)[]).filter(name => roles[name] === "action");
}

/**
 * #1518: makes each named method one kernel computation, whoever calls it: a key's page handler, the bench's
 * instructor controls, a scenario action, a start state or a test. A method called inside a computation joins it.
 * Only actions belong here, never a query: a query run as a computation would keep what an observer reads.
 */
export function asComputations<C extends object>(target: { prototype: C }, names: readonly (keyof C)[], owner: (self: C) => Computing) {
  for (const name of names) {
    const action = target.prototype[name] as unknown as (this: C, ...args: unknown[]) => unknown;
    if (typeof action !== "function") throw new Error(`asComputations: ${String(name)} is not a method`);
    Object.defineProperty(target.prototype, name, {
      configurable: true, writable: true,
      value: function (this: C, ...args: unknown[]) { return owner(this).compute(() => action.apply(this, args)); },
    });
  }
}
