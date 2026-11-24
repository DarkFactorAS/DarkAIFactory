import { request } from "./TaskEditor.jsx";
import { apiFetch } from "./auth.jsx";

// Polling interval of the aiAgent (PollIntervalSeconds default 30).
export const POLL_INTERVAL_MS = 30000;
export const STALE_AFTER_POLLS = 3;
export const CAPABILITIES = ["Jira", "Planning", "Builder", "Review"];
export const PROVIDERS = ["Claude", "Copilot"];
export const CONTAINERS = ["aiagent-1", "aiagent-2", "aiagent-3"];

// Orders agents by assigned container (CONTAINERS order); agents with no or an
// unknown container go last. Ties are broken by name, then id (stable across polls).
function containerRank(agent) {
  const index = CONTAINERS.indexOf(String(agent?.container ?? "").trim().toLowerCase());
  return index === -1 ? CONTAINERS.length : index;
}

export function compareByContainer(a, b) {
  return (
    containerRank(a) - containerRank(b) ||
    String(a?.name ?? "").localeCompare(String(b?.name ?? "")) ||
    (a?.id > b?.id ? 1 : a?.id < b?.id ? -1 : 0)
  );
}

// Returns { agents, nowMs }; nowMs comes from the server Date header (browser clock as fallback).
export async function listAgents() {
  const response = await apiFetch("/api/agents");
  if (!response.ok) throw new Error(`Request failed (${response.status})`);
  const agents = await response.json();
  const header = Date.parse(response.headers.get("Date") ?? "");
  return { agents, nowMs: Number.isNaN(header) ? Date.now() : header };
}
export const listProjects = () => request("/api/agents/projects");

export const createAgent = (agent) =>
  request("/api/agents", { method: "POST", body: JSON.stringify(agent) });

export const updateAgent = (id, agent) =>
  request(`/api/agents/${id}`, { method: "PUT", body: JSON.stringify(agent) });

export const setAgentEnabled = (id, enabled) =>
  request(`/api/agents/${id}/enabled`, {
    method: "PATCH",
    body: JSON.stringify({ enabled }),
  });

export async function deleteAgent(id) {
  const response = await apiFetch(`/api/agents/${id}`, { method: "DELETE" });
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
}

// heartbeatAt is stored as UTC; add 'Z' when the string carries no offset.
export function parseHeartbeat(heartbeatAt) {
  if (!heartbeatAt) return NaN;
  const text = /([zZ]|[+-]\d{2}:?\d{2})$/.test(heartbeatAt) ? heartbeatAt : heartbeatAt + "Z";
  return Date.parse(text);
}

export function isStale(heartbeatAt, nowMs, pollMs = POLL_INTERVAL_MS) {
  const time = parseHeartbeat(heartbeatAt);
  return Number.isNaN(time) || nowMs - time > STALE_AFTER_POLLS * pollMs;
}

export function statusLabel(agent, nowMs, pollMs = POLL_INTERVAL_MS) {
  if (!agent.enabled) return "Disabled";
  const status = agent.status;
  if (!status || isStale(status.heartbeatAt, nowMs, pollMs)) return "Offline";
  if (status.state === "Running") {
    return status.currentIssueKey ? `Running on ${status.currentIssueKey}` : "Running";
  }
  return status.state;
}

// True when the card shows "Running"/"Running on KEY": enabled, fresh heartbeat, state Running.
export function isWorking(agent, nowMs, pollMs = POLL_INTERVAL_MS) {
  const status = agent.status;
  if (!agent.enabled || !status || isStale(status.heartbeatAt, nowMs, pollMs)) return false;
  return status.state === "Running";
}

export function activeCapability(agent, nowMs, pollMs = POLL_INTERVAL_MS) {
  if (!isWorking(agent, nowMs, pollMs) || !agent.status.currentCapability) return null;
  const status = agent.status;
  const wanted = String(status.currentCapability).toLowerCase();
  return CAPABILITIES.find((c) => c.toLowerCase() === wanted) ?? null;
}

const WORK_CAPABILITIES = ["planning", "builder", "review"];

// Lower-cased issue keys an agent is actively planning, building or reviewing.
// An empty capability still counts as working; Jira-capability work does not.
export function workingIssueKeys(agents, nowMs, pollMs = POLL_INTERVAL_MS) {
  const keys = new Set();
  for (const agent of agents ?? []) {
    if (!isWorking(agent, nowMs, pollMs)) continue;
    const { currentIssueKey, currentCapability } = agent.status;
    if (!currentIssueKey) continue;
    if (currentCapability && !WORK_CAPABILITIES.includes(String(currentCapability).toLowerCase())) continue;
    keys.add(String(currentIssueKey).toLowerCase());
  }
  return keys;
}

export const getAiLabels = () => request("/api/settings/ai-labels");

export const getActiveStatuses = () => request("/api/settings/active-statuses");

export const saveActiveStatuses = (statuses) =>
  request("/api/settings/active-statuses", {
    method: "PUT",
    body: JSON.stringify({ statuses }),
  });

export const getCardLabels = () => request("/api/settings/card-labels");

export const saveCardLabels = (labels) =>
  request("/api/settings/card-labels", { method: "PUT", body: JSON.stringify({ labels }) });

export const saveAiLabels = (labels) =>
  request("/api/settings/ai-labels", { method: "PUT", body: JSON.stringify({ labels }) });

export const approveTask = (key) =>
  request(`/api/tasks/${encodeURIComponent(key)}/approve`, { method: "POST" });
