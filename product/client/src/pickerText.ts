/**
 * Truthful picker metadata for the bounded authoring pickers (#402).
 *
 * The server paging contract keeps totalCount as the number of records matching the current search, and
 * exact selected items may be additionally hydrated into items even when they do not match that search.
 * The UI must therefore never describe totalCount as "the whole universe" once a search is active, and a
 * retained hydrated selection must never be presented as a search match.
 *
 * The caller supplies its own current-selection count. totalCount is a cross-page search total while
 * items.length is only the current page plus any hydrated rows, so the two cannot be compared to infer
 * hydration. The explicit selection count is what drives the retained-selection note, without guessing
 * whether any particular selected item happens to match the search.
 */
export type PickerSummary = { headline: string; note?: string }

export function pickerSummary(
  noun: string,
  query: string,
  totalCount: number,
  selectedCount: number,
  location = 'in this build',
): PickerSummary {
  if (!query.trim()) {
    return { headline: `${totalCount} ${noun}${totalCount === 1 ? '' : 's'} ${location}.` }
  }
  const headline = `${totalCount} matching ${noun}${totalCount === 1 ? '' : 's'}.`
  if (selectedCount <= 0) return { headline }
  return {
    headline,
    note: selectedCount === 1
      ? 'Current selection is kept visible independently of the search.'
      : `${selectedCount} current selections are kept visible independently of the search.`,
  }
}
