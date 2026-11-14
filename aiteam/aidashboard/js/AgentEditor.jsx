import { useEffect, useRef, useState } from "react";
import { CAPABILITIES, PROVIDERS, createAgent, listProjects, updateAgent } from "./agentApi.js";
import { WriteTokenPrompt, useWriteToken } from "./WriteToken.jsx";

// Create/edit form for an agent definition. `agent` is null in create mode.
export default function AgentEditor({ agent, onSaved, onCancel }) {
  const { token, setToken } = useWriteToken();
  const [name, setName] = useState(agent?.name ?? "");
  const [role, setRole] = useState(agent?.role ?? "");
  const [behavior, setBehavior] = useState(agent?.behavior ?? "");
  const [provider, setProvider] = useState(agent?.llmProvider ?? PROVIDERS[0]);
  const [model, setModel] = useState(agent?.llmModel ?? "");
  const [capabilities, setCapabilities] = useState(
    () => new Set((agent?.capabilities ?? []).filter((c) => c.enabled).map((c) => c.capability)),
  );
  const [projects, setProjects] = useState(() => new Set(agent?.projects ?? []));
  const [projectOptions, setProjectOptions] = useState([]);
  const [error, setError] = useState(null);
  const [saving, setSaving] = useState(false);
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
    if (inFlight.current || !token) return;
    inFlight.current = true;
    setSaving(true);
    setError(null);
    const body = {
      name,
      role,
      behavior,
      llmProvider: provider,
      llmModel: model.trim() || null,
      capabilities: CAPABILITIES.map((c) => ({ capability: c, enabled: capabilities.has(c) })),
      projects: [...projects],
    };
    try {
      if (agent) {
        await updateAgent(token, agent.id, body);
      } else {
        await createAgent(token, body);
      }
      onSaved();
    } catch (e) {
      if (e.status === 401) setToken("");
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
      className="space-y-2 rounded border border-slate-200 dark:border-slate-700 p-3"
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
      {!token && (
        <div className="text-sm">
          <p className="text-slate-600 dark:text-slate-300">Enter the write token to save.</p>
          <WriteTokenPrompt />
        </div>
      )}
      {error && (
        <p role="alert" className="text-sm text-red-600 dark:text-red-400">
          {error}
        </p>
      )}
      <div className="flex gap-2">
        <button
          type="submit"
          disabled={saving || !token}
          className="rounded bg-slate-900 px-3 py-1 text-sm text-white disabled:opacity-50"
        >
          Save
        </button>
        <button type="button" onClick={onCancel} className="rounded border px-3 py-1 text-sm">
          Cancel
        </button>
      </div>
    </form>
  );
}
