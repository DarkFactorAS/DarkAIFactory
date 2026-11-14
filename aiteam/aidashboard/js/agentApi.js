import { request } from "./TaskEditor.jsx";

// Polling interval of the aiAgent (PollIntervalSeconds default 30).
export const POLL_INTERVAL_MS = 30000;
export const STALE_AFTER_POLLS = 3;
export const CAPABILITIES = ["Jira", "Planning", "Builder", "Review"];
export const PROVIDERS = ["Claude", "Copilot"];

function authHeaders(token) {
  if (!token) throw new Error("The dashboard write token is required.");
  return { "Content-Type": "application/json", Authorization: "Bearer " + token };
}

// Returns { agents, nowMs }; nowMs comes from the server Date header (browser clock as fallback).
export async function listAgents() {
  const response = await fetch("/api/agents");
  if (!response.ok) throw new Error(`Request failed (${response.status})`);
  const agents = await response.json();
  const header = Date.parse(response.headers.get("Date") ?? "");
  return { agents, nowMs: Number.isNaN(header) ? Date.now() : header };
}
export const listProjects = () => request("/api/agents/projects");

export const createAgent = (token, agent) =>
  request("/api/agents", { method: "POST", headers: authHeaders(token), body: JSON.stringify(agent) });

export const updateAgent = (token, id, agent) =>
  request(`/api/agents/${id}`, { method: "PUT", headers: authHeaders(token), body: JSON.stringify(agent) });

export const setAgentEnabled = (token, id, enabled) =>
  request(`/api/agents/${id}/enabled`, {
    method: "PATCH",
    headers: authHeaders(token),
    body: JSON.stringify({ enabled }),
  });

export async function deleteAgent(token, id) {
  const headers = authHeaders(token);
  const response = await fetch(`/api/agents/${id}`, { method: "DELETE", headers });
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
