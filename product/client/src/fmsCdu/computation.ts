/** What a kernel computation runs in: ScriptedFms.compute (#1518). */
export interface Computing { compute<T>(work: () => T): T }

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
