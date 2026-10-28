import { defineConfig } from 'vite';

export default defineConfig({
  base: './',
  server: { host: '127.0.0.1', strictPort: true },
  preview: { host: '127.0.0.1', strictPort: true },
  build: { target: 'es2022', sourcemap: false },
  worker: { format: 'es' },
  css: { postcss: { plugins: [] } },
});
