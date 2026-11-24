import { useEffect, useState } from "react";
import { getTaskComments } from "./agentApi.js";

// Shows the latest Jira comments of a task (newest first), loaded when mounted.
export default function TaskHistory({ taskKey }) {
  const [state, setState] = useState({ loading: true, error: null, comments: [] });

  useEffect(() => {
    let cancelled = false;
    getTaskComments(taskKey)
      .then((comments) => !cancelled && setState({ loading: false, error: null, comments }))
      .catch((err) =>
        !cancelled && setState({ loading: false, error: err.message || "Unable to load comments", comments: [] }));
    return () => {
      cancelled = true;
    };
  }, [taskKey]);

  return (
    <div
      data-testid="ai-task-history"
      className="mt-2 space-y-2 rounded border border-slate-200 p-2 text-xs dark:border-slate-700"
    >
      {state.loading && <p className="text-slate-500 dark:text-slate-400">Loading comments...</p>}
      {state.error && (
        <p role="alert" className="text-red-600 dark:text-red-400">{state.error}</p>
      )}
      {!state.loading && !state.error && state.comments.length === 0 && (
        <p className="text-slate-500 dark:text-slate-400">No comments</p>
      )}
      {state.comments.map((c, i) => (
        <div key={`${c.created}-${i}`}>
          <p className="font-semibold">
            {c.author}{" "}
            <span className="font-normal text-slate-500 dark:text-slate-400">
              {new Date(c.created).toLocaleString()}
            </span>
          </p>
          <p className="whitespace-pre-line">{c.body}</p>
        </div>
      ))}
    </div>
  );
}
