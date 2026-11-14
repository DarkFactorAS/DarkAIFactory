import { useEffect, useState } from "react";

// Fetches JSON from the API and exposes loading/error state.
// With options.pollMs the data is re-fetched at that interval; background
// refreshes keep the existing data, and a failed poll keeps the last good data.
export default function useApiData(url, { pollMs } = {}) {
  const [state, setState] = useState({ data: null, loading: true, error: null });

  useEffect(() => {
    const controller = new AbortController();
    let cancelled = false;
    let inFlight = false;

    const load = (initial) => {
      // Skip a poll tick while the previous request is still running, so slow
      // responses can complete instead of being aborted by the next tick.
      if (inFlight) return;
      inFlight = true;
      fetch(url, { signal: controller.signal })
        .then((response) => {
          if (!response.ok) {
            throw new Error(`Request failed (${response.status})`);
          }
          return response.json();
        })
        .then((data) => {
          if (!cancelled) setState({ data, loading: false, error: null });
        })
        .catch((error) => {
          if (cancelled || error.name === "AbortError") return;
          setState((prev) => ({ data: initial ? null : prev.data, loading: false, error: error.message }));
        })
        .finally(() => {
          inFlight = false;
        });
    };

    setState({ data: null, loading: true, error: null });
    load(true);
    const timer = pollMs ? setInterval(() => load(false), pollMs) : null;

    return () => {
      cancelled = true;
      if (timer) clearInterval(timer);
      controller.abort();
    };
  }, [url, pollMs]);

  return state;
}
