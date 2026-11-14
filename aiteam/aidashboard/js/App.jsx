import { Bar, BarChart, Cell, Legend, Pie, PieChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { useEffect, useRef, useState } from "react";
import useApiData from "./useApiData.js";
import TaskEditor from "./TaskEditor.jsx";
import AddTextPanel from "./AddTextPanel.jsx";
import AgentSlots from "./AgentSlots.jsx";
import AiLabelsEditor from "./AiLabelsEditor.jsx";
import { WriteTokenProvider } from "./WriteToken.jsx";
import { POLL_INTERVAL_MS } from "./agentApi.js";
import useTheme from "./useTheme.js";
import ThemeToggle from "./ThemeToggle.jsx";

const CHART_TEXT = { light: "#334155", dark: "#e2e8f0" };

function tooltipStyle(resolvedTheme) {
  const dark = resolvedTheme === "dark";
  return {
    contentStyle: { backgroundColor: dark ? "#1e293b" : "#ffffff", borderColor: dark ? "#475569" : "#cbd5e1", color: CHART_TEXT[resolvedTheme] },
    itemStyle: { color: CHART_TEXT[resolvedTheme] },
    labelStyle: { color: CHART_TEXT[resolvedTheme] },
  };
}

// Reloads the given query whenever refreshKey changes (skipping the initial render).
function useReloadOn(refreshKey, reload) {
  const lastKey = useRef(refreshKey);
  useEffect(() => {
    if (lastKey.current === refreshKey) {
      return;
    }
    lastKey.current = refreshKey;
    reload();
  }, [refreshKey, reload]);
}

function Panel({ title, state, emptyText, children, actions, extra }) {
  const { data, loading, error } = state;
  return (
    <section className="rounded-lg bg-white dark:bg-slate-800 p-4 shadow">
      <div className="mb-3 flex items-center justify-between gap-2">
        <h2 className="text-lg font-semibold">{title}</h2>
        {actions}
      </div>
      {extra}
      {loading && <p role="status" className="text-slate-500 dark:text-slate-400">Loading...</p>}
      {error && (
        <p role="alert" className="text-red-600 dark:text-red-400">
          Unable to load data: {error}
        </p>
      )}
      {data && data.length === 0 && <p className="text-slate-500 dark:text-slate-400">{emptyText}</p>}
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

function PieWithLegend({ title, slices, testId, resolvedTheme }) {
  const total = slices.reduce((sum, slice) => sum + slice.value, 0);
  const visible = slices.filter((slice) => slice.value > 0);
  return (
    <div data-testid={testId} className="mb-4">
      <h3 className="mb-1 text-sm font-semibold">{title}</h3>
      {total === 0 ? (
        <p className="text-sm text-slate-500 dark:text-slate-400">No solved tasks.</p>
      ) : (
        <>
          <ResponsiveContainer width="100%" height={160}>
            <PieChart>
              <Pie data={visible} dataKey="value" nameKey="name" outerRadius={70}>
                {visible.map((slice) => (
                  <Cell key={slice.name} fill={slice.color} />
                ))}
              </Pie>
              <Tooltip {...tooltipStyle(resolvedTheme)} />
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

function AiShareBarChart({ items, resolvedTheme }) {
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
        <p className="text-sm text-slate-500 dark:text-slate-400">No solved tasks.</p>
      ) : (
        <ResponsiveContainer width="100%" height={260}>
          <BarChart data={data}>
            <XAxis dataKey="project" stroke={CHART_TEXT[resolvedTheme]} />
            <YAxis domain={[0, 100]} tickFormatter={(value) => `${value}%`} stroke={CHART_TEXT[resolvedTheme]} />
            <Tooltip formatter={tooltipFormatter} {...tooltipStyle(resolvedTheme)} />
            <Legend wrapperStyle={{ color: CHART_TEXT[resolvedTheme] }} />
            <Bar dataKey="aiPercent" name="% AI" stackId="share" fill={AI_COLORS.ai} />
            <Bar dataKey="nonAiPercent" name="% non-AI" stackId="share" fill={AI_COLORS.nonAi} />
          </BarChart>
        </ResponsiveContainer>
      )}
    </div>
  );
}

function SolvedByProjectPanel({ labelsVersion, onLabelsSaved, resolvedTheme }) {
  const state = useApiData("/api/stats/solved-by-project");
  useReloadOn(labelsVersion, state.reload);
  const [editing, setEditing] = useState(false);
  return (
    <Panel
      title="Tasks solved per project"
      state={state}
      emptyText="No solved tasks yet."
      extra={
        editing && (
          <AiLabelsEditor
            onCancel={() => setEditing(false)}
            onSaved={() => {
              setEditing(false);
              onLabelsSaved();
            }}
          />
        )
      }
      actions={
        !editing && (
          <button
            type="button"
            onClick={() => setEditing(true)}
            className="rounded border border-slate-300 dark:border-slate-600 px-2 py-1 text-sm"
          >
            Edit AI labels
          </button>
        )
      }
    >
      {(items) => (
        <div>
          <PieWithLegend
            resolvedTheme={resolvedTheme}
            testId="solved-total-chart"
            title="Solved tasks per project"
            slices={items.map((item, index) => ({
              name: item.project,
              value: item.solvedCount,
              color: PROJECT_COLORS[index % PROJECT_COLORS.length],
            }))}
          />
          <AiShareBarChart items={items} resolvedTheme={resolvedTheme} />
        </div>
      )}
    </Panel>
  );
}

function ActiveAiTasksPanel({ labelsVersion }) {
  const [openKey, setOpenKey] = useState(null);
  const state = useApiData("/api/tasks/active-ai", { pollMs: POLL_INTERVAL_MS });
  useEffect(() => {
    if (openKey && state.data && !state.data.some((task) => task.key === openKey)) {
      setOpenKey(null);
    }
  }, [state.data, openKey]);
  useReloadOn(labelsVersion, state.reload);
  return (
    <Panel title="Top 3 AI tasks" state={state} emptyText="No active AI tasks.">
      {(items) => (
        <ul className="space-y-3">
          {items.map((task) => (
            <li key={task.key} className="rounded border border-slate-200 dark:border-slate-700 p-3">
              <button
                type="button"
                aria-expanded={openKey === task.key}
                onClick={() => setOpenKey(openKey === task.key ? null : task.key)}
                className="block w-full text-left"
              >
              <div className="flex items-center justify-between gap-2">
                <span className="font-semibold">{task.key}</span>
                <span className="rounded bg-slate-100 dark:bg-slate-700 px-2 py-0.5 text-xs">{task.status}</span>
              </div>
              <p className="mt-1 text-sm">{task.summary}</p>
              {task.description && (
                <p className="mt-1 line-clamp-3 whitespace-pre-line text-xs text-slate-500 dark:text-slate-400">
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
  const [labelsVersion, setLabelsVersion] = useState(0);
  const { theme, resolvedTheme, setTheme } = useTheme();
  const handleLabelsSaved = () => setLabelsVersion((v) => v + 1);
  return (
    <WriteTokenProvider>
    <div className="min-h-screen bg-slate-50 dark:bg-slate-950 text-slate-900 dark:text-slate-100">
      <header className="bg-slate-900 px-6 py-4 shadow lg:grid lg:grid-cols-[1fr_auto_1fr] lg:items-center lg:gap-4">
        <div className="hidden lg:block" aria-hidden="true" />
        <h1 className="text-2xl font-bold text-white text-center">
          DarkAIFactory - AI Team workflow
        </h1>
        <div className="mt-2 flex justify-center lg:mt-0 lg:justify-end">
          <ThemeToggle theme={theme} onChange={setTheme} />
        </div>
      </header>
      <main className="grid grid-cols-1 gap-6 p-6 lg:grid-cols-[minmax(20rem,1fr)_2fr_1fr]">
        <SolvedByProjectPanel labelsVersion={labelsVersion} onLabelsSaved={handleLabelsSaved} resolvedTheme={resolvedTheme} />
        <div className="space-y-6">
          <AgentSlots />
          <TaskEditor />
        </div>
        <ActiveAiTasksPanel labelsVersion={labelsVersion} />
      </main>
    </div>
    </WriteTokenProvider>
  );
}
