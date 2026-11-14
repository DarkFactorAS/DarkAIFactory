import { createContext, useContext, useMemo, useState } from "react";

const WriteTokenContext = createContext({ token: "", setToken: () => {} });

// Keeps the write token in memory only for the lifetime of the page.
export function WriteTokenProvider({ children }) {
  const [token, setToken] = useState("");
  const value = useMemo(() => ({ token, setToken }), [token]);
  return <WriteTokenContext.Provider value={value}>{children}</WriteTokenContext.Provider>;
}

export const useWriteToken = () => useContext(WriteTokenContext);

export function WriteTokenPrompt() {
  const { token, setToken } = useWriteToken();
  // Keep typing in a local draft so the shared token (and the parents' `!token`
  // guards) only change once the user explicitly confirms the full value.
  const [draft, setDraft] = useState("");
  if (token) return null;

  const commit = () => {
    const value = draft.trim();
    if (value) setToken(value);
  };

  return (
    <span className="inline-flex items-center gap-1">
      <input
        type="password"
        aria-label="Dashboard write token"
        placeholder="Dashboard write token"
        autoComplete="off"
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Enter") {
            e.preventDefault();
            commit();
          }
        }}
        className="rounded border border-slate-300 dark:border-slate-600 p-1 text-sm"
      />
      <button
        type="button"
        onClick={commit}
        disabled={!draft.trim()}
        className="rounded border border-slate-300 dark:border-slate-600 px-2 py-1 text-sm disabled:opacity-50"
      >
        Use token
      </button>
    </span>
  );
}
