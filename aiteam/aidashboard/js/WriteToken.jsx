import { createContext, useCallback, useContext, useMemo, useState } from "react";

const WriteTokenContext = createContext({ token: "", setToken: () => {} });

const COOKIE_NAME = "dashboard_write_token";
const COOKIE_MAX_AGE_SECONDS = 30 * 24 * 60 * 60;

function readTokenCookie() {
  if (typeof document === "undefined") return "";
  for (const part of document.cookie.split(";")) {
    const trimmed = part.trim();
    if (trimmed.startsWith(COOKIE_NAME + "=")) {
      try {
        return decodeURIComponent(trimmed.slice(COOKIE_NAME.length + 1));
      } catch {
        return "";
      }
    }
  }
  return "";
}

function cookieAttributes() {
  const secure = typeof location !== "undefined" && location.protocol === "https:" ? "; Secure" : "";
  return "; path=/; SameSite=Strict" + secure;
}

function writeTokenCookie(value) {
  document.cookie =
    `${COOKIE_NAME}=${encodeURIComponent(value)}; max-age=${COOKIE_MAX_AGE_SECONDS}` + cookieAttributes();
}

function clearTokenCookie() {
  document.cookie = `${COOKIE_NAME}=; max-age=0` + cookieAttributes();
}

// Remembers the write token in a client-side cookie (30 days, SameSite=Strict,
// Secure on HTTPS). Setting an empty token (e.g. after a 401) clears the cookie.
export function WriteTokenProvider({ children }) {
  const [token, setTokenState] = useState(readTokenCookie);
  const setToken = useCallback((value) => {
    if (value) writeTokenCookie(value);
    else clearTokenCookie();
    setTokenState(value || "");
  }, []);
  const value = useMemo(() => ({ token, setToken }), [token, setToken]);
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
