import { useCallback, useEffect, useState } from "react";
import AgentEditor from "./AgentEditor.jsx";
import { POLL_INTERVAL_MS, activeCapability, deleteAgent, isWorking, listAgents, setAgentEnabled, statusLabel } from "./agentApi.js";
import { WriteTokenPrompt, useWriteToken } from "./WriteToken.jsx";

function AgentCard({ agent, nowMs, onEdit, onChanged, onError }) {
  const { token, setToken } = useWriteToken();
  const [confirming, setConfirming] = useState(false);
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
      setConfirming(false);
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
        (working ? "border-blue-600 ring-2 ring-blue-300" : "border-slate-200")
      }
    >
      <div className="flex items-center justify-between gap-2">
        <span className="font-semibold">{agent.name}</span>
        <span className="rounded bg-slate-100 px-2 py-0.5 text-xs" data-testid="agent-status">
          {label}
        </span>
      </div>
      <p className="text-slate-600">{agent.role}</p>
      <p className="text-xs text-slate-500">
        {agent.llmProvider} / {agent.llmModel || "default"}
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
            <span key={c} className="rounded bg-blue-100 px-2 py-0.5 text-xs">
              {c}
            </span>
          ),
        )}
      </div>
      <p className="mt-1 text-xs text-slate-500">Projects: {agent.projects.join(", ") || "none"}</p>
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
        <button
          type="button"
          onClick={() => setConfirming(true)}
          className="rounded border px-2 py-0.5 text-xs text-red-700"
        >
          Delete
        </button>
      </div>
      {confirming && (
        <div role="alertdialog" aria-label={`Delete ${agent.name}`} className="mt-2 rounded bg-red-50 p-2 text-xs">
          <p>Delete {agent.name}?</p>
          <div className="mt-1 flex gap-2">
            <button
              type="button"
              disabled={busy || !token}
              onClick={() => run((t) => deleteAgent(t, agent.id))}
              className="rounded bg-red-700 px-2 py-0.5 text-white disabled:opacity-50"
            >
              Confirm
            </button>
            <button type="button" onClick={() => setConfirming(false)} className="rounded border px-2 py-0.5">
              Cancel
            </button>
          </div>
        </div>
      )}
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
    <section data-testid="agent-slots" className="space-y-3 rounded-lg bg-white p-4 shadow">
      <h2 className="text-lg font-semibold">Agents</h2>
      {!agents && !error && (
        <p role="status" className="text-slate-500">
          Loading...
        </p>
      )}
      {error && (
        <p role="alert" className="text-red-600">
          Unable to load agents: {error}
        </p>
      )}
      {actionError && (
        <p role="alert" className="text-sm text-red-600">
          {actionError}
        </p>
      )}
      {!token && (
        <div className="text-sm">
          <p className="text-slate-600">Enter the write token to change agents.</p>
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
              className="flex h-full min-h-24 w-full items-center justify-center rounded border-2 border-dashed border-slate-300 text-3xl text-slate-400"
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
        />
      )}
    </section>
  );
}
