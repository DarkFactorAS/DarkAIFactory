import { useEffect, useState } from "react";

// Fetches JSON from the API and exposes loading/error state.
// With options.pollMs the data is re-fetched at that interval; background
// refreshes keep the existing data, and a failed poll keeps the last good data.
export default function useApiData(url, { pollMs } = {}) {
  const [state, setState] = useState({ data: null, loading: true, error: null });

  useEffect(() => {
    let controller = null;
    let cancelled = false;

    const load = (initial) => {
      controller?.abort();
      const current = new AbortController();
      controller = current;
      fetch(url, { signal: current.signal })
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
        });
    };

    setState({ data: null, loading: true, error: null });
    load(true);
    const timer = pollMs ? setInterval(() => load(false), pollMs) : null;

    return () => {
      cancelled = true;
      if (timer) clearInterval(timer);
      controller?.abort();
    };
  }, [url, pollMs]);

  return state;
}
