import type { Accepted, Refused, SnapshotRefusal } from "./refusal";

export type IntegrityResult = Accepted<string> | Refused<SnapshotRefusal>;

/**
 * SHA-256 of `bytes` as 64 lower-case hex digits, through WebCrypto. Where `crypto.subtle` is absent (a page outside a
 * secure context) or the digest fails, the result is an explicit `INTEGRITY_UNAVAILABLE` refusal: there is no
 * fallback hash and the check is never skipped.
 */
export async function sha256(bytes: Uint8Array): Promise<IntegrityResult> {
  const subtle = (globalThis as { crypto?: { subtle?: SubtleCrypto } }).crypto?.subtle;
  if (subtle === undefined) {
    return { ok: false, refusal: { code: "INTEGRITY_UNAVAILABLE", detail: "crypto.subtle is not available in this context" } };
  }
  let digest: ArrayBuffer;
  try {
    // Exactly the viewed bytes, never the rest of an underlying buffer the view may share.
    digest = await subtle.digest("SHA-256", bytes.slice());
  } catch (error) {
    return { ok: false, refusal: { code: "INTEGRITY_UNAVAILABLE", detail: `SHA-256 failed: ${error instanceof Error ? error.message : String(error)}` } };
  }
  return { ok: true, value: Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("") };
}
