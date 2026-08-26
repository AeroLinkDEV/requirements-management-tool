import { useEffect, useId, useMemo, useRef, useState } from "react";
import { useCategoryVocabulary, type CategoryDefinition } from "./problemReportCategories";
import "./ProblemReportCategoryPicker.css";

/**
 * Choosing what kind of problem this is.
 *
 * Deliberately not a plain `<select>` of nine names. The distinction that matters — a code defect with
 * functional impact against one without, a test problem that blocks the testing against one that does not —
 * is not visible in the labels alone, and a chooser that hides the meaning gets the categories that sound
 * closest rather than the ones that are right. So the meaning sits next to the choice, grouped by family.
 *
 * The vocabulary is fetched rather than written out here: nine meanings spelled twice is nine chances for
 * the picker to explain a category differently from the record that carries it.
 */
const familyClass = (code: string) => `catFamily${code.slice(0, 1)}`;

/** The tile used by the picker, the record and the queue, so a colour and a code always mean one thing. */
export function CategoryTile({ definition, provenance, compact }:
  { definition: CategoryDefinition; provenance?: string; compact?: boolean }) {
  return (
    <span className={`catTile ${familyClass(definition.code)}${compact ? " compact" : ""}`}>
      <b>{definition.code}</b>
      <span>
        <em>{definition.label}</em>
        {!compact && <small>{definition.meaning}</small>}
      </span>
      {provenance === "MigrationDerived" && (
        <i
          className="catDerived"
          title="Assigned by the 2026-08 category migration from the retired kind. Not chosen by a person — open the report and choose one to confirm it."
        >
          derived
        </i>
      )}
    </span>
  );
}

export default function ProblemReportCategoryPicker({ api, value, disabled, required, onChange }: {
  api: string;
  value?: string;
  disabled?: boolean;
  required?: boolean;
  onChange: (value: string) => void;
}) {
  const definitions = useCategoryVocabulary(api);
  const [open, setOpen] = useState(false);
  const [filter, setFilter] = useState("");
  const [active, setActive] = useState(0);
  const listId = useId();
  const container = useRef<HTMLDivElement>(null);

  const selected = definitions.find(definition => definition.value === value);

  // Matching the meaning as well as the label is what makes "code" surface 61 Data / Configuration, whose
  // whole point is that it is *not* the application code.
  const matches = useMemo(() => {
    const needle = filter.trim().toLowerCase();
    if (!needle) return definitions;
    return definitions.filter(definition =>
      definition.code.includes(needle)
      || definition.label.toLowerCase().includes(needle)
      || definition.meaning.toLowerCase().includes(needle));
  }, [definitions, filter]);

  const grouped = useMemo(() => {
    const families: { family: string; items: CategoryDefinition[] }[] = [];
    for (const definition of matches) {
      const existing = families.find(group => group.family === definition.family);
      if (existing) existing.items.push(definition);
      else families.push({ family: definition.family, items: [definition] });
    }
    return families;
  }, [matches]);

  useEffect(() => { setActive(0) }, [filter, open]);
  useEffect(() => {
    if (!open) return;
    const dismiss = (event: MouseEvent) => {
      if (!container.current?.contains(event.target as Node)) setOpen(false);
    };
    addEventListener("mousedown", dismiss);
    return () => removeEventListener("mousedown", dismiss);
  }, [open]);

  const choose = (definition: CategoryDefinition) => {
    onChange(definition.value);
    setOpen(false);
    setFilter("");
  };

  const onKeyDown = (event: React.KeyboardEvent) => {
    if (!open) return;
    if (event.key === "ArrowDown") { event.preventDefault(); setActive(index => Math.min(index + 1, matches.length - 1)) }
    else if (event.key === "ArrowUp") { event.preventDefault(); setActive(index => Math.max(index - 1, 0)) }
    else if (event.key === "Enter") { event.preventDefault(); if (matches[active]) choose(matches[active]) }
    else if (event.key === "Escape") { setOpen(false) }
  };

  if (!definitions.length)
    return <p className="catUnavailable">The category vocabulary could not be loaded.</p>;

  return (
    <div className="catPicker" ref={container} onKeyDown={onKeyDown}>
      <button
        type="button"
        className="catCurrent"
        disabled={disabled}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={open ? listId : undefined}
        onClick={() => setOpen(current => !current)}
      >
        {selected
          ? <CategoryTile definition={selected} compact />
          : <span className="catNone">{required ? "Choose a category — required before Ready for SCCB" : "No category chosen"}</span>}
        <i aria-hidden="true">▾</i>
      </button>

      {open && (
        <div className="catMenu">
          <input
            autoFocus
            className="catFilter"
            value={filter}
            placeholder="Type to filter by code, name or meaning…"
            aria-label="Filter categories"
            onChange={event => setFilter(event.target.value)}
          />
          <div className="catOptions" role="listbox" id={listId} aria-activedescendant={matches[active] ? `${listId}-${matches[active].value}` : undefined}>
            {grouped.map(group => (
              <div key={group.family} className="catGroup">
                <p className="catGroupLabel">{group.family}</p>
                {group.items.map(definition => (
                  <button
                    type="button"
                    key={definition.value}
                    id={`${listId}-${definition.value}`}
                    role="option"
                    aria-selected={definition.value === value}
                    className={`catOption${matches.indexOf(definition) === active ? " active" : ""}${definition.value === value ? " selected" : ""}`}
                    onMouseEnter={() => setActive(matches.indexOf(definition))}
                    onClick={() => choose(definition)}
                  >
                    <CategoryTile definition={definition} />
                  </button>
                ))}
              </div>
            ))}
            {!matches.length && <p className="catEmpty">No category matches “{filter.trim()}”.</p>}
          </div>
        </div>
      )}
    </div>
  );
}
