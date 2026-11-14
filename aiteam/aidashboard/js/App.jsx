import { Bar, BarChart, Cell, Legend, Pie, PieChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { useState } from "react";
import useApiData from "./useApiData.js";
import TaskEditor from "./TaskEditor.jsx";
import AddTextPanel from "./AddTextPanel.jsx";
import AgentSlots from "./AgentSlots.jsx";
import { WriteTokenProvider } from "./WriteToken.jsx";

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

const PROJECT_COLORS = ["#2563eb", "#f59e0b", "#10b981", "#ef4444", "#8b5cf6", "#06b6d4", "#ec4899", "#84cc16"];
const AI_COLORS = { ai: "#8b5cf6", nonAi: "#94a3b8" };

export function formatPercent(value, total) {
  return total > 0 ? Math.round((value / total) * 100) : 0;
}

export function legendLabel(name, value, total) {
  return `${name} (${value}, ${formatPercent(value, total)}%)`;
}

function PieWithLegend({ title, slices, testId }) {
  const total = slices.reduce((sum, slice) => sum + slice.value, 0);
  const visible = slices.filter((slice) => slice.value > 0);
  return (
    <div data-testid={testId} className="mb-4">
      <h3 className="mb-1 text-sm font-semibold">{title}</h3>
      {total === 0 ? (
        <p className="text-sm text-slate-500">No solved tasks.</p>
      ) : (
        <>
          <ResponsiveContainer width="100%" height={160}>
            <PieChart>
              <Pie data={visible} dataKey="value" nameKey="name" outerRadius={70}>
                {visible.map((slice) => (
                  <Cell key={slice.name} fill={slice.color} />
                ))}
              </Pie>
              <Tooltip />
            </PieChart>
          </ResponsiveContainer>
          <ul className="flex flex-wrap gap-x-4 gap-y-1 text-sm">
            {slices.map((slice) => (
              <li key={slice.name} className="flex items-center gap-1">
                <span className="inline-block h-3 w-3 rounded-sm" style={{ backgroundColor: slice.color }} />
                {legendLabel(slice.name, slice.value, total)}
              </li>
            ))}
          </ul>
        </>
      )}
    </div>
  );
}

function AiShareBarChart({ items }) {
  const data = items.map((item) => {
    const aiCount = item.aiCount ?? 0;
    const nonAiCount = item.nonAiCount ?? 0;
    const total = aiCount + nonAiCount;
    return {
      project: item.project,
      aiCount,
      nonAiCount,
      aiPercent: formatPercent(aiCount, total),
      nonAiPercent: total > 0 ? 100 - formatPercent(aiCount, total) : 0,
    };
  });
  const hasData = data.some((row) => row.aiCount + row.nonAiCount > 0);
  const tooltipFormatter = (value, name, entry) => {
    const row = entry.payload;
    const count = name === "% AI" ? row.aiCount : row.nonAiCount;
    return [`${value}% (${count})`, name];
  };
  return (
    <div data-testid="solved-ai-chart" className="mb-4">
      <h3 className="mb-1 text-sm font-semibold">AI vs non-AI per project</h3>
      {hasData && (
        <ul className="mb-2 text-sm">
          {data.map((row) => (
            <li key={row.project}>
              {row.project}: AI ({row.aiCount}, {row.aiPercent}%); non-AI ({row.nonAiCount}, {row.nonAiPercent}%).
            </li>
          ))}
        </ul>
      )}
      {!hasData ? (
        <p className="text-sm text-slate-500">No solved tasks.</p>
      ) : (
        <ResponsiveContainer width="100%" height={260}>
          <BarChart data={data}>
            <XAxis dataKey="project" />
            <YAxis domain={[0, 100]} tickFormatter={(value) => `${value}%`} />
            <Tooltip formatter={tooltipFormatter} />
            <Legend />
            <Bar dataKey="aiPercent" name="% AI" stackId="share" fill={AI_COLORS.ai} />
            <Bar dataKey="nonAiPercent" name="% non-AI" stackId="share" fill={AI_COLORS.nonAi} />
          </BarChart>
        </ResponsiveContainer>
      )}
    </div>
  );
}

function SolvedByProjectPanel() {
  const state = useApiData("/api/stats/solved-by-project");
  return (
    <Panel title="Tasks solved per project" state={state} emptyText="No solved tasks yet.">
      {(items) => (
        <div>
          <PieWithLegend
            testId="solved-total-chart"
            title="Solved tasks per project"
            slices={items.map((item, index) => ({
              name: item.project,
              value: item.solvedCount,
              color: PROJECT_COLORS[index % PROJECT_COLORS.length],
            }))}
          />
          <AiShareBarChart items={items} />
        </div>
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
    <WriteTokenProvider>
    <div className="min-h-screen bg-slate-50 text-slate-900">
      <header className="bg-slate-900 px-6 py-4 shadow">
        <h1 className="text-2xl font-bold text-white text-center">
          DarkAIFactory - AI Team workflow
        </h1>
      </header>
      <main className="grid grid-cols-1 gap-6 p-6 lg:grid-cols-[minmax(20rem,1fr)_2fr_1fr]">
        <SolvedByProjectPanel />
        <div className="space-y-6">
          <AgentSlots />
          <TaskEditor />
        </div>
        <ActiveAiTasksPanel />
      </main>
    </div>
    </WriteTokenProvider>
  );
}
