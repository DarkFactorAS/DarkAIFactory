import { useState } from "react";
import Orb from "./Orb.jsx";
import { POLL_INTERVAL_MS, PROVIDERS, isStale, isWorking } from "./agentApi.js";

// One orb per LLM provider (same look as the Claude/Codex status orbs in ThorDashboard).
const MODELS = [
  { provider: "Claude", rgb: [104, 226, 170], workingText: "text-emerald-300" },
  { provider: "Copilot", rgb: [120, 156, 255], workingText: "text-indigo-300" },
].filter((m) => PROVIDERS.includes(m.provider));

const STATE_TEXT = {
  idle: "idle",
  working: "working",
  waiting: "waiting",
  error: "error",
  off: "offline",
};

const STATE_CLASS = {
  waiting: "text-amber-400",
  error: "text-red-400",
};

const PAUSE_KEY = "aidashboard.pauseMotion";

function sameProvider(agent, provider) {
  return String(agent?.llmProvider ?? "").trim().toLowerCase() === provider.toLowerCase();
}

// Derives the orb state of one provider from the agents that use it.
// working: an agent is running; error: an online agent reports an error/failed state;
// waiting: an online agent reports another non-idle state; idle: an agent is online; otherwise off.
export function modelState(agents, provider, nowMs, pollMs = POLL_INTERVAL_MS) {
  const mine = (agents ?? []).filter((a) => sameProvider(a, provider));
  const running = mine.filter((a) => isWorking(a, nowMs, pollMs)).length;
  if (running > 0) return { state: "working", running };
  const online = mine.filter((a) => a.enabled && a.status && !isStale(a.status.heartbeatAt, nowMs, pollMs));
  const states = online.map((a) => String(a.status.state ?? ""));
  if (states.some((s) => /error|fail/i.test(s))) return { state: "error", running: 0 };
  if (states.some((s) => s && s !== "Idle" && s !== "Running")) return { state: "waiting", running: 0 };
  return { state: online.length > 0 ? "idle" : "off", running: 0 };
}

export default function ModelOrbs({ agents, nowMs }) {
  const [paused, setPaused] = useState(() => {
    try {
      return localStorage.getItem(PAUSE_KEY) === "1";
    } catch {
      return false;
    }
  });

  const toggle = () => {
    setPaused((p) => {
      try {
        localStorage.setItem(PAUSE_KEY, p ? "0" : "1");
      } catch {
        // ignore unavailable storage
      }
      return !p;
    });
  };

  return (
    <section
      data-testid="model-orbs"
      aria-label="Model status"
      className="relative rounded-lg border border-slate-800 px-2 pt-7 pb-4 text-slate-200 shadow"
      style={{
        background:
          "radial-gradient(circle at 27% 42%, rgba(70, 190, 140, 0.16), transparent 45%), radial-gradient(circle at 73% 42%, rgba(90, 120, 255, 0.16), transparent 45%), #0a0e10",
      }}
    >
      <button
        type="button"
        onClick={toggle}
        aria-pressed={paused}
        className="absolute top-2 right-3 font-mono text-xs text-sky-300 underline underline-offset-2 hover:text-sky-200"
      >
        {paused ? "Start motion" : "Pause motion"}
      </button>
      <div className="flex justify-around">
        {MODELS.map((m) => {
          const { state, running } = modelState(agents, m.provider, nowMs);
          const text = `${STATE_TEXT[state]}${running > 1 ? ` (${running})` : ""}`;
          const stateClass = state === "working" ? m.workingText : STATE_CLASS[state] ?? "text-slate-400";
          return (
            <div key={m.provider} data-testid="model-orb" data-state={state} className="flex flex-col items-center">
              <Orb state={state} rgb={m.rgb} intensity={running} paused={paused} label={`${m.provider}: ${text}`} />
              <div className="mt-1.5 pl-[0.42em] text-sm uppercase tracking-[0.42em] text-slate-100">{m.provider}</div>
              <div className={`mt-0.5 font-mono text-xs ${stateClass}`}>{text}</div>
            </div>
          );
        })}
      </div>
    </section>
  );
}
