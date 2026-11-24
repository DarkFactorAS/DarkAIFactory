import { useCallback, useEffect, useRef, useState } from "react";
import { apiFetch, useAuth } from "./auth.jsx";

export const PROJECT_KEY_STORAGE_KEY = "aidashboard.projectKey";

function readStoredProjectKey() {
  try {
    return window.localStorage.getItem(PROJECT_KEY_STORAGE_KEY) || null;
  } catch {
    return null;
  }
}

function storeProjectKey(key) {
  try {
    window.localStorage.setItem(PROJECT_KEY_STORAGE_KEY, key);
  } catch {
    // storage may be blocked; the choice then only lasts for this page
  }
}

const TASK_REFRESH_INTERVAL_MS = 30000;

async function request(url, options) {
  const response = await apiFetch(url, {
    headers: { "Content-Type": "application/json" },
    ...options,
  });
  if (!response.ok) {
    let message = `Request failed (${response.status})`;
    try {
      const body = await response.json();
      if (body.error) message = body.error;
    } catch {
      // keep generic message
    }
    const error = new Error(message);
    error.status = response.status;
    throw error;
  }
  return response.json();
}

function TaskForm({ initial, submitLabel, onSubmit, onCancel, projectKeys = [], defaultProjectKey = null }) {
  const [title, setTitle] = useState(initial?.title ?? "");
  const [description, setDescription] = useState(initial?.description ?? "");
  const [chosenProject, setChosenProject] = useState(readStoredProjectKey);
  const [error, setError] = useState(null);
  const [saving, setSaving] = useState(false);
  const creating = !initial;
  // Fall back to the default when nothing (or a key that has vanished from the list) is chosen.
  const projectKey =
    chosenProject && projectKeys.includes(chosenProject) ? chosenProject : defaultProjectKey ?? projectKeys[0] ?? null;

  async function handleSubmit(event) {
    event.preventDefault();
    setSaving(true);
    setError(null);
    try {
      await onSubmit(creating ? { projectKey, title, description } : { title, description });
      if (!initial) {
        setTitle("");
        setDescription("");
      }
    } catch (e) {
      setError(e.message);
    } finally {
      setSaving(false);
    }
  }

  return (
    <form onSubmit={handleSubmit} className="space-y-2">
      {creating && projectKeys.length > 1 && (
        <select
          aria-label="Project"
          data-testid="task-project-select"
          className="w-full rounded border border-slate-300 dark:border-slate-600 bg-white dark:bg-slate-800 p-2"
          value={projectKey ?? ""}
          onChange={(e) => {
            setChosenProject(e.target.value);
            storeProjectKey(e.target.value);
          }}
        >
          {projectKeys.map((key) => (
            <option key={key} value={key}>
              {key}
            </option>
          ))}
        </select>
      )}
      {creating && projectKeys.length === 1 && (
        <p data-testid="task-project-readonly" className="text-sm text-slate-600 dark:text-slate-300">
          Project: <span className="font-semibold">{projectKey}</span>
        </p>
      )}
      <input
        aria-label="Title"
        className="w-full rounded border border-slate-300 dark:border-slate-600 p-2"
        value={title}
        maxLength={255}
        onChange={(e) => setTitle(e.target.value)}
        required
      />
      <textarea
        aria-label="Description"
        className="w-full rounded border border-slate-300 dark:border-slate-600 p-2"
        rows={4}
        value={description}
        onChange={(e) => setDescription(e.target.value)}
        required
      />
      {error && (
        <p role="alert" className="text-sm text-red-600 dark:text-red-400">
          {error}
        </p>
      )}
      <div className="flex gap-2">
        <button
          type="submit"
          disabled={saving || !title.trim() || !description.trim() || (creating && !projectKey)}
          className="rounded bg-slate-900 px-3 py-1 text-white disabled:opacity-50"
        >
          {submitLabel}
        </button>
        {onCancel && (
          <button type="button" onClick={onCancel} className="rounded border px-3 py-1">
            Cancel
          </button>
        )}
      </div>
    </form>
  );
}

// Center panel: create tasks and edit those not yet sent to Jira (status New).
// Without a login the pending tasks are listed read-only and the create/edit forms are hidden.
export default function TaskEditor() {
  const { canEdit, openLogin } = useAuth();
  const [tasks, setTasks] = useState([]);
  const [error, setError] = useState(null);
  const [editingIdState, setEditingId] = useState(null);
  const editingId = canEdit ? editingIdState : null;
  const [projectKeys, setProjectKeys] = useState([]);
  const [projectKey, setProjectKey] = useState(null);

  const latestRequest = useRef(0);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const load = useCallback(() => {
    const requestId = ++latestRequest.current;
    request("/api/agent-tasks?status=New")
      .then((data) => {
        if (!mounted.current || requestId !== latestRequest.current) return;
        setTasks(data);
        setError(null);
        // Close the edit form if the edited task has left the list (sent to Jira).
        setEditingId((current) =>
          current !== null && !data.some((t) => t.id === current) ? null : current
        );
      })
      .catch((e) => {
        if (!mounted.current || requestId !== latestRequest.current) return;
        setError(e.message);
      });
  }, []);

  useEffect(() => {
    load();
  }, [load]);
  useEffect(() => {
    const timer = setInterval(load, TASK_REFRESH_INTERVAL_MS);
    return () => clearInterval(timer);
  }, [load]);
  useEffect(() => {
    request("/api/tasks/config")
      .then((data) => {
        const keys = Array.isArray(data.projectKeys) ? data.projectKeys : [];
        setProjectKeys(keys);
        setProjectKey(data.projectKey ?? keys[0] ?? null);
      })
      .catch((e) => setError(e.message));
  }, []);

  async function create({ projectKey, title, description }) {
    if (!projectKey) throw new Error("The Jira project configuration is not available.");
    await request("/api/agent-tasks", {
      method: "POST",
      body: JSON.stringify({ projectKey, title, description }),
    });
    load();
  }

  async function update(task, { title, description }) {
    await request(`/api/agent-tasks/${task.id}`, {
      method: "PATCH",
      body: JSON.stringify({ title, description }),
    });
    setEditingId(null);
    load();
  }

  return (
    <section data-testid="center-panel" className="space-y-4 rounded-lg bg-white dark:bg-slate-800 p-4 shadow">
      <h2 className="text-lg font-semibold">New task</h2>
      {canEdit ? (
        <TaskForm
          submitLabel="Create task"
          onSubmit={create}
          projectKeys={projectKeys}
          defaultProjectKey={projectKey}
        />
      ) : (
        <p data-testid="task-login-hint" className="text-sm text-slate-500 dark:text-slate-400">
          <button type="button" onClick={openLogin} className="font-semibold text-sky-700 underline dark:text-sky-400">
            Log in
          </button>{" "}
          to create and edit tasks.
        </p>
      )}
      <h2 className="text-lg font-semibold">Tasks not yet sent to Jira</h2>
      <p className="text-xs text-slate-500 dark:text-slate-400">Updates every 30 s</p>
      {error && (
        <p role="alert" className="text-red-600 dark:text-red-400">
          Unable to load tasks: {error}
        </p>
      )}
      {!error && tasks.length === 0 && <p className="text-slate-500 dark:text-slate-400">No pending tasks.</p>}
      <ul className="space-y-3">
        {tasks.map((task) => (
          <li key={task.id} className="rounded border border-slate-200 dark:border-slate-700 p-3">
            {editingId === task.id ? (
              <TaskForm
                initial={task}
                submitLabel="Save"
                onSubmit={(values) => update(task, values)}
                onCancel={() => setEditingId(null)}
              />
            ) : (
              <>
                <div className="flex items-center justify-between gap-2">
                  <span className="flex items-center gap-2">
                    <span className="font-semibold">{task.title}</span>
                    <span
                      data-testid="task-project-badge"
                      className="rounded bg-slate-100 px-1.5 py-0.5 text-xs text-slate-600 dark:bg-slate-700 dark:text-slate-300"
                    >
                      {task.projectKey}
                    </span>
                  </span>
                  {canEdit && (
                    <button
                      type="button"
                      onClick={() => setEditingId(task.id)}
                      className="rounded border px-2 py-0.5 text-sm"
                    >
                      Edit
                    </button>
                  )}
                </div>
                <p className="mt-1 whitespace-pre-line text-sm text-slate-600 dark:text-slate-300">{task.description}</p>
              </>
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}

export { request };
