import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { defineConfig } from 'vitest/config'

// Web panel (plan step 1.16, decisions D10, D12): React + TypeScript + Tailwind CSS v4.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  // i18n/strings.json at the repository root is shared with the mobile app (plan step 1.21).
  server: { fs: { allow: ['../..'] } },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test-setup.ts'],
  },
})
