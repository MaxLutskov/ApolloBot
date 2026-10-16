import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()], base: '/miniapp/', cacheDir: '.vite-cache',
  build: { outDir: '../wwwroot/miniapp', emptyOutDir: true },
  server: { proxy: { '/api': 'http://localhost:5179' } }
})
