import { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";

// Fired when any API call returns 401 (session expired or logged out elsewhere).
export const UNAUTHORIZED_EVENT = "aidashboard:unauthorized";

// fetch() for the dashboard API. The session is an HttpOnly cookie the browser sends
// automatically; a 401 outside /api/auth/* sends the user back to the login page.
export async function apiFetch(url, options = {}) {
  const response = await fetch(url, { credentials: "same-origin", ...options });
  if (response.status === 401 && !String(url).startsWith("/api/auth/") && typeof window !== "undefined") {
    window.dispatchEvent(new Event(UNAUTHORIZED_EVENT));
  }
  return response;
}

async function errorMessage(response, fallback) {
  try {
    const body = await response.json();
    if (body?.error) return body.error;
  } catch {
    // keep fallback
  }
  return fallback;
}

const AuthContext = createContext({ status: "loading", username: null, login: async () => {}, logout: async () => {} });

// status: "loading" (checking the session), "anonymous" or "authenticated".
export function AuthProvider({ children }) {
  const [state, setState] = useState({ status: "loading", username: null });

  useEffect(() => {
    let cancelled = false;
    fetch("/api/auth/me", { credentials: "same-origin" })
      .then(async (response) => {
        if (cancelled) return;
        if (response.ok) {
          const body = await response.json();
          setState({ status: "authenticated", username: body.username ?? null });
        } else {
          setState({ status: "anonymous", username: null });
        }
      })
      .catch(() => {
        if (!cancelled) setState({ status: "anonymous", username: null });
      });
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    const onUnauthorized = () => setState({ status: "anonymous", username: null });
    window.addEventListener(UNAUTHORIZED_EVENT, onUnauthorized);
    return () => window.removeEventListener(UNAUTHORIZED_EVENT, onUnauthorized);
  }, []);

  // Throws an Error with a user-facing message when the login is refused.
  const login = useCallback(async (username, password) => {
    const response = await fetch("/api/auth/login", {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username, password }),
    });
    if (!response.ok) {
      throw new Error(await errorMessage(response, `Login failed (${response.status})`));
    }
    const body = await response.json();
    setState({ status: "authenticated", username: body.username ?? username });
  }, []);

  const logout = useCallback(async () => {
    try {
      await fetch("/api/auth/logout", { method: "POST", credentials: "same-origin" });
    } finally {
      setState({ status: "anonymous", username: null });
    }
  }, []);

  const value = useMemo(() => ({ ...state, login, logout }), [state, login, logout]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export const useAuth = () => useContext(AuthContext);
