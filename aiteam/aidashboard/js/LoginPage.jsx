import { useState } from "react";
import { useAuth } from "./auth.jsx";
import ThemeToggle from "./ThemeToggle.jsx";

// Sign-in with a DarkFactor account (checked server-side by the account server, as in DFWeb).
// Opened from the header "Log in" button, or automatically when a write fails because the session
// expired. "Back to dashboard" returns to the read-only dashboard.
export default function LoginPage({ theme, onThemeChange }) {
  const { login, closeLogin, loginReason } = useAuth();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [rememberMe, setRememberMe] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(null);

  async function handleSubmit(event) {
    event.preventDefault();
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      await login(username.trim(), password, rememberMe);
    } catch (e) {
      setError(e.message);
      setPassword("");
      setBusy(false);
    }
  }

  return (
    <div className="flex min-h-screen flex-col bg-slate-50 dark:bg-slate-950 text-slate-900 dark:text-slate-100">
      <header className="flex flex-col items-center gap-2 bg-slate-900 px-6 py-4 shadow lg:flex-row lg:justify-between">
        <h1 className="text-2xl font-bold text-white">DarkAIFactory - AI Team workflow</h1>
        <ThemeToggle theme={theme} onChange={onThemeChange} />
      </header>
      <main className="flex flex-1 items-start justify-center px-4 py-16">
        <form
          onSubmit={handleSubmit}
          aria-labelledby="login-heading"
          data-testid="login-form"
          className="w-full max-w-sm space-y-4 rounded-lg bg-white dark:bg-slate-800 p-6 shadow"
        >
          <div>
            <h2 id="login-heading" className="text-lg font-semibold">
              Sign in
            </h2>
            <p className="text-sm text-slate-500 dark:text-slate-400">Use your DarkFactor account to make changes.</p>
            {loginReason === "expired" && (
              <p role="status" data-testid="login-expired" className="mt-2 text-sm text-amber-700 dark:text-amber-300">
                Your session has expired. Log in again to save changes.
              </p>
            )}
          </div>
          <div>
            <label htmlFor="login-username" className="mb-1 block text-sm font-semibold">
              Username
            </label>
            <input
              id="login-username"
              name="username"
              type="text"
              autoComplete="username"
              autoFocus
              required
              maxLength={255}
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              className="w-full rounded border border-slate-300 dark:border-slate-600 dark:bg-slate-900 p-2 text-sm"
            />
          </div>
          <div>
            <label htmlFor="login-password" className="mb-1 block text-sm font-semibold">
              Password
            </label>
            <input
              id="login-password"
              name="password"
              type="password"
              autoComplete="current-password"
              required
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              className="w-full rounded border border-slate-300 dark:border-slate-600 dark:bg-slate-900 p-2 text-sm"
            />
          </div>
          <div className="flex items-center gap-2">
            <input
              id="login-remember"
              name="rememberMe"
              type="checkbox"
              data-testid="login-remember"
              checked={rememberMe}
              onChange={(e) => setRememberMe(e.target.checked)}
              className="h-4 w-4 rounded border-slate-300 dark:border-slate-600 dark:bg-slate-900"
            />
            <label htmlFor="login-remember" className="text-sm text-slate-700 dark:text-slate-300">
              Remember me for 7 days
            </label>
          </div>
          {error && (
            <p role="alert" className="text-sm text-red-600 dark:text-red-400">
              {error}
            </p>
          )}
          <button
            type="submit"
            disabled={busy || !username.trim() || !password}
            className="w-full rounded bg-slate-900 dark:bg-sky-700 px-3 py-2 text-sm font-semibold text-white disabled:opacity-50"
          >
            {busy ? "Signing in..." : "Sign in"}
          </button>
          <button
            type="button"
            onClick={closeLogin}
            className="w-full rounded border border-slate-300 dark:border-slate-600 px-3 py-2 text-sm"
          >
            Back to dashboard
          </button>
        </form>
      </main>
    </div>
  );
}
