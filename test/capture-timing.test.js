'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const { PassThrough } = require('node:stream');
const { Capture } = require('../windows-captions/capture');
const { SourceManager, SourceScheduler } = require('../windows-captions/sources');
const { createUI } = require('../windows-captions/server');

function fakeClock() {
  let time = 100000, id = 0;
  const timers = new Map();
  return {
    now: () => time, timers,
    setTimer(fn, delay) { timers.set(++id, { at: time + delay, fn }); return id; },
    clearTimer(key) { timers.delete(key); },
    jump(ms) { time += ms; },
    advance(ms) {
      const end = time + ms;
      for (;;) {
        const next = [...timers].filter(([, timer]) => timer.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
        if (!next) break;
        time = Math.max(time, next[1].at);
        timers.delete(next[0]);
        next[1].fn();
      }
      time = end;
    },
  };
}
const tone = (ms, sample = 0) => {
  const pcm = Buffer.alloc(ms * 32);
  if (sample) for (let i = 0; i < pcm.length; i += 2) pcm.writeInt16LE(sample, i);
  return pcm;
};
const turn = () => new Promise(resolve => setImmediate(resolve));
const app = { kind: 'process', id: '55:100', pid: 55, started: '100', name: 'Test browser' };

async function fixture({ deferred = false, maxAgeMs = 15000 } = {}) {
  const time = fakeClock(), workers = [], calls = [], events = [];
  const ui = await createUI({ port: 0 });
  const publish = event => { events.push(event); ui.publish(event); };
  const scheduler = new SourceScheduler(pcm => {
    if (deferred) return new Promise(resolve => calls.push({ pcm, resolve }));
    calls.push({ pcm });
    return Promise.resolve('Speech');
  }, publish, (source, state, message, health) => publish({ type: 'source-status', source, state, message, ...health }), time.now, { ...time, elapsedNow: time.now, maxAgeMs });
  const manager = new SourceManager({
    scheduler, publish, now: time.now,
    createCapture: (audio, status, reset) => new Capture('helper', audio, status, reset, (_exe, args) => {
      assert.ok(workers.every(worker => worker.exitCode !== null), 'no concurrent helper for the same source');
      const worker = new EventEmitter();
      worker.args = args;
      worker.stdout = new PassThrough();
      worker.stderr = new PassThrough();
      worker.stdin = new PassThrough();
      worker.exitCode = null;
      worker.stdin.on('finish', () => { worker.exitCode = 0; worker.emit('exit', 0); });
      worker.kill = () => assert.fail('test helper must exit on stdin close');
      workers.push(worker);
      return worker;
    }, { ...time, elapsedNow: time.now }),
  });
  await manager.configure('a', app);
  return { time, workers, calls, events, ui, scheduler, manager, close: async () => { await manager.close(); scheduler.close(); await ui.close(); } };
}

test('fresh speech is accepted after long silence with delayed PCM reads', async () => {
  const f = await fixture();
  try {
    const worker = f.workers[0];
    for (let i = 0; i < 650; i++) {
      f.time.advance(100);
      worker.stdout.write(tone(100));
    }
    f.time.advance(1000);
    worker.stdout.write(tone(1000)); // A coalesced read of continuous idle PCM.
    assert.equal(f.calls.length, 0);
    // Drive 100 ms of PCM every 150 ms for another long idle interval.
    // Even 50 ms per tick eventually exceeds the freshness limit.
    for (let i = 0; i < 340; i++) {
      f.time.advance(150);
      worker.stdout.write(tone(100));
    }
    for (const [count, sample] of [[5, 1400], [9, 0]]) for (let i = 0; i < count; i++) {
      f.time.advance(100);
      worker.stdout.write(tone(100, sample));
      await turn();
    }
    assert.equal(f.ui.state.history.at(-1)?.text, 'Speech', 'current speech should produce a final caption');
    assert.equal(f.workers.length, 1, 'moderately delayed reads must not restart capture');
    assert.equal(f.events.filter(event => event.type === 'segment-retired').length, 0);
  } finally { await f.close(); }
  assert.equal(f.time.timers.size, 0);
});

test('a stalled capture followed by a sleep-like clock jump rejects old PCM and inference, then recovers', async () => {
  const f = await fixture({ deferred: true });
  try {
    const old = f.workers[0];
    const source = f.manager.slots.get('a').source;
    f.time.advance(400);
    old.stdout.write(tone(400, 1000));
    f.time.advance(900);
    old.stdout.write(tone(900));
    f.calls.shift().resolve('Earlier finalized caption');
    await turn();
    if (f.calls.length) { f.calls.shift().resolve('Earlier finalized caption'); await turn(); }
    assert.equal(f.ui.state.history.at(-1)?.final, true);

    f.time.advance(800);
    old.stdout.write(tone(800, 1100));
    f.calls.shift().resolve('Earlier useful draft');
    await turn();
    assert.equal(f.ui.state.partials.a.text, 'Earlier useful draft');
    f.time.advance(800);
    old.stdout.write(tone(800, 1200));
    const obsolete = f.calls.shift();
    assert.ok(obsolete);
    old.stdout.write(Buffer.from([0xff])); // Leave an old, incomplete frame.

    f.time.jump(60_000); // Pipe data can arrive before overdue timers run.
    old.stdout.write(tone(1300, 1100));
    await turn();
    assert.equal(old.exitCode, 0, 'the stale helper must close before reuse');
    assert.equal(f.workers.length, 2);
    const fresh = f.manager.slots.get('a').source;
    assert.ok(fresh.generation > source.generation);
    assert.equal(fresh.identity, source.identity);
    assert.deepEqual(f.workers[1].args, old.args);
    assert.equal(f.ui.state.partials.a, undefined);
    assert.deepEqual(f.ui.state.history.map(c => [c.text, c.unfinished === true]), [
      ['Earlier finalized caption', false], ['Earlier useful draft', true],
    ]);

    old.stdout.write(tone(3000, 1100));
    // The shared scheduler can request a partial only every 2.5 seconds
    // while the pre-sleep inference is still running.
    for (let i = 0; i < 26; i++) {
      f.time.advance(100);
      f.workers[1].stdout.write(tone(100, 2000));
    }
    obsolete.resolve('Stale result');
    await turn();
    assert.equal(f.calls.length, 1);
    const current = f.calls.shift();
    assert.ok(current.pcm.length > 0);
    for (let i = 0; i < current.pcm.length; i += 2) assert.equal(current.pcm.readInt16LE(i), 2000);
    current.resolve('Current speech');
    await turn();
    assert.equal(f.ui.state.partials.a.text, 'Current speech');
    assert.equal(f.ui.state.partials.a.source.generation, fresh.generation);
    assert.ok(!f.events.some(e => e.text === 'Stale result'));
  } finally { await f.close(); }
  assert.equal(f.time.timers.size, 0);
});

test('a coalesced read keeps earlier segments older than speech at the end of the block', async () => {
  const f = await fixture({ maxAgeMs: 1000 });
  try {
    const worker = f.workers[0];
    worker.stdout.write(tone(100));
    f.time.advance(2600);
    worker.stdout.write(Buffer.concat([tone(400, 1100), tone(900), tone(400, 2200), tone(900)]));
    await turn();
    assert.equal(f.workers.length, 1);
    assert.equal(f.ui.state.history.length, 1);
    assert.equal(f.ui.state.history[0].id, 2);
    assert.equal(f.ui.state.history[0].audioEndAt, f.time.now());
    assert.ok(f.events.some(e => e.type === 'segment-retired' && e.id === 1 && /freshness limit/.test(e.reason)));
  } finally { await f.close(); }
});

test('repeated PCM stalls recover even without another pipe read and release timers and workers', async () => {
  const f = await fixture();
  try {
    for (let i = 0; i < 12; i++) {
      const old = f.workers.at(-1);
      f.time.advance(700);
      old.stdout.write(tone(700, 1500));
      await turn();
      assert.equal(f.ui.state.partials.a?.text, 'Speech');
      assert.ok(f.time.timers.size <= 2, 'at most a watchdog and a partial expiry');
      assert.ok(f.scheduler.sources.get('a').finals.length <= 2);
      assert.equal(f.manager.slots.get('a').segmenter.tail.length, 0);
      f.time.advance(4000);
      await turn();
      assert.equal(old.exitCode, 0);
      assert.equal(f.workers.length, i + 2);
      assert.equal(f.time.timers.size, 0);
    }
  } finally { await f.close(); }
  assert.equal(f.time.timers.size, 0);
  assert.equal(f.manager.slots.size, 0);
  assert.equal(f.scheduler.sources.size, 0);
  assert.ok(f.workers.every(worker => worker.exitCode === 0));
});

test('closing while a watchdog recovery waits for helper exit prevents a replacement', async () => {
  const f = await fixture();
  try {
    const worker = f.workers[0];
    worker.stderr.write('{"type":"ready"}\n');
    f.time.advance(4000); // A ready helper can also stall before its first PCM block.
    await f.manager.close();
    assert.equal(worker.exitCode, 0);
    assert.equal(f.workers.length, 1);
    assert.equal(f.time.timers.size, 0);
  } finally { await f.close(); }
});
