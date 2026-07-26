import { useCallback, useEffect, useMemo, useRef, useState } from "react";

/**
 * Autosave, as this product means it.
 *
 * Autosave here protects *typing*, never the record. A draft is held so a dead laptop, a closed tab, or a
 * misjudged click does not cost somebody an afternoon of writing — but nothing typed becomes part of a
 * controlled artifact until they check in, submit, or sign. If a keystroke could quietly alter an approved
 * requirement there would be no attributable act, no signature over a known snapshot, and no reproducible
 * record; the whole control model would be gone. So a draft is always a draft, and the deliberate act stays
 * deliberate.
 *
 * Two places a draft can live, and the difference is not cosmetic:
 *
 *  - A **server draft**, inside a checked-out edit session, which survives the machine it was typed on and
 *    is visible to whoever administers the project. This is what the change-request and procedure editors
 *    already use, and it is the stronger guarantee.
 *  - A **local draft**, in this browser, for a record that does not exist on the server yet. There is
 *    nothing to attach a server draft to before the artifact is created, and reserving one would consume an
 *    identifier for something nobody submitted.
 */

export type AutosaveStatus = "Idle" | "Saving" | "Saved" | "Error" | "Conflict";

/**
 * Runs `save` a short pause after the value stops changing.
 *
 * Debounced rather than on a timer: a timer either fires too often, wasting writes on an idle form, or too
 * rarely, leaving the last few seconds of typing unprotected. A pause after the last keystroke is what a
 * person actually means by "saved as I go".
 *
 * The first render never saves. Loading a form is not editing it, and writing a draft identical to the
 * record would make every opened form look modified.
 */
export function useDebouncedSave<T>(
  value: T,
  save: (value: T) => void | Promise<void>,
  options: { delaySeconds?: number; enabled?: boolean; maximumSeconds?: number } = {},
) {
  const { delaySeconds = 1, enabled = true, maximumSeconds } = options;
  const [status, setStatus] = useState<AutosaveStatus>("Idle");
  const [savedAt, setSavedAt] = useState<Date>();
  const saveRef = useRef(save);
  const settled = useRef<string | undefined>(undefined);
  const oldestPending = useRef<number | undefined>(undefined);

  saveRef.current = save;

  // Compared by serialised value so a caller can pass an object literal without re-saving on every render.
  const serialised = useMemo(() => JSON.stringify(value ?? null), [value]);

  useEffect(() => {
    if (!enabled) return;
    // The value as first seen is the baseline, not an edit.
    if (settled.current === undefined) {
      settled.current = serialised;
      return;
    }
    if (settled.current === serialised) return;

    // A long paragraph typed without pausing would otherwise never reach the debounce. The ceiling makes
    // continuous typing safe too, which is the case somebody actually loses work in.
    const now = Date.now();
    oldestPending.current ??= now;
    const waited = (now - oldestPending.current) / 1000;
    const delay = maximumSeconds !== undefined && waited >= maximumSeconds ? 0 : delaySeconds * 1000;

    const timer = window.setTimeout(() => {
      void (async () => {
        setStatus("Saving");
        try {
          await saveRef.current(value);
          settled.current = serialised;
          oldestPending.current = undefined;
          setStatus("Saved");
          setSavedAt(new Date());
        } catch (reason) {
          // Conflict is not failure — somebody else moved the record, and the person typing needs to know
          // that specifically, because retrying would overwrite them.
          setStatus(reason instanceof AutosaveConflict ? "Conflict" : "Error");
        }
      })();
    }, delay);
    return () => window.clearTimeout(timer);
  }, [delaySeconds, enabled, maximumSeconds, serialised, value]);

  /** Forces a save now, for a deliberate act that must not race the debounce. */
  const flush = useCallback(async () => {
    if (settled.current === serialised) return;
    setStatus("Saving");
    try {
      await saveRef.current(value);
      settled.current = serialised;
      oldestPending.current = undefined;
      setStatus("Saved");
      setSavedAt(new Date());
    } catch (reason) {
      setStatus(reason instanceof AutosaveConflict ? "Conflict" : "Error");
      throw reason;
    }
  }, [serialised, value]);

  /** Marks the current value as settled without saving — after a successful submit, say. */
  const settle = useCallback(() => {
    settled.current = serialised;
    oldestPending.current = undefined;
    setStatus("Idle");
  }, [serialised]);

  return { status, savedAt, flush, settle };
}

/** Thrown by a save when the record moved underneath the draft. */
export class AutosaveConflict extends Error {
  constructor(message = "This record changed after it was opened.") {
    super(message);
    this.name = "AutosaveConflict";
  }
}

export type LocalDraft<T> = {
  status: AutosaveStatus;
  savedAt?: Date;
  /** A draft found on open, waiting for the person to accept or discard it. Never applied silently. */
  offered?: { value: T; savedAt: Date };
  restore: () => void;
  discard: () => void;
  /** Clears the stored draft — call after the record has actually been created. */
  clear: () => void;
};

/**
 * Holds a draft of a not-yet-created record in this browser.
 *
 * The draft found on return is *offered*, not applied. Silently repopulating a form with text from a
 * previous session is its own hazard: somebody who meant to start fresh would not notice they were editing
 * something old, and in a controlled tool that is how a wrong statement reaches a review.
 */
export function useLocalDraft<T>(
  storageKey: string,
  value: T,
  options: { enabled?: boolean; isEmpty?: (value: T) => boolean } = {},
): LocalDraft<T> {
  const { enabled = true, isEmpty } = options;
  const [offered, setOffered] = useState<{ value: T; savedAt: Date }>();
  const [applied, setApplied] = useState(false);

  // Read once, on open. Re-reading would fight the person as they type.
  useEffect(() => {
    if (!enabled) return;
    try {
      const raw = localStorage.getItem(storageKey);
      if (!raw) return;
      const saved = JSON.parse(raw) as { value: T; savedAt: string };
      if (saved?.value === undefined) return;
      setOffered({ value: saved.value, savedAt: new Date(saved.savedAt) });
    } catch {
      // Unreadable draft: nothing to offer, and nothing worth telling anybody about.
      localStorage.removeItem(storageKey);
    }
  }, [enabled, storageKey]);

  const write = useCallback(
    (next: T) => {
      // An untouched form should not leave a draft behind for the next person who opens it.
      if (isEmpty?.(next)) {
        localStorage.removeItem(storageKey);
        return;
      }
      localStorage.setItem(storageKey, JSON.stringify({ value: next, savedAt: new Date().toISOString() }));
    },
    [isEmpty, storageKey],
  );

  // Held until the offer is answered, so accepting or discarding is not immediately overwritten.
  const { status, savedAt } = useDebouncedSave(value, write, {
    enabled: enabled && (applied || offered === undefined),
    maximumSeconds: 10,
  });

  return {
    status,
    savedAt,
    offered: applied ? undefined : offered,
    restore: () => setApplied(true),
    discard: () => {
      localStorage.removeItem(storageKey);
      setOffered(undefined);
      setApplied(true);
    },
    clear: () => {
      localStorage.removeItem(storageKey);
      setOffered(undefined);
      setApplied(true);
    },
  };
}


/**
 * Holds a draft of an uncontrolled form — one read through `FormData` rather than bound to React state.
 *
 * Several creation forms are written that way, and converting each to controlled state purely to protect
 * typing would be a large refactor with its own risk. Reading the form on input covers all of them with one
 * mechanism.
 *
 * Two kinds of field are never written down. Passwords and anything marked `data-no-draft` are skipped, so
 * a half-typed credential or API token is not persisted by a feature nobody asked to store it — and file
 * inputs cannot be restored anyway, so keeping them would only produce a draft that lies about its contents.
 */
export function useFormDraft(
  form: React.RefObject<HTMLFormElement | null>,
  storageKey: string,
  options: { enabled?: boolean } = {},
): LocalDraft<Record<string, string>> & { apply: () => void } {
  const { enabled = true } = options;
  const [values, setValues] = useState<Record<string, string>>({});

  const read = useCallback(() => {
    const element = form.current;
    if (!element) return {};
    const result: Record<string, string> = {};
    for (const field of Array.from(element.elements)) {
      const input = field as HTMLInputElement;
      if (!input.name || input.dataset.noDraft !== undefined) continue;
      if (input.type === "password" || input.type === "file" || input.type === "hidden") continue;
      result[input.name] = input.type === "checkbox" ? String(input.checked) : (input.value ?? "");
    }
    return result;
  }, [form]);

  useEffect(() => {
    const element = form.current;
    if (!element || !enabled) return;
    const onInput = () => setValues(read());
    element.addEventListener("input", onInput);
    element.addEventListener("change", onInput);
    return () => {
      element.removeEventListener("input", onInput);
      element.removeEventListener("change", onInput);
    };
  }, [enabled, form, read]);

  const draft = useLocalDraft(storageKey, values, {
    enabled,
    isEmpty: (value) => Object.values(value).every((x) => !x.trim() || x === "false"),
  });

  /** Writes a restored draft back into the form's fields. */
  const apply = useCallback(() => {
    const saved = draft.offered?.value;
    draft.restore();
    const element = form.current;
    if (!saved || !element) return;
    for (const field of Array.from(element.elements)) {
      const input = field as HTMLInputElement;
      if (!input.name || saved[input.name] === undefined) continue;
      if (input.type === "checkbox") input.checked = saved[input.name] === "true";
      else input.value = saved[input.name];
    }
    setValues(read());
  }, [draft, form, read]);

  return { ...draft, apply };
}
