import { Bar, BarChart, Cell, Legend, Pie, PieChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { useEffect, useRef, useState } from "react";
import useApiData from "./useApiData.js";
import TaskEditor from "./TaskEditor.jsx";
import AddTextPanel from "./AddTextPanel.jsx";
import AgentSlots from "./AgentSlots.jsx";
import ActiveStatusesEditor from "./ActiveStatusesEditor.jsx";
import CardLabelsEditor from "./CardLabelsEditor.jsx";
import AiLabelsEditor from "./AiLabelsEditor.jsx";
import { AuthProvider, useAuth } from "./auth.jsx";
import LoginPage from "./LoginPage.jsx";
import { POLL_INTERVAL_MS, getCardLabels, listAgents, workingIssueKeys } from "./agentApi.js";
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
  const [expanded, setExpanded] = useState(false);
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
      <div className="mb-1 flex items-center gap-1">
        <button
          type="button"
          onClick={() => setExpanded((value) => !value)}
          aria-expanded={expanded}
          aria-controls="solved-ai-details"
          aria-label={expanded ? "Collapse AI vs non-AI details" : "Expand AI vs non-AI details"}
          className="rounded p-1 text-slate-600 hover:bg-slate-200 dark:text-slate-300 dark:hover:bg-slate-700"
        >
          <svg
            viewBox="0 0 20 20"
            width="16"
            height="16"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            aria-hidden="true"
            className={`transition-transform ${expanded ? "rotate-180" : ""}`}
          >
            <path d="M5 8l5 5 5-5" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
        </button>
        <h3 className="text-sm font-semibold">AI vs non-AI per project</h3>
      </div>
      {expanded && (
        <div id="solved-ai-details">
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
      )}
    </div>
  );
}

function SolvedByProjectPanel({ labelsVersion, onLabelsSaved, resolvedTheme }) {
  const state = useApiData("/api/stats/solved-by-project");
  useReloadOn(labelsVersion, state.reload);
  const { canEdit } = useAuth();
  const [editingState, setEditing] = useState(false);
  const editing = editingState && canEdit;
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
        canEdit &&
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

export const DEFAULT_CARD_BADGE_LABELS = ["ai-approved", "aiagent-needs-clarification", "aiagent-ready-to-merge"];

function ActiveAiTasksPanel({ labelsVersion }) {
  const { canEdit } = useAuth();
  const [openKey, setOpenKey] = useState(null);
  const [editingState, setEditing] = useState(false);
  // Without a login the panel is read-only: no status editor and no "add text" form on the cards.
  const editing = editingState && canEdit;
  const state = useApiData("/api/tasks/active-ai", { pollMs: POLL_INTERVAL_MS });
  // Labels shown as badges on the cards (Dashboard.CardLabels); empty or unavailable means the defaults.
  const [cardLabels, setCardLabels] = useState(DEFAULT_CARD_BADGE_LABELS);
  const loadCardLabels = () =>
    getCardLabels()
      .then((data) => setCardLabels(data.labels?.length ? data.labels : DEFAULT_CARD_BADGE_LABELS))
      .catch(() => {});
  useEffect(() => {
    loadCardLabels();
  }, []);
  useEffect(() => {
    if (openKey && state.data && !state.data.some((task) => task.key === openKey)) {
      setOpenKey(null);
    }
  }, [state.data, openKey]);
  useReloadOn(labelsVersion, state.reload);
  // Agent state only drives the highlight; a failed poll keeps the last known state.
  const [agentState, setAgentState] = useState({ agents: null, nowMs: Date.now(), fetchedAt: Date.now() });
  useEffect(() => {
    let cancelled = false;
    let inFlight = false;
    // Advance the server-derived clock by local elapsed time on every tick,
    // independent of request completion, so cached Running status expires
    // even when polls stall or fail.
    const advanceClock = () => {
      setAgentState((prev) => {
        const localNow = Date.now();
        return { ...prev, nowMs: prev.nowMs + Math.max(0, localNow - prev.fetchedAt), fetchedAt: localNow };
      });
    };
    const load = () => {
      // Skip a tick while the previous poll is still running so a delayed
      // older response cannot overwrite a newer snapshot.
      if (inFlight) return;
      inFlight = true;
      listAgents()
        .then(({ agents, nowMs }) => {
          if (!cancelled) setAgentState({ agents, nowMs, fetchedAt: Date.now() });
        })
        .catch(() => {
          if (!cancelled) advanceClock();
        })
        .finally(() => {
          inFlight = false;
        });
    };
    load();
    const timer = setInterval(() => {
      advanceClock();
      load();
    }, POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, []);
  const workingKeys = workingIssueKeys(agentState.agents, agentState.nowMs);
  return (
    <Panel
      title="Top 5 AI tasks"
      state={state}
      emptyText="No active AI tasks."
      extra={
        editing && (
          <>
            <ActiveStatusesEditor
              onCancel={() => setEditing(false)}
              onSaved={() => {
                setEditing(false);
                state.reload();
              }}
            />
            <CardLabelsEditor
              onCancel={() => setEditing(false)}
              onSaved={() => {
                setEditing(false);
                loadCardLabels();
              }}
            />
          </>
        )
      }
      actions={
        canEdit &&
        !editing && (
          <button
            type="button"
            aria-label="Configure statuses and labels"
            title="Configure statuses and labels"
            onClick={() => setEditing(true)}
            className="rounded border border-slate-300 dark:border-slate-600 px-2 py-1 text-sm"
          >
            <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
              <circle cx="12" cy="12" r="3" />
              <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09a1.65 1.65 0 0 0-1-1.51 1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09a1.65 1.65 0 0 0 1.51-1 1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33h0a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51h0a1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82v0a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
            </svg>
          </button>
        )
      }
    >
      {(items) => (
        <ul className="space-y-3">
          {items.map((task) => {
            const working = workingKeys.has(String(task.key).toLowerCase());
            return (
            <li
              key={task.key}
              data-testid="ai-task-card"
              data-working={working ? "true" : "false"}
              className={
                "rounded border-2 p-3 " +
                (working ? "border-blue-600 ring-2 ring-blue-300" : "border-slate-200 dark:border-slate-700")
              }
            >
              <button
                type="button"
                aria-expanded={canEdit ? openKey === task.key : undefined}
                disabled={!canEdit}
                title={canEdit ? undefined : "Log in to add text to this task"}
                onClick={() => setOpenKey(openKey === task.key ? null : task.key)}
                className="block w-full text-left disabled:cursor-default"
              >
              <div className="flex items-center justify-between gap-2">
                <span className="font-semibold">
                  {task.key}
                  {working && <span className="sr-only"> (being worked on)</span>}
                </span>
                <span className="flex flex-wrap items-center justify-end gap-1">
                  {cardLabels.filter((l) => (task.labels ?? []).includes(l)).map((l) => (
                    <span
                      key={l}
                      data-testid="ai-task-label-badge"
                      className="rounded bg-yellow-100 text-yellow-800 dark:bg-yellow-900 dark:text-yellow-200 px-2 py-0.5 text-xs"
                    >
                      {l}
                    </span>
                  ))}
                  <span className="rounded bg-slate-100 dark:bg-slate-700 px-2 py-0.5 text-xs">{task.status}</span>
                </span>
              </div>
              <p className="mt-1 text-sm">{task.summary}</p>
              {task.description && (
                <p className="mt-1 line-clamp-3 whitespace-pre-line text-xs text-slate-500 dark:text-slate-400">
                  {task.description}
                </p>
              )}
              </button>
              {canEdit && openKey === task.key && <AddTextPanel task={task} onClose={() => setOpenKey(null)} />}
            </li>
            );
          })}
        </ul>
      )}
    </Panel>
  );
}

function Dashboard({ theme, resolvedTheme, setTheme }) {
  const { username, canEdit, logout, openLogin } = useAuth();
  const [labelsVersion, setLabelsVersion] = useState(0);
  const [solvedOpen, setSolvedOpen] = useState(false);
  const handleLabelsSaved = () => setLabelsVersion((v) => v + 1);
  const headerRef = useRef(null);
  const mainRef = useRef(null);
  const drawerRef = useRef(null);
  const closeButtonRef = useRef(null);
  const openerRef = useRef(null);
  useEffect(() => {
    if (!solvedOpen) return undefined;
    const opener = document.activeElement;
    openerRef.current = opener instanceof HTMLElement ? opener : null;
    const background = [headerRef.current, mainRef.current].filter(Boolean);
    background.forEach((el) => {
      el.inert = true;
    });
    closeButtonRef.current?.focus();
    const focusableSelector =
      'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';
    const onKey = (e) => {
      if (e.key === "Escape") {
        setSolvedOpen(false);
        return;
      }
      if (e.key !== "Tab" || !drawerRef.current) return;
      const focusable = Array.from(drawerRef.current.querySelectorAll(focusableSelector)).filter(
        (el) => el.offsetParent !== null || el === document.activeElement,
      );
      if (focusable.length === 0) {
        e.preventDefault();
        return;
      }
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      const active = document.activeElement;
      if (!drawerRef.current.contains(active)) {
        e.preventDefault();
        (e.shiftKey ? last : first).focus();
      } else if (e.shiftKey && active === first) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && active === last) {
        e.preventDefault();
        first.focus();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => {
      window.removeEventListener("keydown", onKey);
      background.forEach((el) => {
        el.inert = false;
      });
      openerRef.current?.focus();
    };
  }, [solvedOpen]);
  return (
    <>
    <div className="min-h-screen bg-slate-50 dark:bg-slate-950 text-slate-900 dark:text-slate-100">
      <header ref={headerRef} className="bg-slate-900 px-6 py-4 shadow lg:grid lg:grid-cols-[1fr_auto_1fr] lg:items-center lg:gap-4">
        <div className="mb-2 flex justify-center lg:mb-0 lg:justify-start">
          <button
            type="button"
            aria-label="Show tasks solved"
            aria-expanded={solvedOpen}
            aria-controls="solved-overlay"
            onClick={() => setSolvedOpen((o) => !o)}
            className="rounded p-2 text-white hover:bg-slate-700 focus:outline-none focus:ring-2 focus:ring-sky-400"
          >
            <svg viewBox="0 0 24 24" className="h-6 w-6" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
              <path strokeLinecap="round" strokeLinejoin="round" d="M4 20V10m6 10V4m6 16v-7m4 7H2" />
            </svg>
          </button>
        </div>
        <h1 className="text-2xl font-bold text-white text-center">
          DarkAIFactory - AI Team workflow
        </h1>
        <div className="mt-2 flex flex-wrap items-center justify-center gap-3 lg:mt-0 lg:justify-end">
          <ThemeToggle theme={theme} onChange={setTheme} />
          {canEdit ? (
            <>
              {username && (
                <span data-testid="signed-in-user" className="text-sm text-slate-300">
                  Signed in as <span className="font-semibold text-white">{username}</span>
                </span>
              )}
              <button
                type="button"
                onClick={logout}
                className="rounded border border-slate-500 px-2 py-1 text-sm text-white hover:bg-slate-700 focus:outline-none focus:ring-2 focus:ring-sky-400"
              >
                Log out
              </button>
            </>
          ) : (
            <>
              <span data-testid="read-only-mode" className="text-sm text-slate-300">
                Read-only
              </span>
              <button
                type="button"
                onClick={openLogin}
                className="rounded border border-slate-500 px-2 py-1 text-sm text-white hover:bg-slate-700 focus:outline-none focus:ring-2 focus:ring-sky-400"
              >
                Log in
              </button>
            </>
          )}
        </div>
      </header>
      <main ref={mainRef} className="grid grid-cols-1 gap-6 p-6 lg:grid-cols-[minmax(20rem,1fr)_2fr_1fr]">
        <AgentSlots />
        <TaskEditor />
        <ActiveAiTasksPanel labelsVersion={labelsVersion} />
      </main>
      {solvedOpen && (
        <div className="fixed inset-0 z-30 bg-black/30" aria-hidden="true" onClick={() => setSolvedOpen(false)} />
      )}
      <aside
        id="solved-overlay"
        ref={drawerRef}
        role="dialog"
        aria-modal="true"
        aria-label="Tasks solved per project"
        className={`fixed inset-y-0 left-0 z-40 w-full max-w-xl overflow-y-auto bg-white p-4 shadow-xl dark:bg-slate-800 ${solvedOpen ? "" : "hidden"}`}
      >
        <div className="mb-2 flex justify-end">
          <button
            type="button"
            ref={closeButtonRef}
            aria-label="Close tasks solved"
            onClick={() => setSolvedOpen(false)}
            className="rounded px-2 py-1 text-xl leading-none hover:bg-slate-100 dark:hover:bg-slate-700"
          >
            ×
          </button>
        </div>
        <SolvedByProjectPanel labelsVersion={labelsVersion} onLabelsSaved={handleLabelsSaved} resolvedTheme={resolvedTheme} />
      </aside>
    </div>
    </>
  );
}

// The dashboard is public and read-only until the user logs in; the login page replaces it only
// while open (header "Log in" button, or a write that failed with 401).
function AuthGate() {
  const { status, loginOpen } = useAuth();
  const { theme, resolvedTheme, setTheme } = useTheme();
  if (status === "loading") {
    return (
      <div className="flex min-h-screen items-center justify-center bg-slate-50 dark:bg-slate-950 text-slate-500 dark:text-slate-400">
        <p role="status">Loading...</p>
      </div>
    );
  }
  if (loginOpen) {
    return <LoginPage theme={theme} onThemeChange={setTheme} />;
  }
  return <Dashboard theme={theme} resolvedTheme={resolvedTheme} setTheme={setTheme} />;
}

export default function App() {
  return (
    <AuthProvider>
      <AuthGate />
    </AuthProvider>
  );
}
