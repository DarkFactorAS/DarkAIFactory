import { useCallback, useEffect, useState } from "react";

export const THEME_STORAGE_KEY = "aidashboard.theme";
export const THEMES = ["system", "light", "dark"];

function readStoredTheme() {
  try {
    const value = window.localStorage.getItem(THEME_STORAGE_KEY);
    return THEMES.includes(value) ? value : "system";
  } catch {
    return "system";
  }
}

function systemPrefersDark() {
  return typeof window.matchMedia === "function" && window.matchMedia("(prefers-color-scheme: dark)").matches;
}

export function resolveTheme(theme, prefersDark) {
  return theme === "system" ? (prefersDark ? "dark" : "light") : theme;
}

// Holds the theme preference (system | light | dark), persists it in localStorage and
// toggles the "dark" class on <html>. Returns the resolved theme (light | dark) too.
export default function useTheme() {
  const [theme, setThemeState] = useState(readStoredTheme);
  const [prefersDark, setPrefersDark] = useState(systemPrefersDark);

  useEffect(() => {
    if (typeof window.matchMedia !== "function") {
      return undefined;
    }
    const query = window.matchMedia("(prefers-color-scheme: dark)");
    const onChange = (event) => setPrefersDark(event.matches);
    query.addEventListener("change", onChange);
    return () => query.removeEventListener("change", onChange);
  }, []);

  const resolvedTheme = resolveTheme(theme, prefersDark);

  useEffect(() => {
    document.documentElement.classList.toggle("dark", resolvedTheme === "dark");
  }, [resolvedTheme]);

  const setTheme = useCallback((next) => {
    if (!THEMES.includes(next)) {
      return;
    }
    setThemeState(next);
    try {
      window.localStorage.setItem(THEME_STORAGE_KEY, next);
    } catch {
      // Storage may be blocked; the choice then only lasts for this page view.
    }
  }, []);

  return { theme, resolvedTheme, setTheme };
}
