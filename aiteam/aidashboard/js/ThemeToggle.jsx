export default function ThemeToggle({ theme, onChange }) {
  return (
    <label className="flex items-center gap-2 text-sm text-white">
      <span>Theme</span>
      <select
        value={theme}
        onChange={(event) => onChange(event.target.value)}
        className="rounded border border-slate-500 bg-slate-800 px-2 py-1 text-white"
      >
        <option value="system">System</option>
        <option value="light">Light</option>
        <option value="dark">Dark</option>
      </select>
    </label>
  );
}
