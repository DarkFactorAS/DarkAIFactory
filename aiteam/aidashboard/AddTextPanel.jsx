import { useState } from "react";
import { request } from "./TaskEditor.jsx";

// Lets the user append text to an AI task; the backend adds it as a Jira comment.
export default function AddTextPanel({ task, onClose }) {
  const [text, setText] = useState("");
  const [status, setStatus] = useState(null);
  const [error, setError] = useState(null);

  async function handleSubmit(event) {
    event.preventDefault();
    setError(null);
    setStatus(null);
    try {
      await request(`/api/tasks/${encodeURIComponent(task.key)}/comments`, {
        method: "POST",
        body: JSON.stringify({ text }),
      });
      setText("");
      setStatus("Text added to the task.");
    } catch (e) {
      setError(e.message);
    }
  }

  return (
    <form onSubmit={handleSubmit} aria-label={`Add text to ${task.key}`} className="mt-3 space-y-2">
      <textarea
        aria-label="Additional text"
        className="w-full rounded border border-slate-300 p-2 text-sm"
        rows={3}
        value={text}
        onChange={(e) => setText(e.target.value)}
      />
      {error && (
        <p role="alert" className="text-sm text-red-600">
          {error}
        </p>
      )}
      {status && (
        <p role="status" className="text-sm text-green-700">
          {status}
        </p>
      )}
      <div className="flex gap-2">
        <button
          type="submit"
          disabled={!text.trim()}
          className="rounded bg-slate-900 px-3 py-1 text-sm text-white disabled:opacity-50"
        >
          Add text
        </button>
        <button type="button" onClick={onClose} className="rounded border px-3 py-1 text-sm">
          Close
        </button>
      </div>
    </form>
  );
}
