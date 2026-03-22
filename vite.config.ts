import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'node:path';

export default defineConfig({
  root: path.resolve(__dirname, 'web/client'),
  plugins: [react()],
  build: {
    outDir: path.resolve(__dirname, 'web/dist'),
    emptyOutDir: true,
  },
  test: {
    globals: true,
    environment: 'node',
    setupFiles: [path.resolve(__dirname, 'web/test/setup.ts')],
    include: ['src/**/*.test.tsx', '../server/**/*.test.ts'],
  },
});
