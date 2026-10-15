import type { Accepted, Refused, RestoreRefusal, RestoreRefusalCode, SnapshotRefusal, SnapshotRefusalCode } from "./refusal";

/**
 * The state codec of the `aerolink.fms.snapshot` envelope, format version 1 (#1516; #1502 D7 9.1 as amended with
 * Codex/Sol's wording of 6 Oct). It turns the authoritative state of a set of units into exact, deterministic bytes and
 * back, and runs the dynamic alias census while it encodes.
 *
 * It is deliberately not the per-frame state digest's encoding: the digest sorts object keys and writes Dates as
 * absolute milliseconds, because it observes values. The snapshot preserves order and identity, because it restores.
 *
 * Bytes (all integers little-endian):
 *
 *     state   = i64 utc0Ms, u32 unitCount, unit*
 *     unit    = str unitId, u32 fieldCount, (str key, value)*        -- authoritative root fields, own-key order
 *     str     = u32 byteLength, UTF-8 bytes                          -- well-formed strings only
 *     value   = 00 undefined | 01 null | 02 false | 03 true
 *             | 04 f64                     -- binary64 bits; -0 and +-Infinity kept; every NaN is 00 00 00 00 00 00 F8 7F
 *             | 05 str
 *             | 06 u32 length, element*    -- element = value | 0F (a hole: absent, not undefined)
 *             | 07 u32 count, (str key, value)*   -- plain object, ECMAScript own-key order
 *             | 08 u32 count, (str key, value)*   -- null-prototype object
 *             | 09 i64 offsetMs               -- a valid Date: getTime() - utc0Ms, exactly
 *             | 0A                            -- an invalid Date
 *             | 0B u32 size, (value key, value value)*   -- Map, entry order
 *             | 0C u32 size, value*           -- Set, entry order
 *             | 0D u32 byteLength, bytes      -- ArrayBuffer, bytes exact
 *             | 0E u8 kind, value buffer, u32 byteOffset, u32 length   -- typed array (length in elements) or DataView
 *             | 10 str unitId, u32 stepCount, step*   -- a reference to the object first met at that path
 *             | 11 str className, u32 count, (str key, value)*   -- an instance of a registered class
 *             | 12 str contentHash, str ident, u32 index   -- an object that belongs to registered content
 *     step    = 00 str key | 01 u32 index | 02 u32 mapKey | 03 u32 mapValue | 04 u32 setEntry | 05 (a view's buffer)
 *
 * Nesting: a value's path from its unit root (the root field is step 1) may have at most 256 steps. Encode refuses
 * deeper state (`NESTING_LIMIT`) and decode refuses deeper bytes (`NON_CANONICAL`), at the same point.
 *
 * The census. Every object is emitted once, at the first path where the encoder meets it (depth-first, units in the
 * given order, fields in own-key order); every later meeting is a reference to that path, so shared objects stay
 * shared and distinct objects stay distinct even when their values are equal. Nothing is ever emitted as a copy of
 * an object met elsewhere. Before encoding, the census walks the whole graph of every config, observer and derived
 * field: restore rebuilds those, so authoritative state that reaches anything in them refuses (`UNCLASSIFIED_ALIAS`).
 * Cross-unit sharing is allowed only under a declared link to an owner that precedes the holder, and a link field
 * must hold a unit root or an object its declared owner emits.
 *
 * Decoding accepts exactly the bytes this encoder can produce for the same class registry, links and content
 * registry, so decode-then-encode reproduces the input bytes.
 */

/**
 * - authoritative: serialized and restored exactly.
 * - link: re-bound by `link()` on restore; never serialized.
 * - config: rebuilt from the recorded configuration identity; never serialized.
 * - observer: listeners and UI counters; never serialized.
 * - derived: caches and indices recomputed silently on restore (D7 9.2); never serialized.
 */
export type FieldClass = "authoritative" | "link" | "config" | "observer" | "derived";

/**
 * A unit's declaration that, at or under `path` (property keys or array indexes from its root), it holds objects
 * emitted by the unit `owner`. The owner must come before the holder in the unit order.
 */
export type CrossOwnerLink = { readonly path: readonly string[]; readonly owner: string };

export type SnapshotUnit = {
  readonly unitId: string;
  /** The unit's live object. Only its own data properties are read; no getter and no method is ever called. */
  readonly root: object;
  /** Every own property of the root, classified. Only authoritative fields are encoded. */
  readonly fields: Readonly<Record<string, FieldClass>>;
  readonly links?: readonly CrossOwnerLink[];
};

/**
 * A class whose instances are encoded as their own data properties and decoded with `Object.create(prototype)`,
 * without running the constructor. Registering a class asserts that its whole state is its own data properties: no
 * `#private` fields, no side tables keyed by the instance (such as a WeakMap), and no state held by constructor
 * closures. The codec cannot see any of those, so each registration must be reviewed against this.
 */
export type ClassRegistration = { readonly name: string; readonly prototype: object };

/** Where an object came from in registered content (a navigation database entry, for example). */
export type ContentReference = { readonly contentHash: string; readonly ident: string; readonly index: number };

/** Registered content: objects that are encoded as references to content, never as values. */
export type ContentRegistry = {
  readonly referenceOf: (value: object) => ContentReference | undefined;
  readonly resolve: (reference: ContentReference) => object | undefined;
};

export type PathStep =
  | { readonly kind: "key"; readonly key: string }
  | { readonly kind: "index"; readonly index: number }
  | { readonly kind: "mapKey"; readonly entry: number }
  | { readonly kind: "mapValue"; readonly entry: number }
  | { readonly kind: "setEntry"; readonly entry: number }
  | { readonly kind: "buffer" };

export type ObjectLocation = { readonly unitId: string; readonly path: readonly PathStep[] };

/** One alias the census found: the object met at `at` is the one first met at `target`. */
export type AliasRecord = { readonly at: ObjectLocation; readonly target: ObjectLocation };

export type EncodedState = { readonly bytes: Uint8Array; readonly aliases: readonly AliasRecord[] };
export type DecodedUnit = { readonly unitId: string; readonly fields: Record<string, unknown> };
export type DecodedState = { readonly utc0Ms: number; readonly units: readonly DecodedUnit[] };

export type EncodeResult = Accepted<EncodedState> | Refused<SnapshotRefusal>;
export type DecodeResult = Accepted<DecodedState> | Refused<RestoreRefusal>;

export type EncodeOptions = {
  readonly utc0Ms: number;
  readonly classes?: readonly ClassRegistration[];
  readonly content?: ContentRegistry;
};

export type DecodeOptions = {
  readonly classes?: readonly ClassRegistration[];
  /** Each unit's declared links, by unit id; a cross-unit reference outside them refuses. */
  readonly links?: Readonly<Record<string, readonly CrossOwnerLink[]>>;
  readonly content?: ContentRegistry;
};

const TAG = {
  undefined: 0x00, null: 0x01, false: 0x02, true: 0x03, number: 0x04, string: 0x05, array: 0x06, object: 0x07,
  nullObject: 0x08, date: 0x09, invalidDate: 0x0a, map: 0x0b, set: 0x0c, buffer: 0x0d, view: 0x0e, hole: 0x0f,
  reference: 0x10, instance: 0x11, content: 0x12,
} as const;

const STEP = { key: 0, index: 1, mapKey: 2, mapValue: 3, setEntry: 4, buffer: 5 } as const;

/** The deepest path, in steps from the unit root, that a value may have. */
const MAX_NESTING = 256;

type ViewConstructor = { readonly prototype: object; new (buffer: ArrayBuffer, byteOffset: number, length: number): ArrayBufferView };
/** View kinds 1..12, in this fixed order. */
const VIEWS: readonly ViewConstructor[] = [
  Int8Array, Uint8Array, Uint8ClampedArray, Int16Array, Uint16Array, Int32Array, Uint32Array, Float32Array, Float64Array,
  BigInt64Array, BigUint64Array, DataView,
];
const BUILT_IN_PROTOTYPES: ReadonlySet<object> = new Set<object>([
  Object.prototype, Array.prototype, Date.prototype, Map.prototype, Set.prototype, ArrayBuffer.prototype, Function.prototype,
  ...VIEWS.map(view => view.prototype),
]);
/** The intrinsic accessors, so a subclass cannot run code while the census walks. */
const typedArrayBuffer = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(Int8Array.prototype) as object, "buffer")!.get!;
const dataViewBuffer = Object.getOwnPropertyDescriptor(DataView.prototype, "buffer")!.get!;

/** The largest |getTime()| of a valid Date. */
const MAX_DATE_MS = 8_640_000_000_000_000n;
const LONE_SURROGATE = /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/;
const ARRAY_INDEX = /^(?:0|[1-9][0-9]*)$/;
const isArrayIndex = (key: string) => ARRAY_INDEX.test(key) && Number(key) < 0xffffffff;

class Refusal<C extends string> {
  readonly code: C;
  readonly detail: string;
  readonly contentHash?: string;
  constructor(code: C, detail: string, contentHash?: string) { this.code = code; this.detail = detail; this.contentHash = contentHash; }
}

function pathText(path: readonly PathStep[]): string {
  return path.map(step => {
    switch (step.kind) {
      case "key": return `.${step.key}`;
      case "index": return `[${step.index}]`;
      case "mapKey": return `<key ${step.entry}>`;
      case "mapValue": return `<value ${step.entry}>`;
      case "setEntry": return `<entry ${step.entry}>`;
      case "buffer": return "<buffer>";
    }
  }).join("") || "(root)";
}

const describe = (location: ObjectLocation) => `${location.unitId}:${pathText(location.path)}`;
const locationKey = (unitId: string, path: readonly PathStep[]) => JSON.stringify([unitId, path]);

/** Whether `links` declares that, at `path`, the holder may hold objects emitted by `owner`. */
function linkCovers(links: readonly CrossOwnerLink[] | undefined, owner: string, path: readonly PathStep[]) {
  return (links ?? []).some(link => link.owner === owner && link.path.length <= path.length &&
    link.path.every((key, i) => {
      const step = path[i];
      return (step.kind === "key" && step.key === key) || (step.kind === "index" && String(step.index) === key);
    }));
}

/** The class registry, refusing duplicate names, duplicate prototypes and built-in prototypes. */
function classRegistry(classes: readonly ClassRegistration[]) {
  const byPrototype = new Map<object, string>();
  const byName = new Map<string, object>();
  for (const { name, prototype } of classes) {
    if (byName.has(name)) throw new Refusal("CLASS_REGISTRY_INVALID", `class name ${name} is registered twice`);
    if (byPrototype.has(prototype)) throw new Refusal("CLASS_REGISTRY_INVALID", `one prototype is registered as both ${byPrototype.get(prototype)} and ${name}`);
    if (BUILT_IN_PROTOTYPES.has(prototype)) throw new Refusal("CLASS_REGISTRY_INVALID", `${name} registers a built-in prototype`);
    byPrototype.set(prototype, name);
    byName.set(name, prototype);
  }
  return { byPrototype, byName };
}

// ------------------------------------------------------------------ bytes

const utf8 = new TextEncoder();

class Writer {
  private bytes = new Uint8Array(4096);
  private view = new DataView(this.bytes.buffer);
  private length = 0;

  private room(n: number) {
    if (this.length + n <= this.bytes.length) return;
    let size = this.bytes.length * 2;
    while (size < this.length + n) size *= 2;
    const next = new Uint8Array(size);
    next.set(this.bytes.subarray(0, this.length));
    this.bytes = next;
    this.view = new DataView(next.buffer);
  }

  u8(value: number) { this.room(1); this.bytes[this.length++] = value; }
  u32(value: number) {
    if (!Number.isInteger(value) || value < 0 || value > 0xffffffff) throw new Refusal("UNCLASSIFIED_VALUE", `a length or index of ${value} does not fit in u32`);
    this.room(4); this.view.setUint32(this.length, value, true); this.length += 4;
  }
  i64(value: bigint) { this.room(8); this.view.setBigInt64(this.length, value, true); this.length += 8; }
  f64(value: number) {
    this.room(8);
    if (Number.isNaN(value)) { this.view.setUint32(this.length, 0, true); this.view.setUint32(this.length + 4, 0x7ff80000, true); }
    else this.view.setFloat64(this.length, value, true);
    this.length += 8;
  }
  raw(bytes: Uint8Array) { this.room(bytes.length); this.bytes.set(bytes, this.length); this.length += bytes.length; }
  str(value: string) { const encoded = utf8.encode(value); this.u32(encoded.length); this.raw(encoded); }
  result() { return this.bytes.slice(0, this.length); }
}

// ------------------------------------------------------------------ encode

const hasDefaultAttributes = (descriptor: PropertyDescriptor) =>
  "value" in descriptor && descriptor.writable === true && descriptor.enumerable === true && descriptor.configurable === true;

class Encoder {
  private readonly w = new Writer();
  private readonly seen = new Map<object, ObjectLocation>();
  /** Everything restore does not take from the snapshot: unit roots and every object reachable from a config,
   *  observer or derived field. */
  private readonly unserialized = new Map<object, string>();
  private readonly roots = new Set<object>();
  private readonly classes: Map<object, string>;
  private readonly content: ContentRegistry | undefined;
  private readonly aliases: AliasRecord[] = [];
  private readonly utc0: bigint;
  private unit: SnapshotUnit = { unitId: "", root: {}, fields: {} };
  private readonly path: PathStep[] = [];

  constructor(utc0Ms: number, classes: readonly ClassRegistration[], content: ContentRegistry | undefined) {
    this.utc0 = BigInt(utc0Ms);
    this.classes = classRegistry(classes).byPrototype;
    this.content = content;
  }

  private refuse(code: SnapshotRefusalCode, detail: string): never {
    throw new Refusal(code, `${detail} at ${describe({ unitId: this.unit.unitId, path: this.path })}`);
  }

  run(units: readonly SnapshotUnit[]) {
    const order = new Map<string, number>();
    units.forEach((unit, i) => {
      if (order.has(unit.unitId)) throw new Refusal("OWNERSHIP_AMBIGUOUS", `duplicate unit id ${unit.unitId}`);
      order.set(unit.unitId, i);
      this.roots.add(unit.root);
      this.unserialized.set(unit.root, `the root of unit ${unit.unitId}`);
    });
    units.forEach((unit, i) => {
      for (const link of unit.links ?? []) {
        const owner = order.get(link.owner);
        if (owner === undefined || owner === i) throw new Refusal("OWNERSHIP_AMBIGUOUS", `unit ${unit.unitId} declares a link to ${link.owner}, which is not another unit of this snapshot`);
        if (owner > i) throw new Refusal("OWNER_AFTER_HOLDER", `unit ${unit.unitId} declares a link to ${link.owner}, which comes after it; owners must precede holders`);
      }
    });
    for (const unit of units) {
      for (const key of Reflect.ownKeys(unit.root)) {
        if (typeof key !== "string") throw new Refusal("UNCLASSIFIED_PROPERTY", `a symbol-keyed property on unit ${unit.unitId}`);
        const fieldClass = Object.hasOwn(unit.fields, key) ? unit.fields[key] : undefined;
        if (fieldClass === undefined) throw new Refusal("UNCLASSIFIED_PROPERTY", `unit ${unit.unitId} property ${key} is not classified`);
        const descriptor = Object.getOwnPropertyDescriptor(unit.root, key)!;
        if (!("value" in descriptor)) throw new Refusal("UNCLASSIFIED_PROPERTY", `unit ${unit.unitId} property ${key} is an accessor`);
        if (fieldClass === "config" || fieldClass === "observer" || fieldClass === "derived") {
          this.markUnserialized(descriptor.value, `the ${fieldClass} field ${unit.unitId}.${key}`);
        }
      }
    }
    this.w.i64(this.utc0);
    this.w.u32(units.length);
    for (const unit of units) {
      this.unit = unit;
      this.w.str(this.checkedString(unit.unitId));
      const keys = Reflect.ownKeys(unit.root).filter((key): key is string => typeof key === "string" && unit.fields[key] === "authoritative");
      this.w.u32(keys.length);
      for (const key of keys) {
        this.w.str(this.checkedString(key));
        this.at({ kind: "key", key }, Object.getOwnPropertyDescriptor(unit.root, key)!.value);
      }
    }
    for (const unit of units) this.checkLinkFields(unit);
    return { bytes: this.w.result(), aliases: this.aliases };
  }

  /** Marks everything reachable from `start` (own data properties, elements, entries, view buffers) as unserialized.
   *  Iterative and refusing nothing; it stops at unit roots, functions and registered content. */
  private markUnserialized(start: unknown, description: string) {
    const stack: unknown[] = [start];
    while (stack.length > 0) {
      const value = stack.pop();
      if (typeof value !== "object" || value === null || this.unserialized.has(value)) continue;
      if (this.content?.referenceOf(value) !== undefined) continue;
      this.unserialized.set(value, description);
      if (ArrayBuffer.isView(value)) {
        stack.push(value instanceof DataView ? dataViewBuffer.call(value) : typedArrayBuffer.call(value));
        continue;
      }
      try { Map.prototype.forEach.call(value, (item: unknown, key: unknown) => { stack.push(key, item); }); } catch { /* not a Map */ }
      try { Set.prototype.forEach.call(value, (item: unknown) => { stack.push(item); }); } catch { /* not a Set */ }
      for (const key of Reflect.ownKeys(value)) {
        const descriptor = Object.getOwnPropertyDescriptor(value, key);
        if (descriptor !== undefined && "value" in descriptor) stack.push(descriptor.value);
      }
    }
  }

  /** A link field holds a unit root, registered content, or an object emitted by the owner its declared link names. */
  private checkLinkFields(unit: SnapshotUnit) {
    this.unit = unit;
    for (const key of Reflect.ownKeys(unit.root)) {
      if (typeof key !== "string" || unit.fields[key] !== "link") continue;
      const value: unknown = Object.getOwnPropertyDescriptor(unit.root, key)!.value;
      if (typeof value !== "object" || value === null || this.roots.has(value) || this.content?.referenceOf(value) !== undefined) continue;
      const emitted = this.seen.get(value);
      const step: PathStep[] = [{ kind: "key", key }];
      if (emitted === undefined || emitted.unitId === unit.unitId || !linkCovers(unit.links, emitted.unitId, step)) {
        throw new Refusal("UNCLASSIFIED_ALIAS", `the link field ${unit.unitId}.${key} holds an object that ${emitted ? `${describe(emitted)} emits` : "no unit emits"} with no declared link to its owner`);
      }
    }
  }

  private checkedString(value: string) {
    if (LONE_SURROGATE.test(value)) this.refuse("UNCLASSIFIED_VALUE", "a string with a lone surrogate (no exact UTF-8)");
    return value;
  }

  private at(step: PathStep, value: unknown) {
    this.path.push(step);
    if (this.path.length > MAX_NESTING) this.refuse("NESTING_LIMIT", `state nested deeper than ${MAX_NESTING} steps`);
    this.value(value);
    this.path.pop();
  }

  private value(value: unknown): void {
    const w = this.w;
    switch (typeof value) {
      case "undefined": w.u8(TAG.undefined); return;
      case "boolean": w.u8(value ? TAG.true : TAG.false); return;
      case "number": w.u8(TAG.number); w.f64(value); return;
      case "string": w.u8(TAG.string); w.str(this.checkedString(value)); return;
      case "object": if (value === null) { w.u8(TAG.null); return; } break;
      default: this.refuse("UNCLASSIFIED_VALUE", `a ${typeof value}`);
    }
    this.object(value as object);
  }

  private object(value: object) {
    const w = this.w;
    const content = this.content?.referenceOf(value);
    if (content !== undefined) {
      w.u8(TAG.content); w.str(this.checkedString(content.contentHash)); w.str(this.checkedString(content.ident)); w.u32(content.index);
      return;
    }
    const unserialized = this.unserialized.get(value);
    if (unserialized !== undefined) this.refuse("UNCLASSIFIED_ALIAS", `serialized state reaches an object held by ${unserialized}`);
    const prior = this.seen.get(value);
    if (prior) {
      if (prior.unitId !== this.unit.unitId && !linkCovers(this.unit.links, prior.unitId, this.path)) {
        this.refuse("OWNERSHIP_AMBIGUOUS", `an object owned by ${describe(prior)} with no declared link`);
      }
      this.aliases.push({ at: { unitId: this.unit.unitId, path: [...this.path] }, target: prior });
      w.u8(TAG.reference);
      w.str(prior.unitId);
      w.u32(prior.path.length);
      for (const step of prior.path) writeStep(w, step);
      return;
    }
    this.seen.set(value, { unitId: this.unit.unitId, path: [...this.path] });

    const proto: unknown = Object.getPrototypeOf(value);
    if (proto === Object.prototype) { w.u8(TAG.object); this.properties(value); return; }
    if (proto === null) { w.u8(TAG.nullObject); this.properties(value); return; }
    if (proto === Array.prototype && Array.isArray(value)) { this.array(value as unknown[]); return; }
    if (proto === Date.prototype) {
      this.noOwnProperties(value);
      const time = (value as Date).getTime();
      if (Number.isNaN(time)) w.u8(TAG.invalidDate);
      else { w.u8(TAG.date); w.i64(BigInt(time) - this.utc0); }
      return;
    }
    if (proto === Map.prototype) {
      this.noOwnProperties(value);
      const map = value as Map<unknown, unknown>;
      w.u8(TAG.map); w.u32(map.size);
      let entry = 0;
      for (const [key, item] of map) {
        this.at({ kind: "mapKey", entry }, key);
        this.at({ kind: "mapValue", entry }, item);
        entry += 1;
      }
      return;
    }
    if (proto === Set.prototype) {
      this.noOwnProperties(value);
      const set = value as Set<unknown>;
      w.u8(TAG.set); w.u32(set.size);
      let entry = 0;
      for (const item of set) { this.at({ kind: "setEntry", entry }, item); entry += 1; }
      return;
    }
    if (proto === ArrayBuffer.prototype) {
      this.noOwnProperties(value);
      const buffer = value as ArrayBuffer & { readonly resizable?: boolean; readonly detached?: boolean };
      if (buffer.resizable === true || buffer.detached === true) this.refuse("UNCLASSIFIED_VALUE", "a resizable or detached ArrayBuffer");
      w.u8(TAG.buffer); w.u32(buffer.byteLength); w.raw(new Uint8Array(buffer));
      return;
    }
    const kind = VIEWS.findIndex(view => view.prototype === proto);
    if (kind >= 0) {
      this.noOwnProperties(value, true);
      const view = value as ArrayBufferView & { readonly length?: number };
      w.u8(TAG.view); w.u8(kind + 1);
      this.at({ kind: "buffer" }, view.buffer);
      w.u32(view.byteOffset);
      w.u32(view instanceof DataView ? view.byteLength : view.length!);
      return;
    }
    const className = this.classes.get(proto as object);
    if (className !== undefined) { w.u8(TAG.instance); w.str(className); this.properties(value); return; }
    const name = (proto as { constructor?: { name?: unknown } } | undefined)?.constructor?.name;
    this.refuse("UNCLASSIFIED_VALUE", `an instance of the unregistered class ${typeof name === "string" ? name : "(anonymous)"}`);
  }

  private noOwnProperties(value: object, typedArray = false) {
    if (!Object.isExtensible(value)) this.refuse("UNCLASSIFIED_VALUE", "a non-extensible object");
    // A typed array's elements are own properties; its state is its buffer, offset and length.
    const extra = Reflect.ownKeys(value).filter(key => !(typedArray && typeof key === "string" && isArrayIndex(key)));
    if (extra.length > 0) this.refuse("UNCLASSIFIED_VALUE", `own property ${String(extra[0])} on a built-in object`);
  }

  private properties(value: object) {
    if (!Object.isExtensible(value)) this.refuse("UNCLASSIFIED_VALUE", "a non-extensible object");
    const keys = Reflect.ownKeys(value);
    this.w.u32(keys.length);
    for (const key of keys) {
      if (typeof key !== "string") this.refuse("UNCLASSIFIED_VALUE", "a symbol-keyed property");
      const descriptor = Object.getOwnPropertyDescriptor(value, key)!;
      if (!hasDefaultAttributes(descriptor)) this.refuse("UNCLASSIFIED_VALUE", `property ${key} is an accessor or has non-default attributes`);
      this.w.str(this.checkedString(key));
      this.at({ kind: "key", key }, descriptor.value);
    }
  }

  private array(value: unknown[]) {
    if (!Object.isExtensible(value)) this.refuse("UNCLASSIFIED_VALUE", "a non-extensible array");
    for (const key of Reflect.ownKeys(value)) {
      if (key === "length") continue;
      if (typeof key !== "string" || !isArrayIndex(key)) this.refuse("UNCLASSIFIED_VALUE", `array property ${String(key)}`);
      if (!hasDefaultAttributes(Object.getOwnPropertyDescriptor(value, key)!)) this.refuse("UNCLASSIFIED_VALUE", `array element ${key} is an accessor or has non-default attributes`);
    }
    if (Object.getOwnPropertyDescriptor(value, "length")!.writable !== true) this.refuse("UNCLASSIFIED_VALUE", "an array with a fixed length");
    this.w.u8(TAG.array); this.w.u32(value.length);
    for (let index = 0; index < value.length; index += 1) {
      if (!Object.hasOwn(value, index)) { this.w.u8(TAG.hole); continue; }
      this.at({ kind: "index", index }, value[index]);
    }
  }

  /** One standalone value, outside any unit (the envelope header). */
  single(owner: string, value: unknown) {
    this.unit = { unitId: owner, root: {}, fields: {} };
    this.value(value);
    return this.w.result();
  }
}

function writeStep(w: Writer, step: PathStep) {
  switch (step.kind) {
    case "key": w.u8(STEP.key); w.str(step.key); return;
    case "index": w.u8(STEP.index); w.u32(step.index); return;
    case "mapKey": w.u8(STEP.mapKey); w.u32(step.entry); return;
    case "mapValue": w.u8(STEP.mapValue); w.u32(step.entry); return;
    case "setEntry": w.u8(STEP.setEntry); w.u32(step.entry); return;
    case "buffer": w.u8(STEP.buffer); return;
  }
}

function encodeRefusal(error: unknown): Refused<SnapshotRefusal> {
  if (error instanceof Refusal) return { ok: false, refusal: { code: error.code as SnapshotRefusalCode, detail: error.detail } };
  throw error;
}

/**
 * Encodes the authoritative state of `units`, in the given order, relative to `utc0Ms`. Pure: it reads own data
 * properties only and changes nothing. A unit that holds another unit's objects must come after it.
 */
export function encodeState(units: readonly SnapshotUnit[], options: EncodeOptions): EncodeResult {
  if (!Number.isSafeInteger(options.utc0Ms) || BigInt(Math.abs(options.utc0Ms)) > MAX_DATE_MS) {
    return { ok: false, refusal: { code: "INVALID_UTC0", detail: `UTC0 must be whole epoch milliseconds of a valid Date, not ${options.utc0Ms}` } };
  }
  try {
    return { ok: true, value: new Encoder(options.utc0Ms, options.classes ?? [], options.content).run(units) };
  } catch (error) {
    return encodeRefusal(error);
  }
}

/** Encodes one standalone value with the same value encoding (no UTC0: it must hold no Date). */
export function encodeValue(owner: string, value: unknown): Accepted<Uint8Array> | Refused<SnapshotRefusal> {
  try {
    return { ok: true, value: new Encoder(0, [], undefined).single(owner, value) };
  } catch (error) {
    return encodeRefusal(error);
  }
}

// ------------------------------------------------------------------ decode

const strictUtf8 = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true });

class Reader {
  private readonly view: DataView;
  private readonly bytes: Uint8Array;
  position = 0;
  constructor(bytes: Uint8Array) {
    this.bytes = bytes;
    this.view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  }
  get remaining() { return this.bytes.length - this.position; }
  private need(n: number) { if (this.remaining < n) throw new Refusal("NON_CANONICAL", `truncated at byte ${this.position}`); }
  u8() { this.need(1); return this.bytes[this.position++]; }
  u32() { this.need(4); const value = this.view.getUint32(this.position, true); this.position += 4; return value; }
  i64() { this.need(8); const value = this.view.getBigInt64(this.position, true); this.position += 8; return value; }
  f64() {
    this.need(8);
    const value = this.view.getFloat64(this.position, true);
    if (Number.isNaN(value) && (this.view.getUint32(this.position, true) !== 0 || this.view.getUint32(this.position + 4, true) !== 0x7ff80000)) {
      throw new Refusal("NON_CANONICAL", `a non-canonical NaN at byte ${this.position}`);
    }
    this.position += 8;
    return value;
  }
  raw(n: number) { this.need(n); const out = this.bytes.slice(this.position, this.position + n); this.position += n; return out; }
  str() {
    const length = this.u32();
    const at = this.position;
    try { return strictUtf8.decode(this.raw(length)); } catch { throw new Refusal("NON_CANONICAL", `invalid UTF-8 at byte ${at}`); }
  }
}

const HOLE: unique symbol = Symbol("hole");

class Decoder {
  private readonly r: Reader;
  private readonly classes: Map<string, object>;
  private readonly links: Readonly<Record<string, readonly CrossOwnerLink[]>>;
  private readonly content: ContentRegistry | undefined;
  private readonly objects = new Map<string, object>();
  private utc0 = 0n;
  private unitId = "";
  private readonly path: PathStep[] = [];

  constructor(bytes: Uint8Array, options: DecodeOptions) {
    this.r = new Reader(bytes);
    this.classes = classRegistry(options.classes ?? []).byName;
    this.links = options.links ?? {};
    this.content = options.content;
  }

  private refuse(code: RestoreRefusalCode, detail: string, contentHash?: string): never {
    throw new Refusal(code, `${detail} at ${describe({ unitId: this.unitId, path: this.path })}`, contentHash);
  }

  run(): DecodedState {
    const r = this.r;
    this.utc0 = r.i64();
    if (this.utc0 > MAX_DATE_MS || this.utc0 < -MAX_DATE_MS) this.refuse("NON_CANONICAL", "UTC0 out of range");
    const count = r.u32();
    const units: DecodedUnit[] = [];
    const ids = new Set<string>();
    for (let u = 0; u < count; u += 1) {
      this.unitId = r.str();
      if (ids.has(this.unitId)) this.refuse("OWNERSHIP_AMBIGUOUS", `duplicate unit id ${this.unitId}`);
      ids.add(this.unitId);
      const fields: Record<string, unknown> = {};
      this.properties(fields);
      units.push({ unitId: this.unitId, fields });
    }
    if (r.remaining !== 0) this.refuse("NON_CANONICAL", `${r.remaining} trailing bytes`);
    return { utc0Ms: Number(this.utc0), units };
  }

  private register(value: object) { this.objects.set(locationKey(this.unitId, this.path), value); }

  private at(step: PathStep, read: () => unknown) {
    this.path.push(step);
    try {
      if (this.path.length > MAX_NESTING) this.refuse("NON_CANONICAL", `bytes nested deeper than ${MAX_NESTING} steps`);
      return read();
    } finally { this.path.pop(); }
  }

  /** A value; `hole` is returned only where an array element may be absent. */
  private value(allowHole = false): unknown {
    const r = this.r;
    const tag = r.u8();
    switch (tag) {
      case TAG.undefined: return undefined;
      case TAG.null: return null;
      case TAG.false: return false;
      case TAG.true: return true;
      case TAG.number: return r.f64();
      case TAG.string: return r.str();
      case TAG.hole: if (allowHole) return HOLE; break;
      case TAG.object: { const out = {}; this.register(out); this.properties(out); return out; }
      case TAG.nullObject: { const out = Object.create(null) as object; this.register(out); this.properties(out); return out; }
      case TAG.instance: {
        const name = r.str();
        const proto = this.classes.get(name);
        if (proto === undefined) this.refuse("UNCLASSIFIED_VALUE", `unregistered class ${name}`);
        const out = Object.create(proto) as object;
        this.register(out);
        this.properties(out);
        return out;
      }
      case TAG.array: {
        const length = r.u32();
        const out: unknown[] = [];
        this.register(out);
        out.length = length;
        for (let index = 0; index < length; index += 1) {
          const item = this.at({ kind: "index", index }, () => this.value(true));
          if (item !== HOLE) out[index] = item;
        }
        return out;
      }
      case TAG.date: {
        const time = this.utc0 + r.i64();
        if (time > MAX_DATE_MS || time < -MAX_DATE_MS) this.refuse("NON_CANONICAL", "a Date out of range");
        const out = new Date(Number(time));
        this.register(out);
        return out;
      }
      case TAG.invalidDate: { const out = new Date(Number.NaN); this.register(out); return out; }
      case TAG.map: {
        const size = r.u32();
        const out = new Map<unknown, unknown>();
        this.register(out);
        for (let entry = 0; entry < size; entry += 1) {
          const key = this.at({ kind: "mapKey", entry }, () => this.value());
          const item = this.at({ kind: "mapValue", entry }, () => this.value());
          if (out.has(key) || Object.is(key, -0)) this.refuse("NON_CANONICAL", "a duplicate or non-canonical Map key");
          out.set(key, item);
        }
        return out;
      }
      case TAG.set: {
        const size = r.u32();
        const out = new Set<unknown>();
        this.register(out);
        for (let entry = 0; entry < size; entry += 1) {
          const item = this.at({ kind: "setEntry", entry }, () => this.value());
          if (out.has(item) || Object.is(item, -0)) this.refuse("NON_CANONICAL", "a duplicate or non-canonical Set entry");
          out.add(item);
        }
        return out;
      }
      case TAG.buffer: {
        const out = r.raw(r.u32()).buffer;
        this.register(out);
        return out;
      }
      case TAG.view: {
        const kind = r.u8();
        const View = VIEWS[kind - 1];
        if (View === undefined) this.refuse("NON_CANONICAL", `unknown view kind ${kind}`);
        const buffer = this.at({ kind: "buffer" }, () => this.value());
        if (!(buffer instanceof ArrayBuffer)) this.refuse("NON_CANONICAL", "a view over something other than an ArrayBuffer");
        const byteOffset = r.u32(), length = r.u32();
        let out: ArrayBufferView;
        try { out = new View(buffer, byteOffset, length); } catch { this.refuse("NON_CANONICAL", "a view outside its buffer or misaligned"); }
        this.register(out);
        return out;
      }
      case TAG.reference: {
        const unitId = r.str();
        const count = r.u32();
        if (count > MAX_NESTING) this.refuse("NON_CANONICAL", `a reference path deeper than ${MAX_NESTING} steps`);
        const steps: PathStep[] = [];
        for (let n = 0; n < count; n += 1) steps.push(this.step());
        if (unitId !== this.unitId && !linkCovers(this.links[this.unitId], unitId, this.path)) {
          this.refuse("OWNERSHIP_AMBIGUOUS", `a reference to ${describe({ unitId, path: steps })} with no declared link`);
        }
        const target = this.objects.get(locationKey(unitId, steps));
        if (target === undefined) this.refuse("REFERENCE_UNRESOLVED", `no object at ${describe({ unitId, path: steps })}`);
        return target;
      }
      case TAG.content: {
        const reference: ContentReference = { contentHash: r.str(), ident: r.str(), index: r.u32() };
        const target = this.content?.resolve(reference);
        if (target === undefined) this.refuse("CONTENT_MISSING", `content ${reference.contentHash} ${reference.ident} #${reference.index} is not registered`, reference.contentHash);
        return target;
      }
    }
    this.refuse("NON_CANONICAL", `unexpected tag 0x${tag.toString(16)} at byte ${r.position - 1}`);
  }

  private step(): PathStep {
    const r = this.r;
    const kind = r.u8();
    switch (kind) {
      case STEP.key: return { kind: "key", key: r.str() };
      case STEP.index: return { kind: "index", index: r.u32() };
      case STEP.mapKey: return { kind: "mapKey", entry: r.u32() };
      case STEP.mapValue: return { kind: "mapValue", entry: r.u32() };
      case STEP.setEntry: return { kind: "setEntry", entry: r.u32() };
      case STEP.buffer: return { kind: "buffer" };
    }
    this.refuse("NON_CANONICAL", `unknown path step ${kind}`);
  }

  /** Own data properties, defined (never assigned, so `__proto__` stays an ordinary key) in canonical order. */
  private properties(out: object) {
    const count = this.r.u32();
    const keys: string[] = [];
    for (let n = 0; n < count; n += 1) {
      const key = this.r.str();
      if (Object.hasOwn(out, key)) this.refuse("NON_CANONICAL", `duplicate key ${key}`);
      const item = this.at({ kind: "key", key }, () => this.value());
      Object.defineProperty(out, key, { value: item, writable: true, enumerable: true, configurable: true });
      keys.push(key);
    }
    const order = Reflect.ownKeys(out);
    if (order.some((key, i) => key !== keys[i])) this.refuse("NON_CANONICAL", "keys out of ECMAScript own-key order");
  }
}

/**
 * Decodes bytes produced by `encodeState`. Anything else refuses; references are resolved against the decoded graph,
 * and a reference into another unit must fall under one of the holder's declared links.
 */
export function decodeState(bytes: Uint8Array, options: DecodeOptions = {}): DecodeResult {
  try {
    return { ok: true, value: new Decoder(bytes, options).run() };
  } catch (error) {
    if (error instanceof Refusal) {
      const refusal: RestoreRefusal = error.contentHash === undefined
        ? { code: error.code as RestoreRefusalCode, detail: error.detail }
        : { code: error.code as RestoreRefusalCode, detail: error.detail, contentHash: error.contentHash };
      return { ok: false, refusal };
    }
    throw error;
  }
}
