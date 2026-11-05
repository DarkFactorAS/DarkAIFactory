import { useState } from "react";
import useApiData from "./useApiData.js";
import TaskEditor from "./TaskEditor.jsx";
import AddTextPanel from "./AddTextPanel.jsx";

function Panel({ title, state, emptyText, children }) {
  const { data, loading, error } = state;
  return (
    <section className="rounded-lg bg-white p-4 shadow">
      <h2 className="mb-3 text-lg font-semibold">{title}</h2>
      {loading && <p role="status" className="text-slate-500">Loading...</p>}
      {error && (
        <p role="alert" className="text-red-600">
          Unable to load data: {error}
        </p>
      )}
      {data && data.length === 0 && <p className="text-slate-500">{emptyText}</p>}
      {data && data.length > 0 && children(data)}
    </section>
  );
}

function SolvedByProjectPanel() {
  const state = useApiData("/api/stats/solved-by-project");
  return (
    <Panel title="Tasks solved per project" state={state} emptyText="No solved tasks yet.">
      {(items) => (
        <ul className="divide-y divide-slate-200">
          {items.map((item) => (
            <li key={item.project} className="flex justify-between py-2">
              <span className="font-medium">{item.project}</span>
              <span className="text-slate-600">{item.solvedCount}</span>
            </li>
          ))}
        </ul>
      )}
    </Panel>
  );
}

function ActiveAiTasksPanel() {
  const [openKey, setOpenKey] = useState(null);
  const state = useApiData("/api/tasks/active-ai");
  return (
    <Panel title="Top 3 AI tasks" state={state} emptyText="No active AI tasks.">
      {(items) => (
        <ul className="space-y-3">
          {items.map((task) => (
            <li key={task.key} className="rounded border border-slate-200 p-3">
              <button
                type="button"
                aria-expanded={openKey === task.key}
                onClick={() => setOpenKey(openKey === task.key ? null : task.key)}
                className="block w-full text-left"
              >
              <div className="flex items-center justify-between gap-2">
                <span className="font-semibold">{task.key}</span>
                <span className="rounded bg-slate-100 px-2 py-0.5 text-xs">{task.status}</span>
              </div>
              <p className="mt-1 text-sm">{task.summary}</p>
              {task.description && (
                <p className="mt-1 line-clamp-3 whitespace-pre-line text-xs text-slate-500">
                  {task.description}
                </p>
              )}
              </button>
              {openKey === task.key && <AddTextPanel task={task} onClose={() => setOpenKey(null)} />}
            </li>
          ))}
        </ul>
      )}
    </Panel>
  );
}

export default function App() {
  return (
    <div className="min-h-screen bg-slate-50 text-slate-900">
      <header className="bg-slate-900 px-6 py-4 shadow">
        <h1 className="text-2xl font-bold text-white">
          DarkAIFactoy - AI Team workflow
        </h1>
      </header>
      <main className="grid grid-cols-1 gap-6 p-6 lg:grid-cols-[1fr_2fr_1fr]">
        <SolvedByProjectPanel />
        <TaskEditor />
        <ActiveAiTasksPanel />
      </main>
    </div>
  );
}
