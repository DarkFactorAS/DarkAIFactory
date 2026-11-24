import { useCallback, useEffect, useRef, useState } from "react";
import AgentEditor from "./AgentEditor.jsx";
import AgentMetrics from "./AgentMetrics.jsx";
import { useAuth } from "./auth.jsx";
import { POLL_INTERVAL_MS, activeCapability, compareByContainer, isWorking, listAgentMetrics, listAgents, statusLabel } from "./agentApi.js";

function statusBadgeColors(label) {
  if (label === "Disabled") return "bg-yellow-100 text-yellow-800 dark:bg-yellow-900 dark:text-yellow-200";
  if (label === "Offline") return "bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-200";
  return "bg-slate-100 dark:bg-slate-700";
}

function AgentCard({ agent, metrics, nowMs, onEdit, canEdit }) {
  const label = statusLabel(agent, nowMs);
  const active = activeCapability(agent, nowMs);
  const working = isWorking(agent, nowMs);
  const chips = agent.capabilities.filter((c) => c.enabled).map((c) => c.capability);
  if (active && !chips.some((c) => c.toLowerCase() === active.toLowerCase())) chips.push(active);

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
        <span className={"rounded px-2 py-0.5 text-xs " + statusBadgeColors(label)} data-testid="agent-status">
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
      <AgentMetrics metrics={metrics} />
      {canEdit && (
        <div className="mt-2 flex flex-wrap items-center gap-2">
          <button type="button" onClick={() => onEdit(agent)} className="rounded border px-2 py-0.5 text-xs">
            Edit
          </button>
        </div>
      )}
    </li>
  );
}

// Modal overlay around the agent editor. A native <dialog> opened with showModal()
// renders in the top layer, makes the page inert, traps focus and restores focus
// to the opener when closed.
function AgentEditorDialog({ agent, onCancel, children }) {
  const ref = useRef(null);
  const titleId = "agent-editor-title";

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return undefined;
    if (!dialog.open) dialog.showModal();
    dialog.querySelector("form input")?.focus();
    return () => {
      if (dialog.open) dialog.close();
    };
  }, []);

  return (
    <dialog
      ref={ref}
      data-testid="agent-editor-dialog"
      aria-labelledby={titleId}
      onCancel={(e) => {
        e.preventDefault();
        onCancel();
      }}
      onClick={(e) => {
        if (e.target === ref.current) onCancel();
      }}
      className="m-auto w-full max-w-2xl max-h-[90vh] overflow-y-auto rounded-lg bg-white p-0 text-inherit shadow-xl backdrop:bg-black/30 dark:bg-slate-800 dark:text-slate-100"
    >
      <div className="p-4">
        <div className="mb-3 flex items-center justify-between gap-2">
          <h2 id={titleId} className="text-lg font-semibold">
            {agent ? `Edit ${agent.name}` : "Create agent"}
          </h2>
          <button type="button" aria-label="Close agent editor" onClick={onCancel} className="rounded border px-2 py-0.5 text-sm">
            ×
          </button>
        </div>
        {children}
      </div>
    </dialog>
  );
}

// Agents are visible to everyone; adding and editing them needs a login.
export default function AgentSlots() {
  const { canEdit } = useAuth();
  const [state, setState] = useState(() => ({ agents: null, metrics: {}, nowMs: Date.now(), fetchedAt: Date.now(), error: null }));
  const [editorState, setEditor] = useState(null); // null | { agent }
  const editor = canEdit ? editorState : null;
  const [showEdit, setShowEdit] = useState(false);

  // Reset on logout so the Edit buttons do not return automatically after the next login.
  useEffect(() => {
    if (!canEdit) setShowEdit(false);
  }, [canEdit]);

  const load = useCallback(() => {
    // Metrics are optional: a failure leaves the last known values and never blocks the agents.
    // Merge them in their own continuation so a slow metrics query cannot gate the agent cards.
    listAgentMetrics()
      .then((metrics) => {
        if (metrics) setState((prev) => ({ ...prev, metrics }));
      })
      .catch(() => {});
    listAgents()
      .then(({ agents, nowMs }) => {
        setState((prev) => ({
          ...prev,
          agents,
          nowMs,
          fetchedAt: Date.now(),
          error: null,
        }));
      })
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

  const { agents, metrics, nowMs, error } = state;
  return (
    <section data-testid="agent-slots" className="space-y-3 rounded-lg bg-white dark:bg-slate-800 p-4 shadow">
      <div className="flex items-center justify-between gap-2">
        <h2 className="text-lg font-semibold">Agents</h2>
        {canEdit && (
          <button
            type="button"
            data-testid="agent-edit-toggle"
            aria-pressed={showEdit}
            title={showEdit ? "Hide edit buttons" : "Show edit buttons"}
            onClick={() => setShowEdit((v) => !v)}
            className={"rounded border px-2 py-0.5 text-xs" + (showEdit ? " bg-slate-200 dark:bg-slate-600" : "")}
          >
            Edit
            <span className="sr-only">{showEdit ? " (hide edit buttons)" : " (show edit buttons)"}</span>
          </button>
        )}
      </div>
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
      {agents && (
        <ul className="flex flex-col gap-3">
          {[...agents].sort(compareByContainer).map((agent) => (
            <AgentCard
              key={agent.id}
              agent={agent}
              metrics={metrics[agent.id]}
              nowMs={nowMs}
              onEdit={(a) => setEditor({ agent: a })}
              canEdit={canEdit && showEdit}
            />
          ))}
          {canEdit && (
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
          )}
        </ul>
      )}
      {editor && (
        <AgentEditorDialog agent={editor.agent} onCancel={() => setEditor(null)}>
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
        </AgentEditorDialog>
      )}
    </section>
  );
}
