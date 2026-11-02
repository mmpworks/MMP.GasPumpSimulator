// End-to-end checks against the real pair of processes: the station host (which spawns the dispenser) and the
// built renderer served by Vite preview. Both run under a throwaway log directory so the suite never touches
// the journal and chains of a real session. One worker, because there is one pump.
import { defineConfig } from '@playwright/test'
import path from 'node:path'
import os from 'node:os'

const logDir = path.join(os.tmpdir(), `gaspump-e2e-${process.pid}`)
// GASPUMP_E2E_STRATEGY runs the suite under another dispatch (channel, asyncproxy). GASPUMP_DISPENSER_POOL_MAX caps the
// dispenser's thread pool for that run, the way a Compact Framework box would: see docs/ce-budget.md.
const strategy = process.env.GASPUMP_E2E_STRATEGY ?? 'threaded'
const hostEnv: Record<string, string> = { GASPUMP_LOG_DIR: logDir }
if (process.env.GASPUMP_DISPENSER_POOL_MAX) hostEnv.GASPUMP_DISPENSER_POOL_MAX = process.env.GASPUMP_DISPENSER_POOL_MAX

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
      command: `dotnet run --project ../src/GasPump.Host -- --strategy ${strategy}`,
      url: 'http://localhost:5177/api/state',
      timeout: 180_000,
      reuseExistingServer: false,
      env: hostEnv,
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
