import { useCallback, useEffect, useRef, useState } from "react";

const TASK_REFRESH_INTERVAL_MS = 30000;

async function request(url, options) {
  const response = await fetch(url, {
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

function TaskForm({ initial, submitLabel, onSubmit, onCancel }) {
  const [title, setTitle] = useState(initial?.title ?? "");
  const [description, setDescription] = useState(initial?.description ?? "");
  const [error, setError] = useState(null);
  const [saving, setSaving] = useState(false);

  async function handleSubmit(event) {
    event.preventDefault();
    setSaving(true);
    setError(null);
    try {
      await onSubmit({ title, description });
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
          disabled={saving || !title.trim() || !description.trim()}
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
export default function TaskEditor() {
  const [tasks, setTasks] = useState([]);
  const [error, setError] = useState(null);
  const [editingId, setEditingId] = useState(null);
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
      .then((data) => setProjectKey(data.projectKey))
      .catch((e) => setError(e.message));
  }, []);

  async function create({ title, description }) {
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
      <TaskForm submitLabel="Create task" onSubmit={create} />
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
                  <span className="font-semibold">{task.title}</span>
                  <button
                    type="button"
                    onClick={() => setEditingId(task.id)}
                    className="rounded border px-2 py-0.5 text-sm"
                  >
                    Edit
                  </button>
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
