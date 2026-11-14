import { useEffect, useState } from "react";
import { getActiveStatuses, saveActiveStatuses } from "./agentApi.js";
import { useWriteToken, WriteTokenPrompt } from "./WriteToken.jsx";

export function parseStatuses(text) {
  const seen = new Set();
  return text
    .split(",")
    .map((s) => s.trim())
    .filter((s) => s && !seen.has(s.toLowerCase()) && seen.add(s.toLowerCase()));
}

// Inline editor for settings Dashboard.ActiveStatuses (comma-separated input).
export default function ActiveStatusesEditor({ onSaved, onCancel }) {
  const { token, setToken } = useWriteToken();
  const [text, setText] = useState("");
  const [loaded, setLoaded] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(null);

  useEffect(() => {
    getActiveStatuses()
      .then((data) => {
        setText((data.statuses ?? []).join(", "));
        setLoaded(true);
      })
      .catch((e) => setError(e.message));
  }, []);

  async function save() {
    setBusy(true);
    setError(null);
    try {
      await saveActiveStatuses(token, parseStatuses(text));
      onSaved();
    } catch (e) {
      if (e.status === 401) setToken("");
      setError(e.message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="mb-4 rounded border border-slate-200 dark:border-slate-700 p-3" data-testid="active-statuses-editor">
      <label className="mb-1 block text-sm font-semibold" htmlFor="active-statuses-input">
        Statuses to show (comma-separated)
      </label>
      <input
        id="active-statuses-input"
        value={text}
        disabled={!loaded || busy}
        onChange={(e) => setText(e.target.value)}
        className="w-full rounded border border-slate-300 dark:border-slate-600 p-1 text-sm"
      />
      <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
        Jira status names, e.g. In Progress, In Review. An empty list shows all non-Done statuses.
      </p>
      {error && (
        <p role="alert" className="text-sm text-red-600 dark:text-red-400">
          {error}
        </p>
      )}
      <div className="mt-2 flex flex-wrap items-center gap-2">
        {!token && <WriteTokenPrompt />}
        <button
          type="button"
          onClick={save}
          disabled={!token || !loaded || busy}
          className="rounded border border-slate-300 dark:border-slate-600 px-2 py-1 text-sm disabled:opacity-50"
        >
          Save
        </button>
        <button type="button" onClick={onCancel} className="rounded border border-slate-300 dark:border-slate-600 px-2 py-1 text-sm">
          Cancel
        </button>
      </div>
    </div>
  );
}
