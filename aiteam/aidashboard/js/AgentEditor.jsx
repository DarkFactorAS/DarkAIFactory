import { useEffect, useRef, useState } from "react";
import { CAPABILITIES, CONTAINERS, PROVIDERS, createAgent, deleteAgent, listProjects, updateAgent } from "./agentApi.js";

// Create/edit form for an agent definition. `agent` is null in create mode.
export default function AgentEditor({ agent, onSaved, onCancel, onDeleted }) {
  const [name, setName] = useState(agent?.name ?? "");
  const [role, setRole] = useState(agent?.role ?? "");
  const [behavior, setBehavior] = useState(agent?.behavior ?? "");
  const [provider, setProvider] = useState(agent?.llmProvider ?? PROVIDERS[0]);
  const [model, setModel] = useState(agent?.llmModel ?? "");
  const [container, setContainer] = useState(agent?.container ?? "");
  const [enabled, setEnabled] = useState(agent?.enabled ?? false);
  const [capabilities, setCapabilities] = useState(
    () => new Set((agent?.capabilities ?? []).filter((c) => c.enabled).map((c) => c.capability)),
  );
  const [projects, setProjects] = useState(() => new Set(agent?.projects ?? []));
  const [projectOptions, setProjectOptions] = useState([]);
  const [error, setError] = useState(null);
  const [saving, setSaving] = useState(false);
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const inFlight = useRef(false);

  useEffect(() => {
    listProjects()
      .then(setProjectOptions)
      .catch((e) => setError(e.message));
  }, []);

  function toggle(set, setSet, value) {
    const next = new Set(set);
    if (next.has(value)) next.delete(value);
    else next.add(value);
    setSet(next);
  }

  async function handleSubmit(event) {
    event.preventDefault();
    if (inFlight.current) return;
    inFlight.current = true;
    setSaving(true);
    setError(null);
    const body = {
      name,
      role,
      behavior,
      llmProvider: provider,
      llmModel: model.trim() || null,
      container: container || null,
      enabled,
      capabilities: CAPABILITIES.map((c) => ({ capability: c, enabled: capabilities.has(c) })),
      projects: [...projects],
    };
    try {
      if (agent) {
        await updateAgent(agent.id, body);
      } else {
        await createAgent(body);
      }
      onSaved();
    } catch (e) {
      setError(e.message);
    } finally {
      inFlight.current = false;
      setSaving(false);
    }
  }

  async function handleDelete() {
    if (inFlight.current || !agent) return;
    inFlight.current = true;
    setSaving(true);
    setError(null);
    try {
      await deleteAgent(agent.id);
      onDeleted();
    } catch (e) {
      setError(e.message);
    } finally {
      inFlight.current = false;
      setSaving(false);
    }
  }

  const label = "block text-sm font-medium";
  const field = "w-full rounded border border-slate-300 dark:border-slate-600 p-1 text-sm";
  return (
    <form
      onSubmit={handleSubmit}
      aria-label={agent ? `Edit ${agent.name}` : "Create agent"}
      className="space-y-2"
    >
      <label className={label}>
        Name
        <input className={field} value={name} maxLength={255} required onChange={(e) => setName(e.target.value)} />
      </label>
      <label className={label}>
        Role
        <input className={field} value={role} required onChange={(e) => setRole(e.target.value)} />
      </label>
      <label className={label}>
        Behavior
        <textarea
          className={field}
          rows={5}
          maxLength={8000}
          value={behavior}
          required
          onChange={(e) => setBehavior(e.target.value)}
        />
      </label>
      <div className="flex gap-2">
        <label className={label}>
          Provider
          <select className={field} value={provider} onChange={(e) => setProvider(e.target.value)}>
            {PROVIDERS.map((p) => (
              <option key={p}>{p}</option>
            ))}
          </select>
        </label>
        <label className={`${label} grow`}>
          Model (empty = default)
          <input className={field} value={model} maxLength={255} onChange={(e) => setModel(e.target.value)} />
        </label>
      </div>
      <label className={label}>
        Container
        <select className={field} value={container} onChange={(e) => setContainer(e.target.value)}>
          <option value="">(none)</option>
          {CONTAINERS.map((c) => (
            <option key={c}>{c}</option>
          ))}
        </select>
      </label>
      <label className="flex items-center gap-1 text-sm font-medium">
        <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
        Enabled
      </label>
      <fieldset>
        <legend className={label}>Capabilities</legend>
        <div className="flex flex-wrap gap-3 text-sm">
          {CAPABILITIES.map((c) => (
            <label key={c} className="flex items-center gap-1">
              <input
                type="checkbox"
                checked={capabilities.has(c)}
                onChange={() => toggle(capabilities, setCapabilities, c)}
              />
              {c}
            </label>
          ))}
        </div>
      </fieldset>
      <fieldset>
        <legend className={label}>Projects</legend>
        <div className="flex flex-wrap gap-3 text-sm">
          {projectOptions.map((key) => (
            <label key={key} className="flex items-center gap-1">
              <input type="checkbox" checked={projects.has(key)} onChange={() => toggle(projects, setProjects, key)} />
              {key}
            </label>
          ))}
          {projectOptions.length === 0 && <span className="text-slate-500 dark:text-slate-400">No projects available.</span>}
        </div>
      </fieldset>
      {error && (
        <p role="alert" className="text-sm text-red-600 dark:text-red-400">
          {error}
        </p>
      )}
      <div className="flex gap-2">
        <button
          type="submit"
          disabled={saving}
          className="rounded bg-slate-900 px-3 py-1 text-sm text-white disabled:opacity-50"
        >
          Save
        </button>
        <button type="button" onClick={onCancel} className="rounded border px-3 py-1 text-sm">
          Cancel
        </button>
        {agent && (
          <button
            type="button"
            disabled={saving}
            onClick={() => setConfirmingDelete(true)}
            className="rounded border px-3 py-1 text-sm text-red-700 dark:text-red-400 disabled:opacity-50"
          >
            Delete
          </button>
        )}
      </div>
      {agent && confirmingDelete && (
        <div role="alertdialog" aria-label={`Delete ${agent.name}`} className="mt-2 rounded bg-red-50 dark:bg-red-950 p-2 text-xs">
          <p>Delete {agent.name}?</p>
          <div className="mt-1 flex gap-2">
            <button
              type="button"
              disabled={saving}
              onClick={handleDelete}
              className="rounded bg-red-700 px-2 py-0.5 text-white disabled:opacity-50"
            >
              Confirm
            </button>
            <button type="button" onClick={() => setConfirmingDelete(false)} className="rounded border px-2 py-0.5">
              Cancel
            </button>
          </div>
        </div>
      )}
    </form>
  );
}
