import { useCallback, useEffect, useRef, useState } from "react";
import { readRoute } from "./routing";
import type { AppRoute } from "./routing";

/** History and in-page selection share one typed route; hydration never rewrites it. */
export function useWorkspaceRoute() {
  const [route, setRoute] = useState<AppRoute>(readRoute);
  const currentRoute = useRef(route);
  currentRoute.current = route;
  const isCurrent = useCallback(() => currentRoute.current === route, [route]);
  const update = useCallback(<K extends keyof AppRoute>(key: K, value: AppRoute[K]) => {
    if (!isCurrent()) return;
    setRoute(current => current[key] === value ? current : { ...current, [key]: value });
  }, [isCurrent]);
  const writeHistory = useCallback((mode: "pushState" | "replaceState", path: string | URL) => {
    if (!isCurrent()) return;
    history[mode]({}, "", path);
    const next = readRoute();
    setRoute(current => JSON.stringify(current) === JSON.stringify(next) ? current : next);
  }, [isCurrent]);
  useEffect(() => {
    // Local worklist paging is owned by its workspace. Do not replace an unchanged shell
    // route while that workspace's own popstate handler is restoring the list.
    const restore = () => {
      const next = readRoute();
      setRoute(current => JSON.stringify(current) === JSON.stringify(next) ? current : next);
    };
    addEventListener("popstate", restore);
    return () => removeEventListener("popstate", restore);
  }, []);
  return { route, update, writeHistory, isCurrent };
}
