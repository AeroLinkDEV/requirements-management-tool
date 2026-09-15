/**
 * The verification-profile rules the Create New Project walkthrough displays.
 *
 * These are pure functions over the *saved* answer. They exist as a module rather than as component-local
 * helpers so the exact compatibility rules can be asserted directly, and so display, remembering and repair
 * all answer from one interpretation instead of three similar ones.
 *
 * The stored value is the creator's own content and is never rewritten here: an absent profile, an empty
 * list, a valid profile and an invalid one stay four different facts, and only a deliberate supported
 * repair replaces a saved answer.
 */

export type VerificationKind = "Case" | "Procedure";

/** The minimal step shape these helpers read. */
export type VerificationProfileStep = {
  catalogueEntry: string;
  capabilities: number;
  /** The persisted profile exactly as the server stored it. `undefined` means the draft carried none. */
  enabledArtifactKinds?: unknown;
};

/** The maintained interpretation of a level without its own profile, as the server reports it. */
export type VerificationReadiness = { effective: string[]; profileSource: string };

export const verificationCapabilityMask = 2;

export function hasVerificationCapability(step: { capabilities: number }) {
  return (step.capabilities & verificationCapabilityMask) !== 0;
}

/** A profile the server could not read as a list at all. Kept verbatim so a save cannot rewrite it. */
export function artifactProfileIsMalformed(step: VerificationProfileStep): boolean {
  return step.enabledArtifactKinds !== undefined && !Array.isArray(step.enabledArtifactKinds);
}

/** The raw saved list exactly as stored, or undefined when the draft carried no profile at all. */
export function rawProfileEntries(step: VerificationProfileStep): unknown[] | undefined {
  return Array.isArray(step.enabledArtifactKinds) ? step.enabledArtifactKinds : undefined;
}

/** The profiles this level supports, in the exact order the server accepts them. */
export function supportedVerificationProfiles(catalogueEntry: string): VerificationKind[][] {
  return catalogueEntry === "System" ? [["Procedure"]] : [["Case"], ["Case", "Procedure"]];
}

/**
 * Whether the raw saved list is exactly one of this level's supported profiles: element types, length and
 * order all have to match. A filtered or reordered approximation is not a supported profile.
 */
export function rawProfileIsExactSupportedProfile(
  step: VerificationProfileStep,
  catalogueEntry: string = step.catalogueEntry,
): boolean {
  const raw = rawProfileEntries(step);
  if (!raw) return false;
  return supportedVerificationProfiles(catalogueEntry).some(
    (candidate) => candidate.length === raw.length && candidate.every((kind, index) => raw[index] === kind),
  );
}

/**
 * The saved profile, but only when it is exactly one of the profiles this level supports. Re-enabling
 * verification may restore an actual prior choice; it must never derive a compatible-looking choice by
 * filtering, sorting or shortening an invalid one.
 */
export function compatibleRememberedProfile(
  step: VerificationProfileStep,
  catalogueEntry: string = step.catalogueEntry,
): VerificationKind[] | undefined {
  return rawProfileIsExactSupportedProfile(step, catalogueEntry)
    ? (rawProfileEntries(step) as VerificationKind[])
    : undefined;
}

/** The software-profile control's value, which is blank unless the raw answer is exactly one profile. */
export function profileSelection(step: VerificationProfileStep): "" | "Case" | "Case+Procedure" {
  const raw = rawProfileEntries(step);
  if (!raw) return "";
  if (raw.length === 1 && raw[0] === "Case") return "Case";
  if (raw.length === 2 && raw[0] === "Case" && raw[1] === "Procedure") return "Case+Procedure";
  return "";
}

/**
 * The text tokens the draft stores, or undefined when the draft carries no list at all. Non-text entries are
 * reported through {@link profileHasNonTextEntries} instead of being dropped here.
 */
export function savedArtifactTokens(step: VerificationProfileStep): string[] | undefined {
  const raw = rawProfileEntries(step);
  return raw === undefined ? undefined : raw.filter((kind): kind is string => typeof kind === "string");
}

/** Tokens the draft carries that this build cannot interpret. They are shown, never silently dropped. */
export function unrecognizedArtifactTokens(step: VerificationProfileStep): string[] {
  return (savedArtifactTokens(step) ?? []).filter((kind) => kind !== "Case" && kind !== "Procedure");
}

/** A list carrying values that are not artifact kinds at all — a different problem from an unknown name. */
export function profileHasNonTextEntries(step: VerificationProfileStep): boolean {
  return (rawProfileEntries(step) ?? []).some((entry) => typeof entry !== "string");
}

/** Duplicated or reordered known kinds: stored content the server refuses but that is not "unrecognized". */
export function profileShapeIsUnsupported(step: VerificationProfileStep): boolean {
  if (artifactProfileIsMalformed(step)) return false;
  const raw = rawProfileEntries(step);
  if (!raw) return false;
  return !rawProfileIsExactSupportedProfile(step);
}

/** Why an enabled level's saved answer is not a profile this level can finalize with. */
export function invalidProfileReason(step: VerificationProfileStep): string {
  if (artifactProfileIsMalformed(step)) return "is not a list of artifact kinds";
  const raw = rawProfileEntries(step) ?? [];
  if (raw.length === 0) return "selects no artifacts";
  if (profileHasNonTextEntries(step)) return "contains values that are not artifact kinds";
  if (unrecognizedArtifactTokens(step).length > 0)
    return `contains artifact kinds this version does not recognize (${boundedTokenList(
      unrecognizedArtifactTokens(step),
    )})`;
  return "is not one of this level's supported profiles, which each level accepts as one exact ordered list";
}

/** The contradiction the final gate refuses: verification is off while the profile still enables artifacts. */
export function disabledVerificationWithArtifacts(step: VerificationProfileStep): boolean {
  return !hasVerificationCapability(step) && (rawProfileEntries(step)?.length ?? 0) > 0;
}

/**
 * Verification is on, but the saved answer is empty, unreadable, or not exactly one of this level's
 * supported profiles.
 */
export function enabledVerificationProfileInvalid(step: VerificationProfileStep): boolean {
  if (!hasVerificationCapability(step)) return false;
  if (artifactProfileIsMalformed(step)) return true;
  const raw = rawProfileEntries(step);
  if (!raw) return false;
  return !rawProfileIsExactSupportedProfile(step);
}

/** Diagnostic tokens are bounded before they are shown; the stored value itself is never altered. */
export const displayTokenLimit = 40;
export const displayTokenCount = 3;

export function boundedTokenList(tokens: string[]): string {
  const shown = tokens
    .slice(0, displayTokenCount)
    .map((token) => (token.length > displayTokenLimit ? `${token.slice(0, displayTokenLimit)}…` : token));
  const remaining = tokens.length - shown.length;
  return `${shown.join(", ")}${remaining > 0 ? ` and ${remaining} more` : ""}`;
}

/** What the draft stores for this level, said without implying the stored value is usable. */
export function savedArtifactsLabel(step: VerificationProfileStep): string {
  if (artifactProfileIsMalformed(step)) return "not a list of artifact kinds (kept as saved)";
  const raw = rawProfileEntries(step);
  if (!raw) return "none recorded";
  if (raw.length === 0) return "none selected";
  if (profileHasNonTextEntries(step))
    return `a list with entries that are not artifact kinds (${raw.length} saved, kept as stored)`;
  return boundedTokenList(raw as string[]);
}

/** The maintained interpretation of the saved answer, which is a different fact from the saved answer. */
export function effectiveArtifactsLabel(
  step: VerificationProfileStep,
  verdict?: VerificationReadiness,
): string {
  if (!hasVerificationCapability(step))
    return (rawProfileEntries(step)?.length ?? 0) > 0
      ? "no verification artifacts (the capability is disabled)"
      : "none";
  const effective = verdict?.effective ?? compatibleRememberedProfile(step);
  const label = effective && effective.length ? effective.join(" + ") : "none yet";
  return verdict?.profileSource === "catalogue-fallback"
    ? `${label} (maintained default for an unspecified saved profile)`
    : label;
}

/**
 * What a level verifies, said plainly — including a stored contradiction that has not been repaired yet.
 * The selected capability, the saved artifacts and their effective interpretation are separate facts, and a
 * level whose profile is invalid is never described as if it had a valid one.
 */
export function verificationSummary(
  step: VerificationProfileStep,
  verdict?: VerificationReadiness,
): string {
  const raw = rawProfileEntries(step);
  if (!hasVerificationCapability(step)) {
    if (raw && raw.length)
      return `Verification disabled — but the saved profile still enables ${savedArtifactsLabel(
        step,
      )}. This contradiction must be repaired before the Project can be created.`;
    if (artifactProfileIsMalformed(step))
      return "Verification disabled — the saved profile is not a list of artifact kinds and cannot be kept as a valid choice.";
    return "Verification disabled — no verification artifacts are enabled.";
  }
  const effective = verdict?.effective ?? compatibleRememberedProfile(step);
  const stated = effective && effective.length ? effective.join(" + ") : "no artifacts selected yet";
  const notes: string[] = [];
  if (verdict?.profileSource === "catalogue-fallback")
    notes.push("maintained default for an unspecified saved profile");
  if (unrecognizedArtifactTokens(step).length > 0)
    notes.push(`unrecognized saved tokens: ${boundedTokenList(unrecognizedArtifactTokens(step))}`);
  if (artifactProfileIsMalformed(step)) notes.push("the saved profile is not a list of artifact kinds");
  else if (profileHasNonTextEntries(step)) notes.push("the saved list holds entries that are not artifact kinds");
  else if (profileShapeIsUnsupported(step))
    notes.push("the saved list is not one of this level's supported profiles");
  if (raw !== undefined && raw.length === 0 && notes.length === 0)
    notes.push("incomplete configuration, not a default");
  return `Verification enabled · ${stated}${notes.length ? ` (${notes.join("; ")})` : ""}`;
}
