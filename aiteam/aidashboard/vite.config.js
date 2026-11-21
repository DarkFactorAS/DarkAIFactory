import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// Dev server port and API target can be overridden (e.g. by .vscode/launch.json):
// AIDASHBOARD_DEV_PORT (default 5173) and AIDASHBOARD_API_URL (default http://localhost:5000).
const devPort = Number(process.env.AIDASHBOARD_DEV_PORT) || 5173;
const apiUrl = process.env.AIDASHBOARD_API_URL || "http://localhost:5000";

export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: { outDir: "dist", emptyOutDir: true },
  // strictPort: fail instead of silently moving to another port the browser is not opened on.
  server: { port: devPort, strictPort: true, proxy: { "/api": apiUrl } },
});
