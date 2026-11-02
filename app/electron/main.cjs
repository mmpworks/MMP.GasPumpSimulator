// Electron's main process. It starts the .NET host, opens one window, and stops the host on exit.
// Set GASPUMP_HOST=external to skip spawning and use a host you started yourself.
// Set GASPUMP_STRATEGY=channel to run the Task/Channel dispatch instead of threads.
// Set GASPUMP_DISPENSER=inproc to keep the pump inside the station process instead of GasPump.Dispenser.exe.
// Set GASPUMP_STATION, GASPUMP_THEME, GASPUMP_PORT, and GASPUMP_DISPENSER_PORT to run a second station beside the
// first. The host reads the same variables, so the spawned host and this window agree on the port.
const { app, BrowserWindow } = require('electron')
const { spawn } = require('node:child_process')
const path = require('node:path')
const http = require('node:http')

const PORT = Number(process.env.GASPUMP_PORT) || 5177
const HOST_URL = `http://localhost:${PORT}`
const STATION = process.env.GASPUMP_STATION || "Howard's"
let host = null

function startHost() {
  if (process.env.GASPUMP_HOST === 'external') return
  const project = path.resolve(__dirname, '..', '..', 'src', 'GasPump.Host')
  const strategy = process.env.GASPUMP_STRATEGY || 'threaded'
  const dispenser = process.env.GASPUMP_DISPENSER || 'socket'
  host = spawn('dotnet', ['run', '--project', project, '--', '--strategy', strategy, '--dispenser', dispenser], {
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
  })
  host.stdout.on('data', d => process.stdout.write(`[host] ${d}`))
  host.stderr.on('data', d => process.stderr.write(`[host] ${d}`))
  host.on('exit', code => { process.stdout.write(`[host] exited ${code}\n`); host = null })
}

function waitForHost(timeoutMs = 60000) {
  const started = Date.now()
  return new Promise((resolve, reject) => {
    const tick = () => {
      const req = http.get(`${HOST_URL}/api/state`, res => { res.resume(); resolve() })
      req.on('error', () => {
        if (Date.now() - started > timeoutMs) reject(new Error('host did not start'))
        else setTimeout(tick, 400)
      })
    }
    tick()
  })
}

async function createWindow() {
  const win = new BrowserWindow({
    width: 1360,
    height: 900,
    title: `${STATION} · Gas Pump Simulator`,
    backgroundColor: '#0e1412',
    webPreferences: { preload: path.join(__dirname, 'preload.cjs'), contextIsolation: true },
  })
  const devUrl = process.env.VITE_DEV_SERVER_URL
  if (devUrl) await win.loadURL(devUrl)
  else await win.loadFile(path.join(__dirname, '..', 'dist', 'index.html'))
}

app.whenReady().then(async () => {
  startHost()
  try { await waitForHost() } catch (e) { console.error(e.message) }
  await createWindow()
})

app.on('window-all-closed', () => {
  if (host) host.kill()
  app.quit()
})

app.on('before-quit', () => { if (host) host.kill() })
