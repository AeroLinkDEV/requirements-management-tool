/** Copies authored CSS in cascade order, without booting another application or relaxing its policy. */
export function mirrorStationStyles(owner: Document, child: Document, onFailure: (error: Error) => void) {
  const ownerWindow = owner.defaultView!;
  const copies = new Map<Element, { node: Element; fingerprint: string }>();
  const cancellations = new Set<() => void>();
  let disposed = false;

  const copyRootPreferences = () => {
    for (const name of ["lang", "data-density", "data-motion"]) {
      const value = owner.documentElement.getAttribute(name);
      if (value === null) child.documentElement.removeAttribute(name);
      else child.documentElement.setAttribute(name, value);
    }
  };

  const copy = (source: Element): { node: Element; loaded: Promise<void> } => {
    const node = child.importNode(source, true);
    if (node.tagName !== "LINK") return { node, loaded: Promise.resolve() };
    const link = node as HTMLLinkElement;
    // A blank child inherits the opener's policy; base-uri 'none' forbids a <base> element.
    const url = new URL((source as HTMLLinkElement).href, owner.baseURI);
    if (url.origin !== new URL(owner.baseURI).origin || !["http:", "https:"].includes(url.protocol)) {
      throw new Error("The station stylesheet must come from this AeroLink installation.");
    }
    link.href = url.href;
    const loaded = new Promise<void>((resolve, reject) => {
      let timer = 0;
      const finish = (error?: Error) => {
        ownerWindow.clearTimeout(timer);
        link.removeEventListener("load", success);
        link.removeEventListener("error", failure);
        cancellations.delete(cancel);
        if (error) reject(error); else resolve();
      };
      const success = () => finish();
      const failure = () => finish(new Error("The station stylesheet could not load. The panel remains in the bench."));
      const cancel = () => finish(new Error("Station stylesheet loading was cancelled."));
      cancellations.add(cancel);
      link.addEventListener("load", success, { once: true });
      link.addEventListener("error", failure, { once: true });
      timer = ownerWindow.setTimeout(failure, 15_000);
    });
    return { node, loaded };
  };

  const sync = () => {
    if (disposed) return [];
    const sources = [...owner.head.querySelectorAll("style, link[rel~='stylesheet']")].filter(source => {
      if (source.tagName !== "LINK") return true;
      // Extension styles belong to the browser extension, rather than this application's cascade.
      const protocol = new URL((source as HTMLLinkElement).href, owner.baseURI).protocol;
      return protocol !== "chrome-extension:" && protocol !== "moz-extension:";
    });
    const loads: Promise<void>[] = [];
    for (const [source, entry] of copies) if (!sources.includes(source)) { entry.node.remove(); copies.delete(source); }
    for (const source of sources) {
      const fingerprint = source.outerHTML;
      let entry = copies.get(source);
      if (entry?.fingerprint !== fingerprint) {
        entry?.node.remove();
        const created = copy(source);
        entry = { node: created.node, fingerprint };
        copies.set(source, entry);
        loads.push(created.loaded);
        // Also observe failures if a later source throws before this sync can return its aggregate.
        void created.loaded.catch(error => { if (!disposed) onFailure(error instanceof Error ? error : new Error(String(error))); });
      }
      // Moving an existing stylesheet node keeps its loaded sheet while matching the owner's cascade order.
      child.head.appendChild(entry.node);
    }
    copyRootPreferences();
    return loads;
  };

  let ready: Promise<void>;
  try { ready = Promise.all(sync()).then(() => undefined); }
  catch (error) {
    // A later invalid source must not strand earlier link loads when no disposer has been returned yet.
    disposed = true;
    for (const cancel of [...cancellations]) cancel();
    for (const entry of copies.values()) entry.node.remove();
    copies.clear();
    throw error;
  }
  const stylesObserver = new MutationObserver(() => {
    try { void Promise.all(sync()).catch(error => { if (!disposed) onFailure(error instanceof Error ? error : new Error(String(error))); }); }
    catch (error) { if (!disposed) onFailure(error instanceof Error ? error : new Error(String(error))); }
  });
  stylesObserver.observe(owner.head, { childList: true, subtree: true, attributes: true, characterData: true });
  const preferencesObserver = new MutationObserver(copyRootPreferences);
  preferencesObserver.observe(owner.documentElement, { attributes: true, attributeFilter: ["lang", "data-density", "data-motion"] });
  return {
    ready,
    dispose() {
      disposed = true;
      stylesObserver.disconnect();
      preferencesObserver.disconnect();
      for (const cancel of [...cancellations]) cancel();
      for (const entry of copies.values()) entry.node.remove();
      copies.clear();
    },
  };
}
