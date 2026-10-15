/**
 * Typed refusals of the whole-system snapshot (#1516, stage I2 of #1502 D7). A snapshot or restore that cannot be made
 * exactly is refused with one of these codes; it never degrades to an approximate result.
 */

/** Refusals when a snapshot is taken (capture, then seal). */
export type SnapshotRefusalCode =
  /** The kernel is not stopped at a snapshot boundary S_k (for example R_j, or the run-end halt). */
  | "NOT_AT_BOUNDARY"
  /** An exception inside the composite INTEGRATE has left the kernel faulted. */
  | "FAULTED"
  /** A value the codec has no exact encoding for: a function, symbol, BigInt, unregistered class instance, accessor
   *  property, non-extensible object, non-default property attributes, an ill-formed string or an unusable buffer. */
  | "UNCLASSIFIED_VALUE"
  /** A unit root's own property that its schema does not classify (authoritative, link, config, observer, derived). */
  | "UNCLASSIFIED_PROPERTY"
  /** One object would be restored as two: authoritative state reaches a unit root; a config field's graph reaches an
   *  object authoritative state owns (configuration is rebuilt separately); or a link field's graph reaches an object
   *  the holder itself emits, or another unit emits with no declared link for that field. */
  | "UNCLASSIFIED_ALIAS"
  /** Two distinct objects share one content reference, so decode could not keep them distinct. */
  | "CONTENT_AMBIGUOUS"
  /** An object reachable from two units without a declared owner, a link to a unit not in the snapshot, or a duplicate
   *  unit id. */
  | "OWNERSHIP_AMBIGUOUS"
  /** A unit declares a link to an owner that comes after it in the unit order; owners must precede holders. */
  | "OWNER_AFTER_HOLDER"
  /** State nested deeper than the format's declared limit (256 path steps). */
  | "NESTING_LIMIT"
  /** The class registry names a class twice, registers one prototype twice, or registers a built-in prototype. */
  | "CLASS_REGISTRY_INVALID"
  /** UTC0 is not a valid whole number of epoch milliseconds. */
  | "INVALID_UTC0"
  /** SHA-256 cannot be computed here (`crypto.subtle` is absent, for example outside a secure context). */
  | "INTEGRITY_UNAVAILABLE";

/**
 * Refusals when a snapshot is decoded or restored. The version-policy codes are used from I2e (`Kernel.restore()`);
 * the decode codes are produced by the codec now.
 */
export type RestoreRefusalCode =
  /** The bytes are not the canonical encoding of any value: truncated, trailing bytes, an unknown tag, a non-canonical
   *  NaN, keys out of ECMAScript own-key order, duplicate keys or entries, invalid UTF-8, a Date or UTC0 out of range,
   *  nesting deeper than 256 path steps. */
  | "NON_CANONICAL"
  /** A reference names no object that the decoded graph holds at that path. */
  | "REFERENCE_UNRESOLVED"
  /** A cross-unit reference outside the holder's declared links, or a duplicate unit id. */
  | "OWNERSHIP_AMBIGUOUS"
  | "CLASS_REGISTRY_INVALID"
  /** A registered-class encoding names a class this build does not register. */
  | "UNCLASSIFIED_VALUE"
  | "INTEGRITY_UNAVAILABLE"
  /** The SHA-256 does not match the header and state bytes. */
  | "INTEGRITY"
  /** A unit schema version newer than this build supports. */
  | "SCHEMA_NEWER"
  /** An older unit schema version with no explicit migration. */
  | "SCHEMA_OLDER_NO_MIGRATION"
  /** A unit this build cannot restore. */
  | "UNIT_UNSUPPORTED"
  /** The rebuilt configuration differs from the recorded configuration identity. */
  | "CONFIG_MISMATCH"
  /** A content reference (navigation database cycle, user database, magnetic database) is not in the content store. */
  | "CONTENT_MISSING"
  /** The snapshot does not cover every unit, so it cannot be restored. */
  | "COVERAGE_PARTIAL";

export type SnapshotRefusal = {
  readonly code: SnapshotRefusalCode;
  readonly detail: string;
};

export type RestoreRefusal = {
  readonly code: RestoreRefusalCode;
  readonly detail: string;
  /** For `CONTENT_MISSING`: the missing content hash. */
  readonly contentHash?: string;
};

export type Refused<R> = { readonly ok: false; readonly refusal: R };
export type Accepted<T> = { readonly ok: true; readonly value: T };
