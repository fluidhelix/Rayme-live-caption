# Rayme Smart Live Captions 1.0.0 — prerelease

A portable Windows 11 x64 application for locally generated playback captions,
with a floating native WPF display, modern Controls, two application sources,
whole-device fallback, source attribution and multi-monitor recovery.

Download **Rayme-Smart-Live-Captions-v1.0.0-win-x64.zip** and **SHA256SUMS.txt**
from this GitHub Release. Extract the complete ZIP and open **Rayme.exe**.
No developer tools or separately installed Node/.NET runtime are required.

The package includes pinned Node.js, minimal locked production dependencies,
self-contained native executables and pinned CPU/CUDA whisper.cpp builds. Models
are selected/downloaded during native first-run setup; Large-v3-Turbo is offered
with pinned upstream hash verification. Model weights are not in the ZIP.

Quit Rayme is an explicit private-pipe request. It stops the backend, Whisper,
capture helpers and overlay, without opening a browser. Unexpected failures
stop the session and offer controlled Retry or Quit. Hide and Close Controls
remain separate operations.

## Acceptance status

Publication is gated on required Node, native, WPF GUI/lifecycle, portable ZIP
startup/integrity, packaged real Whisper inference and retained integration jobs.
The exact commit, runtime versions and archive size accompany the build.
Hardware-dependent Windows audio and physical multi-monitor/performance checks
remain distinct from hosted synthetic tests. See the matching workflow results;
an unavailable hosted audio endpoint is not represented as a physical audio PASS.

This is intentionally a **prerelease**, not a stable certification. Final clean
Windows-machine acceptance is required before a stable release is authorized.
Unsigned publisher/SmartScreen warnings may occur; security must not be disabled.
No Defender whitelist, anti-cheat approval or exclusive-fullscreen visibility
claim is made. CPU speed/model memory and recognition accuracy vary with hardware
and source audio. The same ZIP includes CUDA 12.8.1 runtime components under NVIDIA's redistribution terms. Auto prefers supported RTX 30/40 GPUs, including RTX 4080, with driver 572.61 or newer, and falls back to CPU. Explicit CUDA never silently claims success on CPU. Controls report the actual backend and mark GPU activity unverified until real inference and NVIDIA per-process evidence succeed. Mode changes stop capture and the previous model before loading a replacement; enable sources again afterward. The CUDA toolkit and GPU drivers are not installed by Rayme.

## Privacy and attribution

No credentials, .env, user settings, transcripts, captured audio, CI fixtures or
original Discord program are included. Normal operation is offline after model
setup. Licenses and attribution for every bundled component are in the ZIP.
OpenAI explicitly releases Whisper code and weights under MIT; this package
includes the pinned OpenAI license and whisper.cpp conversion attribution.

Report acceptance results against the release tag and VERSION.json commit.
