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
  if (token) return null;
  return (
    <input
      type="password"
      aria-label="Dashboard write token"
      placeholder="Dashboard write token"
      autoComplete="off"
      value={token}
      onChange={(e) => setToken(e.target.value)}
      className="rounded border border-slate-300 p-1 text-sm"
    />
  );
}
