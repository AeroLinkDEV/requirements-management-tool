import type { ReactNode } from "react"
import "./DigitalThreadTable.css"

/** The two representations of one Digital Thread view. */
export type ThreadRepresentation = "map" | "table"

/** A typed cell renderer keeps the table frame shared without flattening view-specific domain facts. */
export type DigitalThreadTableColumn<Row extends DigitalThreadTableRow> = {
  key: string
  label: string
  render: (row: Row) => ReactNode
}

/** Every view supplies its authoritative identity and the cells belonging to that view. */
export type DigitalThreadTableRow = {
  id: string
  label: string
}

export type DigitalThreadTableProps<Row extends DigitalThreadTableRow> = {
  ariaLabel: string
  caption: string
  columns: readonly DigitalThreadTableColumn<Row>[]
  rows: readonly Row[]
  /** Count before the active view's filter/search predicate, for an honest no-match state. */
  availableCount: number
  selectedId?: string | null
  onSelect?: (id: string | null) => void
  loading?: boolean
  error?: string | null
  onRetry?: () => void
  emptyMessage: string
  selectionMessage?: string
  truncatedMessage?: string | null
  /** A view may retain its opened record while reporting that no row genuinely matched its search. */
  noMatch?: boolean
  noMatchMessage?: string
  /** Space reserved by the active view's inspector, so table rows remain reachable beside the panel. */
  reservedInset?: { right?: number; left?: number; bottom?: number }
}

/**
 * The accessible representation for one Digital Thread view.
 *
 * This component owns table mechanics only. Rows, relation wording, hop meaning, exact links and state
 * labels stay with the Network, Inside and Artifact projections that already own those facts. Selection gets
 * a separate native control from every identity link so keyboard activation cannot navigate and select twice.
 */
export default function DigitalThreadTable<Row extends DigitalThreadTableRow>({
  ariaLabel,
  caption,
  columns,
  rows,
  availableCount,
  selectedId = null,
  onSelect,
  loading = false,
  error = null,
  onRetry,
  emptyMessage,
  selectionMessage,
  truncatedMessage,
  noMatch = false,
  noMatchMessage = "No records match. Clear a filter chip or the search box to bring records back.",
  reservedInset,
}: DigitalThreadTableProps<Row>) {
  const showNoMatch = !loading && !error && availableCount > 0 && (rows.length === 0 || noMatch)

  return (
    <section
      className="dtThreadTable"
      aria-label={ariaLabel}
      style={{
        paddingRight: 18 + (reservedInset?.right ?? 0),
        paddingLeft: 18 + (reservedInset?.left ?? 0),
        paddingBottom: 18 + (reservedInset?.bottom ?? 0),
      }}
    >
      {selectionMessage && !selectedId ? (
        <p className="dtThreadTableSelection" role="status">{selectionMessage}</p>
      ) : null}
      {truncatedMessage ? (
        <p className="dtThreadTableTruncated" role="status">{truncatedMessage}</p>
      ) : null}
      {loading ? (
        <p className="dtThreadTableState" role="status">Loading this Digital Thread table…</p>
      ) : null}
      {error ? (
        <div className="dtThreadTableState dtThreadTableState-error" role="alert">
          <b>This Digital Thread table could not be loaded.</b>
          <p>{error}</p>
          {onRetry ? <button type="button" onClick={onRetry}>Try again</button> : null}
        </div>
      ) : null}
      {showNoMatch ? (
        <p className="dtThreadTableState" role="status">
        {noMatchMessage}
        </p>
      ) : null}
      {!loading && !error && !showNoMatch && !rows.length ? (
        <p className="dtThreadTableState" role="status">{emptyMessage}</p>
      ) : null}

      {rows.length ? (
        <div className="dtThreadTableScroll">
          <table>
            <caption>{caption}</caption>
            <thead>
              <tr>
                <th scope="col" className="dtThreadTableSelectHead">Select</th>
                {columns.map(column => <th scope="col" key={column.key}>{column.label}</th>)}
              </tr>
            </thead>
            <tbody>
              {rows.map(row => {
                const selected = row.id === selectedId
                return (
                  <tr key={row.id} aria-selected={selected} className={selected ? "is-selected" : ""}>
                    <td className="dtThreadTableSelectCell">
                      <button
                        type="button"
                        className="dtThreadTableSelect"
                        aria-label={`${selected ? "Selected" : "Select"} ${row.label}`}
                        aria-pressed={selected}
                        onClick={() => onSelect?.(selected ? null : row.id)}
                      >
                        {selected ? "Selected" : "Select"}
                      </button>
                    </td>
                    {columns.map(column => <td key={column.key}>{column.render(row)}</td>)}
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      ) : null}
    </section>
  )
}
