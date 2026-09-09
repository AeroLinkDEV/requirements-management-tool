import { useCallback, useEffect, useRef } from "react";

/** Each async state stream accepts only its newest request, and none after cleanup. */
export function useLatestRequest() {
  const generation = useRef(0);
  const invalidate = useCallback(() => { generation.current++; }, []);
  const begin = useCallback(() => {
    const issued = ++generation.current;
    return () => issued === generation.current;
  }, []);
  useEffect(() => invalidate, [invalidate]);
  return { begin, invalidate };
}
