import { useEffect, useState } from "react";
import { getAiLabels, saveAiLabels } from "./agentApi.js";
import { useWriteToken, WriteTokenPrompt } from "./WriteToken.jsx";

export function parseLabels(text) {
  return [
    ...new Set(
      text
        .split(/[,\s]+/)
        .map((label) => label.trim())
        .filter(Boolean),
    ),
  ];
}

// Inline editor for settings Dashboard.AiLabels (comma-separated input).
export default function AiLabelsEditor({ onSaved, onCancel }) {
  const { token, setToken } = useWriteToken();
  const [text, setText] = useState("");
  const [loaded, setLoaded] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(null);

  useEffect(() => {
    getAiLabels()
      .then((data) => {
        setText((data.labels ?? []).join(", "));
        setLoaded(true);
      })
      .catch((e) => setError(e.message));
  }, []);

  async function save() {
    setBusy(true);
    setError(null);
    try {
      await saveAiLabels(token, parseLabels(text));
      onSaved();
    } catch (e) {
      if (e.status === 401) setToken("");
      setError(e.message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="mb-4 rounded border border-slate-200 dark:border-slate-700 p-3" data-testid="ai-labels-editor">
      <label className="mb-1 block text-sm font-semibold" htmlFor="ai-labels-input">
        AI labels (comma-separated)
      </label>
      <input
        id="ai-labels-input"
        value={text}
        disabled={!loaded || busy}
        onChange={(e) => setText(e.target.value)}
        className="w-full rounded border border-slate-300 dark:border-slate-600 p-1 text-sm"
      />
      <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
        An empty list uses the workflow pickup/analyzed labels. These labels also drive the Top 3 AI tasks panel.
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
