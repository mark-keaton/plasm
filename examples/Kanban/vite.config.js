import { defineConfig } from "vite";

// The client is served by Vite while developing (API calls go on to the
// server), and built into the server's wwwroot for a single deployable site.
export default defineConfig({
  root: "src/Client",
  build: { outDir: "../Server/wwwroot", emptyOutDir: true },
  server: { port: 5173, proxy: { "/api": "http://localhost:5080" } },
});
