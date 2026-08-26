import { useEffect, useState } from "react";

/**
 * The category vocabulary, fetched rather than written out in the browser.
 *
 * Nine meanings spelled twice is nine chances for the picker to explain a category differently from the
 * record that carries it, so the server is the single source and this only caches what it said.
 *
 * Split out of ProblemReportCategoryPicker for the same reason richContentModel is split out of
 * RichContent: a module that exports a hook alongside its components breaks fast refresh.
 */
export type CategoryDefinition = {
  value: string;
  code: string;
  family: string;
  label: string;
  meaning: string;
};

/** What a record carries: the definition, plus how the value got there. */
export type SelectedCategory = CategoryDefinition & { provenance?: string };

export function useCategoryVocabulary(api: string) {
  const [definitions, setDefinitions] = useState<CategoryDefinition[]>([]);
  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch(`${api}/api/problem-reports/categories`);
        if (!response.ok) return;
        const body = (await response.json()) as { categories?: CategoryDefinition[] };
        if (live && Array.isArray(body.categories)) setDefinitions(body.categories);
      } catch {
        // A picker with no vocabulary renders as unavailable rather than as an empty list of choices.
      }
    })();
    return () => { live = false };
  }, [api]);
  return definitions;
}
