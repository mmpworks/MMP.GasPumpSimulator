// Nothing crosses the bridge yet. The renderer talks to the host over HTTP and a WebSocket.
const { contextBridge } = require('electron')
// GASPUMP_PORT picks the station this window talks to; the default is the port the host has always used.
contextBridge.exposeInMainWorld('gaspump', { hostUrl: `http://localhost:${Number(process.env.GASPUMP_PORT) || 5177}` })
