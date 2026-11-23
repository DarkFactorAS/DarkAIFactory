import { useEffect, useState } from "react";
import { getCardLabels, saveCardLabels } from "./agentApi.js";
import { parseLabels } from "./AiLabelsEditor.jsx";

// Inline editor for settings Dashboard.CardLabels (comma-separated input).
export default function CardLabelsEditor({ onSaved, onCancel }) {
  const [text, setText] = useState("");
  const [loaded, setLoaded] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(null);

  useEffect(() => {
    getCardLabels()
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
      await saveCardLabels(parseLabels(text));
      onSaved();
    } catch (e) {
      setError(e.message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="mb-4 rounded border border-slate-200 dark:border-slate-700 p-3" data-testid="card-labels-editor">
      <label className="mb-1 block text-sm font-semibold" htmlFor="card-labels-input">
        Labels to show on cards (comma-separated)
      </label>
      <input
        id="card-labels-input"
        value={text}
        disabled={!loaded || busy}
        onChange={(e) => setText(e.target.value)}
        className="w-full rounded border border-slate-300 dark:border-slate-600 p-1 text-sm"
      />
      <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
        Jira labels shown as badges on the task cards. An empty list shows the defaults: ai-approved, aiagent-needs-clarification, aiagent-ready-to-merge.
      </p>
      {error && (
        <p role="alert" className="text-sm text-red-600 dark:text-red-400">
          {error}
        </p>
      )}
      <div className="mt-2 flex flex-wrap items-center gap-2">
        <button
          type="button"
          onClick={save}
          disabled={!loaded || busy}
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
