import { useCallback, useEffect, useState } from "react";
import AgentEditor from "./AgentEditor.jsx";
import { POLL_INTERVAL_MS, activeCapability, isWorking, listAgents, setAgentEnabled, statusLabel } from "./agentApi.js";
import { WriteTokenPrompt, useWriteToken } from "./WriteToken.jsx";

function AgentCard({ agent, nowMs, onEdit, onChanged, onError }) {
  const { token, setToken } = useWriteToken();
  const [busy, setBusy] = useState(false);
  const label = statusLabel(agent, nowMs);
  const active = activeCapability(agent, nowMs);
  const working = isWorking(agent, nowMs);
  const chips = agent.capabilities.filter((c) => c.enabled).map((c) => c.capability);
  if (active && !chips.some((c) => c.toLowerCase() === active.toLowerCase())) chips.push(active);

  async function run(action) {
    if (busy || !token) return;
    setBusy(true);
    onError(null);
    try {
      await action(token);
      onChanged();
    } catch (e) {
      if (e.status === 401) setToken("");
      onError(e.message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <li
      data-testid="agent-card"
      data-working={working ? "true" : "false"}
      className={
        "rounded border-2 p-3 text-sm " +
        (working ? "border-blue-600 ring-2 ring-blue-300" : "border-slate-200 dark:border-slate-700")
      }
    >
      <div className="flex items-center justify-between gap-2">
        <span className="font-semibold">{agent.name}</span>
        <span className="rounded bg-slate-100 dark:bg-slate-700 px-2 py-0.5 text-xs" data-testid="agent-status">
          {label}
        </span>
      </div>
      <p className="text-slate-600 dark:text-slate-300">{agent.role}</p>
      <p className="text-xs text-slate-500 dark:text-slate-400">
        {agent.llmProvider} / {agent.llmModel || "default"}
        {agent.container ? ` / ${agent.container}` : ""}
      </p>
      <div className="mt-1 flex flex-wrap gap-1">
        {chips.map((c) =>
          active && c.toLowerCase() === active.toLowerCase() ? (
            <span
              key={c}
              aria-current="step"
              data-testid="agent-active-capability"
              title="Currently running"
              className="rounded bg-blue-600 px-2 py-0.5 text-xs font-semibold text-white ring-2 ring-blue-300"
            >
              {c}
              <span className="sr-only"> (active)</span>
            </span>
          ) : (
            <span key={c} className="rounded bg-blue-100 dark:bg-blue-900 px-2 py-0.5 text-xs">
              {c}
            </span>
          ),
        )}
      </div>
      <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">Projects: {agent.projects.join(", ") || "none"}</p>
      <div className="mt-2 flex flex-wrap items-center gap-2">
        <label className="flex items-center gap-1 text-xs">
          <input
            type="checkbox"
            checked={agent.enabled}
            disabled={busy || !token}
            onChange={(e) => run((t) => setAgentEnabled(t, agent.id, e.target.checked))}
          />
          Enabled
        </label>
        <button type="button" onClick={() => onEdit(agent)} className="rounded border px-2 py-0.5 text-xs">
          Edit
        </button>
      </div>
    </li>
  );
}

export default function AgentSlots() {
  const { token } = useWriteToken();
  const [state, setState] = useState(() => ({ agents: null, nowMs: Date.now(), fetchedAt: Date.now(), error: null }));
  const [editor, setEditor] = useState(null); // null | { agent }
  const [actionError, setActionError] = useState(null);

  const load = useCallback(() => {
    listAgents()
      .then(({ agents, nowMs }) => setState({ agents, nowMs, fetchedAt: Date.now(), error: null }))
      .catch((e) =>
        setState((prev) => {
          // Keep cached agents, but advance the server-derived clock by the
          // local time elapsed so stale heartbeats eventually show as Offline.
          const localNow = Date.now();
          return {
            ...prev,
            nowMs: prev.nowMs + Math.max(0, localNow - prev.fetchedAt),
            fetchedAt: localNow,
            error: e.message,
          };
        }),
      );
  }, []);

  useEffect(() => {
    load();
    const timer = setInterval(load, POLL_INTERVAL_MS);
    return () => clearInterval(timer);
  }, [load]);

  const { agents, nowMs, error } = state;
  return (
    <section data-testid="agent-slots" className="space-y-3 rounded-lg bg-white dark:bg-slate-800 p-4 shadow">
      <h2 className="text-lg font-semibold">Agents</h2>
      {!agents && !error && (
        <p role="status" className="text-slate-500 dark:text-slate-400">
          Loading...
        </p>
      )}
      {error && (
        <p role="alert" className="text-red-600 dark:text-red-400">
          Unable to load agents: {error}
        </p>
      )}
      {actionError && (
        <p role="alert" className="text-sm text-red-600 dark:text-red-400">
          {actionError}
        </p>
      )}
      {!token && (
        <div className="text-sm">
          <p className="text-slate-600 dark:text-slate-300">Enter the write token to change agents.</p>
          <WriteTokenPrompt />
        </div>
      )}
      {agents && (
        <ul className="grid grid-cols-1 gap-3 md:grid-cols-2">
          {agents.map((agent) => (
            <AgentCard
              key={agent.id}
              agent={agent}
              nowMs={nowMs}
              onEdit={(a) => setEditor({ agent: a })}
              onChanged={load}
              onError={setActionError}
            />
          ))}
          <li>
            <button
              type="button"
              aria-label="Add agent"
              onClick={() => setEditor({ agent: null })}
              className="flex h-full min-h-24 w-full items-center justify-center rounded border-2 border-dashed border-slate-300 dark:border-slate-600 text-3xl text-slate-400 dark:text-slate-500"
            >
              +
            </button>
          </li>
        </ul>
      )}
      {editor && (
        <AgentEditor
          key={editor.agent?.id ?? "new"}
          agent={editor.agent}
          onCancel={() => setEditor(null)}
          onSaved={() => {
            setEditor(null);
            load();
          }}
          onDeleted={() => {
            // Only close the editor if it is still the selection that started
            // the delete; the user may have opened another draft meanwhile.
            const deletingEditor = editor;
            setEditor((current) => (current === deletingEditor ? null : current));
            load();
          }}
        />
      )}
    </section>
  );
}
