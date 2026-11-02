// Nothing crosses the bridge yet. The renderer talks to the host over HTTP and a WebSocket.
const { contextBridge } = require('electron')
contextBridge.exposeInMainWorld('gaspump', { hostUrl: 'http://localhost:5277' })
