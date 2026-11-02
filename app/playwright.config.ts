// End-to-end checks against the real pair of processes: the station host (which spawns the dispenser) and the
// built renderer served by Vite preview. Both run under a throwaway log directory so the suite never touches
// the journal and chains of a real session. One worker, because there is one pump.
import { defineConfig } from '@playwright/test'
import path from 'node:path'
import os from 'node:os'

const logDir = path.join(os.tmpdir(), `gaspump-e2e-${process.pid}`)

export default defineConfig({
  testDir: './e2e',
  timeout: 180_000,
  expect: { timeout: 10_000 },
  workers: 1,
  fullyParallel: false,
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL: 'http://localhost:4173',
    viewport: { width: 1800, height: 1050 },
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  webServer: [
    {
      command: 'dotnet run --project ../src/GasPump.Host -- --strategy threaded',
      url: 'http://localhost:5277/api/state',
      timeout: 180_000,
      reuseExistingServer: false,
      env: { GASPUMP_LOG_DIR: logDir },
      stdout: 'ignore',
      stderr: 'pipe',
    },
    {
      command: 'npx vite preview --port 4173 --strictPort',
      url: 'http://localhost:4173',
      timeout: 60_000,
      reuseExistingServer: false,
    },
  ],
})
