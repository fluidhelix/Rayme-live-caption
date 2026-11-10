'use strict';
const fs = require('node:fs');
const path = require('node:path');
const net = require('node:net');
const { spawn } = require('node:child_process');
const { wav } = require('./audio');
function resolveRuntime(env = process.env, root = process.cwd()) {
  const walk = (dir, depth = 0) => depth > 3 || !fs.existsSync(dir) ? [] : fs.readdirSync(dir, { withFileTypes: true }).flatMap(e => e.isDirectory() ? walk(path.join(dir, e.name), depth + 1) : [path.join(dir, e.name)]);
  const files = walk(path.join(root, 'whisper'));
  const gpu = f => ['ggml-cuda.dll', 'ggml-vulkan.dll'].some(d => fs.existsSync(path.join(path.dirname(f), d)));
  const servers = files.filter(f => path.basename(f).toLowerCase() === 'whisper-server.exe');
  const exe = path.resolve(root, env.WHISPER_SERVER_EXE || servers.find(gpu) || servers[0] || 'whisper/whisper-server.exe');
  const model = path.resolve(root, env.WHISPER_MODEL || files.find(f => path.basename(f) === 'ggml-large-v3-turbo.bin') || 'whisper/ggml-large-v3-turbo.bin');
  for (const f of [exe, model]) if (!fs.existsSync(f)) throw new Error(`Missing local Whisper file: ${f}. Configure WHISPER_SERVER_EXE and WHISPER_MODEL in .env.`);
  return { exe, model };
}
async function request(url, options = {}) {
  const response = await fetch(url, { ...options, signal: AbortSignal.any([AbortSignal.timeout(options.timeout || 60000), ...(options.signal ? [options.signal] : [])]) });
  if (!response.ok) { await response.body?.cancel(); throw new Error(`Whisper HTTP ${response.status}`); }
  // Bound responses; server error pages must never become captions.
  const reader = response.body.getReader(); const decoder = new TextDecoder(); let text = '';
  while (true) { const {done,value} = await reader.read(); if (done) break; text += decoder.decode(value, { stream: true }); if (text.length > 65536) { await reader.cancel(); throw new Error('Oversized Whisper response'); } }
  return JSON.parse(text + decoder.decode());
}
async function waitReady(url, alive, { timeout = 120000, interval = 300, signal } = {}) {
  const end = Date.now() + timeout;
  while (Date.now() < end) {
    if (!alive() || signal?.aborted) throw new Error('Whisper exited before becoming ready');
    try { const data = await request(`${url}/health`, { timeout: 1000, signal }); if (data.status === 'ok') return; } catch {}
    await new Promise(r => setTimeout(r, interval));
  }
  throw new Error('Whisper readiness timed out. This mode requires a whisper-server build with /health support.');
}
async function stopChild(child) {
  if (!child || child.exitCode !== null || child.signalCode) return;
  await new Promise(resolve => {
    const timer = setTimeout(() => { child.kill('SIGKILL'); resolve(); }, 2000);
    child.once('exit', () => { clearTimeout(timer); resolve(); }); child.kill();
  });
}
class Whisper {
  constructor(runtime, env = process.env) { this.runtime = runtime; this.env = env; this.port = Number(env.WHISPER_PORT || 8178); this.url = `http://127.0.0.1:${this.port}`; this.abort = new AbortController(); }
  async start(status) {
    // Refuse another process on the port; never mistake an unrelated server for our child.
    await new Promise((resolve, reject) => { const s = net.createServer(); s.once('error', () => reject(new Error(`Port ${this.port} is busy. Close the other caption mode first.`))); s.listen(this.port, '127.0.0.1', () => s.close(resolve)); });
    const { exe, model } = this.runtime; const dir = path.dirname(exe);
    const args = ['-m', model, '--host', '127.0.0.1', '--port', String(this.port), '-t', this.env.WHISPER_THREADS || '4', '-l', this.env.WHISPER_LANGUAGE || 'en', '-bs', '5', '-bo', '5', '-nc'];
    if (this.env.WHISPER_GPU === 'false') args.push('-ng');
    if (this.env.FLASH_ATTN === 'true') args.push('-fa');
    this.child = spawn(exe, args, { cwd: dir, windowsHide: true, env: { ...process.env, ...this.env, PATH: dir + path.delimiter + process.env.PATH }, stdio: ['ignore', 'ignore', 'pipe'] });
    this.failed = false;
    this.child.on('error', () => { this.failed = true; status('Could not start Whisper. Check executable and runtime DLLs.'); });
    this.child.on('exit', () => { this.failed = true; status('Whisper stopped. Restart captions after checking the local installation.'); });
    this.child.stderr.on('data', () => {}); // Drain native diagnostics; never retain audio/transcripts.
    await waitReady(this.url, () => !this.failed && this.child.exitCode === null, { signal: this.abort.signal });
  }
  async infer(pcm, signal) {
    const body = new FormData(); body.set('file', new Blob([wav(pcm)], { type: 'audio/wav' }), 'audio.wav');
    body.set('no_context', 'true'); body.set('response_format', 'json'); body.set('temperature', '0.0'); body.set('language', this.env.WHISPER_LANGUAGE || 'en');
    const data = await request(`${this.url}/inference`, { method: 'POST', body, signal });
    if (typeof data.text !== 'string') throw new Error('Missing transcription text');
    // Keep [laughter], *coughing*, etc. Only explicit no-audio markers are discarded.
    const text = data.text.replace(/\s+/g, ' ').trim().slice(0, 4000);
    return /^(?:\[|\()?\s*(?:blank_audio|silence|no audio)\s*(?:\]|\))?[.!]?$/i.test(text) ? '' : text;
  }
  async close() { this.abort.abort(); await stopChild(this.child); }
}
module.exports = { resolveRuntime, request, waitReady, stopChild, Whisper };
