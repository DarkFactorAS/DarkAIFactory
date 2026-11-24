import { useId, useState } from "react";

const NA = "N/A";

export function formatDuration(ms) {
  if (ms == null) return NA;
  const total = Math.round(ms / 1000);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  if (h > 0) return `${h}h ${m}m`;
  if (m > 0) return `${m}m ${s}s`;
  return `${s}s`;
}

export function formatCount(n) {
  return n == null ? NA : Number(n).toLocaleString("en-US");
}

export function formatCost(usd) {
  return usd == null ? NA : `$${Number(usd).toFixed(2)}`;
}

function formatCostPrecise(usd) {
  return usd == null ? NA : `$${Number(usd).toFixed(4)}`;
}

function totalTokens(t) {
  return t.inputTokens == null && t.outputTokens == null ? null : (t.inputTokens ?? 0) + (t.outputTokens ?? 0);
}

export function metricsDetails(t) {
  return [
    `Average wall-clock time: ${formatDuration(t.avgWallClockMs)}`,
    `Wall-clock time: ${formatDuration(t.wallClockMs)}`,
    `LLM time: ${formatDuration(t.llmDurationMs)} (average ${formatDuration(t.avgLlmDurationMs)})`,
    `Input tokens: ${formatCount(t.inputTokens)}`,
    `Output tokens: ${formatCount(t.outputTokens)}`,
    `Total tokens: ${formatCount(totalTokens(t))}`,
    `Tool calls: ${formatCount(t.toolCalls)}`,
    `Cost: ${formatCostPrecise(t.costUsd)}`,
  ].join("\n");
}

export default function AgentMetrics({ metrics }) {
  const [range, setRange] = useState("allTime");
  const [showDetails, setShowDetails] = useState(false);
  const detailsId = useId();
  if (!metrics) return null;
  const t = metrics[range];
  const details = metricsDetails(t);
  return (
    <div data-testid="agent-metrics" className="mt-2 text-xs text-slate-600 dark:text-slate-300">
      <div data-testid="agent-metrics-row" className="flex items-center gap-2 whitespace-nowrap overflow-x-auto">
        <div className="inline-flex shrink-0 overflow-hidden rounded border border-slate-300 dark:border-slate-600">
          {[
            ["allTime", "All", "All time"],
            ["last7Days", "7d", "Last 7 days"],
          ].map(([key, text, label]) => (
            <button
              key={key}
              type="button"
              aria-pressed={range === key}
              aria-label={label}
              title={label}
              onClick={() => setRange(key)}
              className={"px-2 py-0.5 " + (range === key ? "bg-blue-600 text-white" : "")}
            >
              {text}
            </button>
          ))}
        </div>
        <span>
          {t.succeeded}/{t.succeeded + t.failed} ok
        </span>
        <span
          data-testid="agent-metrics-info"
          role="img"
          tabIndex={0}
          aria-label="Show metrics details"
          aria-describedby={detailsId}
          onFocus={() => setShowDetails(true)}
          onBlur={() => setShowDetails(false)}
          onMouseEnter={() => setShowDetails(true)}
          onMouseLeave={() => setShowDetails(false)}
          onKeyDown={(e) => {
            if (e.key === "Escape") setShowDetails(false);
          }}
          className="inline-block cursor-help text-slate-500 dark:text-slate-400"
        >
          <svg viewBox="0 0 20 20" fill="currentColor" aria-hidden="true" className="h-3.5 w-3.5">
            <path
              fillRule="evenodd"
              d="M18 10a8 8 0 11-16 0 8 8 0 0116 0zm-7-4a1 1 0 11-2 0 1 1 0 012 0zM9 9a1 1 0 000 2v3a1 1 0 001 1h1a1 1 0 100-2v-3a1 1 0 00-1-1H9z"
              clipRule="evenodd"
            />
          </svg>
        </span>
        <span>
          · {formatDuration(t.wallClockMs)} · {formatCost(t.costUsd)}
        </span>
      </div>
      <div
        id={detailsId}
        role="tooltip"
        data-testid="agent-metrics-details"
        hidden={!showDetails}
        className="mt-1 whitespace-pre-line rounded border border-slate-300 bg-white p-2 text-slate-700 shadow dark:border-slate-600 dark:bg-slate-800 dark:text-slate-200"
      >
        {details}
      </div>
    </div>
  );
}
