import { useState } from "react";

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

export function metricsDetails(t) {
  return [
    `Average wall-clock time: ${formatDuration(t.avgWallClockMs)}`,
    `Wall-clock time: ${formatDuration(t.wallClockMs)}`,
    `LLM time: ${formatDuration(t.llmDurationMs)} (average ${formatDuration(t.avgLlmDurationMs)})`,
    `Input tokens: ${formatCount(t.inputTokens)}`,
    `Output tokens: ${formatCount(t.outputTokens)}`,
    `Tool calls: ${formatCount(t.toolCalls)}`,
    `Cost: ${formatCostPrecise(t.costUsd)}`,
  ].join("\n");
}

export default function AgentMetrics({ metrics }) {
  const [range, setRange] = useState("allTime");
  if (!metrics) return null;
  const t = metrics[range];
  const tokens =
    t.inputTokens == null && t.outputTokens == null ? null : (t.inputTokens ?? 0) + (t.outputTokens ?? 0);
  return (
    <div data-testid="agent-metrics" className="mt-2 text-xs text-slate-600 dark:text-slate-300">
      <div className="mb-1 inline-flex overflow-hidden rounded border border-slate-300 dark:border-slate-600">
        {[
          ["allTime", "All-time"],
          ["last7Days", "7 days"],
        ].map(([key, text]) => (
          <button
            key={key}
            type="button"
            aria-pressed={range === key}
            onClick={() => setRange(key)}
            className={"px-2 py-0.5 " + (range === key ? "bg-blue-600 text-white" : "")}
          >
            {text}
          </button>
        ))}
      </div>
      <p data-testid="agent-metrics-row" title={metricsDetails(t)} className="cursor-help whitespace-nowrap overflow-x-auto">
        {t.succeeded} succeeded / {t.failed} failed · {formatDuration(t.wallClockMs)} · {formatCount(tokens)} tokens ·{" "}
        {formatCount(t.toolCalls)} tool calls · {formatCost(t.costUsd)}
      </p>
    </div>
  );
}
