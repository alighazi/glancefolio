import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// The build output goes straight into the ASP.NET Core static root, so the API
// and the UI are served from one origin and one port - no CORS, and the browser
// only ever makes relative requests.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../Server/wwwroot',
    emptyOutDir: true,
    sourcemap: false,
    chunkSizeWarningLimit: 700,
  },
  server: {
    host: '0.0.0.0',
    port: 5173,
    proxy: {
      '/api': { target: 'http://127.0.0.1:5000', changeOrigin: false },
    },
  },
})
