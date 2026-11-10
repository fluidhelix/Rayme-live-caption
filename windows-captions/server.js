'use strict';
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const { randomBytes } = require('node:crypto');
const { WebSocketServer, WebSocket } = require('ws');
async function createUI({ port = 8090, onSelect, onRefresh }) {
  const token = randomBytes(24).toString('hex');
  const state = { type: 'snapshot', status: 'Starting local speech recognition…', history: [], partial: null, devices: [] };
  const server = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://localhost');
    if (url.pathname !== '/' || url.searchParams.get('token') !== token) { res.writeHead(404).end(); return; }
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store', 'Content-Security-Policy': "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'" });
    res.end(fs.readFileSync(path.join(__dirname, 'overlay.html')));
  });
  const wss = new WebSocketServer({ noServer: true, maxPayload: 8192 });
  server.on('upgrade', (req, socket, head) => {
    const url = new URL(req.url, 'http://localhost');
    const origin = `http://127.0.0.1:${server.address().port}`;
    if (req.headers.origin !== origin || url.searchParams.get('token') !== token) { socket.destroy(); return; }
    wss.handleUpgrade(req, socket, head, ws => wss.emit('connection', ws));
  });
  function publish(event) {
    if (event.type === 'status') state.status = event.message;
    if (event.type === 'devices') { state.devices = event.devices; state.selected = event.selected; }
    if (event.type === 'reset') state.partial = null;
    if (event.type === 'caption') {
      if (event.final) { state.partial = null; if (event.text) { state.history.push(event); state.history = state.history.slice(-30); } }
      else state.partial = event;
    }
    for (const ws of wss.clients) if (ws.readyState === WebSocket.OPEN) { if (ws.bufferedAmount > 256 * 1024) ws.terminate(); else ws.send(JSON.stringify(event)); }
  }
  wss.on('connection', ws => {
    ws.send(JSON.stringify(state));
    ws.on('error', () => {});
    ws.on('message', raw => {
      try { const msg = JSON.parse(raw); if (msg.type === 'select' && (msg.id === 'default' || state.devices.some(d => d.id === msg.id))) Promise.resolve(onSelect(msg.id)).catch(() => publish({ type: 'status', message: 'Could not select audio device.' }));
        if (msg.type === 'refresh') Promise.resolve(onRefresh()).catch(() => publish({ type: 'status', message: 'Could not enumerate audio devices.' }));
      } catch {}
    });
  });
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(port, '127.0.0.1', resolve); });
  return { state, publish, url: `http://127.0.0.1:${server.address().port}/?token=${token}`, close: async () => { for (const ws of wss.clients) ws.terminate(); wss.close(); await new Promise(r => server.close(r)); } };
}
module.exports = { createUI };
