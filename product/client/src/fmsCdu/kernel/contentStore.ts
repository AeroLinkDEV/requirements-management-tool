/** I1c dataset identity. Hashing/resolution is asynchronous and never runs in a simulation frame. */
export type ContentRef = { readonly sha256: string; readonly bytes: number };
export type StoredContent = ContentRef & { readonly data: readonly number[] };
export const CONTENT_SCHEMA = "aerolink.fms-content.v1";

export async function sha256(bytes: Uint8Array): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new Uint8Array(bytes));
  return Array.from(new Uint8Array(digest), value => value.toString(16).padStart(2, "0")).join("");
}

/** Exact bytes, not a reserialization of the model's parsed database. The exported store is self-contained. */
export class ContentStore {
  private readonly values = new Map<string, Uint8Array>();

  async put(bytes: Uint8Array): Promise<ContentRef> {
    const value = new Uint8Array(bytes), hash = await sha256(value);
    this.values.set(hash, value);
    return { sha256: hash, bytes: value.byteLength };
  }

  async putText(text: string): Promise<ContentRef> { return this.put(new TextEncoder().encode(text)); }

  async resolve(ref: ContentRef): Promise<Uint8Array> {
    const value = this.values.get(ref.sha256);
    if (!value) throw new Error(`missing content ${ref.sha256}`);
    if (value.byteLength !== ref.bytes || await sha256(value) !== ref.sha256) throw new Error(`corrupt content ${ref.sha256}`);
    return new Uint8Array(value);
  }

  // Saved store strings are exact inputs, including a leading U+FEFF. File imports decode separately.
  async text(ref: ContentRef): Promise<string> { return new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(await this.resolve(ref)); }

  export(): readonly StoredContent[] {
    return [...this.values].sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0)
      .map(([hash, data]) => ({ sha256: hash, bytes: data.byteLength, data: Array.from(data) }));
  }

  static async from(entries: readonly StoredContent[]): Promise<ContentStore> {
    const store = new ContentStore();
    for (const entry of entries) {
      if (!/^[0-9a-f]{64}$/.test(entry.sha256) || !Number.isSafeInteger(entry.bytes) || entry.bytes < 0
        || entry.data.length !== entry.bytes || entry.data.some(value => !Number.isInteger(value) || value < 0 || value > 255)
        || store.values.has(entry.sha256)) throw new Error(`invalid content ${entry.sha256}`);
      const bytes = new Uint8Array(entry.data);
      if (await sha256(bytes) !== entry.sha256) throw new Error(`corrupt content ${entry.sha256}`);
      store.values.set(entry.sha256, bytes);
    }
    return store;
  }
}
