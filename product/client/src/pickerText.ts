/**
 * Truthful picker metadata for the bounded authoring pickers (#402).
 *
 * The server paging contract keeps totalCount as the number of records matching the current search, and
 * exact selected items may be additionally hydrated into items even when they do not match that search.
 * The UI must therefore never describe totalCount as "the whole universe" once a search is active, and a
 * retained hydrated selection must never be presented as a search match.
 */
export type PickerSummary = { headline: string; note?: string }

export function pickerSummary(
  noun: string,
  query: string,
  totalCount: number,
  itemsLength: number,
  location = 'in this build',
): PickerSummary {
  if (!query.trim()) {
    return { headline: `${totalCount} ${noun}${totalCount === 1 ? '' : 's'} ${location}.` }
  }
  const headline = `${totalCount} matching ${noun}${totalCount === 1 ? '' : 's'}.`
  const retained = Math.max(0, itemsLength - totalCount)
  if (retained <= 0) return { headline }
  return {
    headline,
    note: `Showing ${itemsLength} option${itemsLength === 1 ? '' : 's'}, including the current selection${retained > 1 ? 's' : ''}.`,
  }
}
