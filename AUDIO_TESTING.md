# Automated audio integration testing

Baseline verified: clean `feature/windows-loopback-captions` at `13c95ab`. This change adds test tools/workflow only. Production architecture, Phase 2 behavior, native helper packaging and all Discord files are preserved. Continue on this branch / draft PR #1; do not merge main.

## What the Windows workflow now exercises

| Test | Real integration? | Pass requirements |
| --- | --- | --- |
| Existing Node tests | Mock inference/protocol and synthetic PCM | All assertions pass. Not a WASAPI or real-model claim. |
| Existing native tests | Real conversion arithmetic + mocked lifecycle policy | Ten check groups pass; no endpoint requirement. |
| Existing browser test | Actual browser/WebSocket, synthetic captions | Two-source controls/history/recovery assertions pass. Not a capture claim. |
| `speech-integration` job | **Real CPU whisper-server and tiny.en inference** | Checksum-verified licensed WAV traverses actual Segmenter → SourceScheduler → Whisper.infer HTTP → createUI/history → real WebSocket client. Requires a finalized correctly attributed caption, >=80% expected-keyword recall and the ordered phrase “ask not what your country”. Startup/download/inference/content failures fail CI. No endpoint is needed. |
| Windows process activation | **Real documented WASAPI API and actual production helper** | Launch a controlled process; check independent API activation and production `--process PID --started ticks` readiness. Activation alone is explicitly not an audio PASS. Attempted even if enumeration yields no rendering endpoints. |
| Windows rendering/isolation | **Real two-process rendering and process/device capture** | Separate controlled producer processes render 440 Hz and 997 Hz into one selected endpoint. An independent native-format endpoint meter confirms both tones without production conversion. Production whole-device capture must also contain both; each process capture must be non-silent and reject the other tone by >=20 dB amplitude ratio. |
| Windows speech capture | **Real licensed WAV playback and capture** | Play the known WAV in a controlled process; require >=1 second of captured PCM and RMS >=100 (PCM16 units). Uses process capture when available, otherwise reports whole-device-only capture. This is a non-silence check, not recognition accuracy of captured speech. |

The recognition job and real-audio probe are separate: absence of an endpoint does not prevent testing actual Whisper. The CI-only tiny.en model is downloaded under ignored `artifacts/ci-whisper`, never the production `whisper` directory or `.env`. It does not replace Large-v3-Turbo. The source commit, archive checksum, model checksum and WAV checksum are pinned in `test/audio/assets.json`. Model/source downloads preserve TLS verification. No audio/model binaries are committed.

Fixture: the public-domain Kennedy inaugural-address excerpt shipped with whisper.cpp; exact provenance and license rationale are in [test/audio/FIXTURE_LICENSE.md](test/audio/FIXTURE_LICENSE.md). No private conversation is recorded or uploaded. Audio received by the probe exists only in bounded memory; diagnostics contain numeric metrics/status, not captured samples. The only recognition text under test is the known public-domain fixture.

## Outcomes: PASS vs INCONCLUSIVE vs FAIL

- **PASS:** the stated assertion actually ran and succeeded. An activation PASS never stands in for captured audio. Mock/synthetic passes never stand in for real integration.
- **INCONCLUSIVE:** no active rendering endpoint, an independent meter cannot confirm rendered signals, unsupported OS, or an independently confirmed environmental capability error. Explicit known HRESULTs are E_NOTIMPL, E_NOINTERFACE, REGDB_E_CLASSNOTREG, ERROR_NOT_SUPPORTED, E_ACCESSDENIED, ERROR_SERVICE_NOT_ACTIVE and ERROR_NOT_FOUND. Diagnostics retain the actual error and both activation attempts. No security/service/routing/volume changes are attempted. If process capture is unavailable but rendering works, whole-device checks still run and are labeled accordingly.
- **FAIL:** missing test assets/binaries, checksum/build/download failures, unknown activation errors/timeouts, helper failure when the independent activation works, unexpected worker exit, missing/wrong speech, production capture missing independently observed tones, or process leakage. No unconditional skips or `continue-on-error` are used. A missing result creates a FAIL diagnostic through `ensure-result.js`.

Hosted CI allows the command to complete with exit zero for **INCONCLUSIVE**, emits a GitHub warning, and explicitly writes INCONCLUSIVE to the job summary and JSON/Markdown artifacts. **A green Actions job therefore does not by itself prove real audio passed. Read the diagnostic outcome.** `--require-audio` treats every non-PASS as a nonzero exit, for unattended physical-PC validation.

## Diagnostics/artifacts

[Windows caption helper workflow](https://github.com/fluidhelix/Rayme-live-caption/actions/workflows/windows-captions.yml) retains `windows-loopback-win-x64` and adds:

- `windows-audio-test-tools`: self-contained `AudioIntegrationProbe.exe`, a test-only controlled producer/reference meter. No SDK/runtime install is needed to run it.
- `windows-audio-diagnostics`: `windows.json` / `windows.md` with endpoint discovery, real activation results, signal/RMS/length metrics and exact outcomes.
- `speech-integration-diagnostics`: `speech.json` / `speech.md` with actual model health, segmentation/publication and recognition assertions.

Each job publishes diagnostics with `if: always()`. Download/build/setup failures remain failures, not audio capability skips. Production helper artifacts are uploaded before audio probes, so an audio failure does not discard a successfully built helper. No model, source archive, captured PCM or fixture recording is uploaded in diagnostic artifacts.

## Smallest unattended Windows 11 PC test

Use a normal signed-in desktop session with a working playback device. This intentionally plays tones and a short historical speech; close unrelated audio producers for a controlled reference. No admin, driver installation, virtual cable, service/security changes or game integration is required.

1. Update this feature branch. Download matching `windows-loopback-win-x64` and `windows-audio-test-tools` artifacts from the same successful build commit. Put the helper in `native/bin/LoopbackCapture.exe` and the test tool in `artifacts/audio-test-tools/AudioIntegrationProbe.exe`.
2. From the repository root, run in a PowerShell session permitted by your existing policy:

   ```powershell
   ./test/audio/run-windows.ps1
   ```

   This fetches/checksums only the public fixture and runs the Node probe with `--require-audio`. Existing Node.js is sufficient; no .NET SDK is required for these downloaded self-contained executables. To select a particular active endpoint, set `$env:AUDIO_TEST_ENDPOINT` to its ID from `native/bin/LoopbackCapture.exe --list`. Otherwise the first active render endpoint is used, and its identity is recorded.
3. Read `artifacts/audio-diagnostics/local-windows.md` / `.json`. Strict local mode is nonzero for FAIL **or INCONCLUSIVE**; unattended automation must require PASS. The test controls/cleans up only its own workers; producers also have a 180-second watchdog. No user conversation files are saved.

For a developer without artifacts, build both tools with .NET 10:

```powershell
dotnet publish native/LoopbackCapture -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/windows-loopback
dotnet publish native/AudioIntegrationProbe -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/audio-test-tools
./test/audio/run-windows.ps1 -Helper artifacts/windows-loopback/LoopbackCapture.exe
```

To run the independent real-model test (requires CMake/C++ development tools, already on the hosted Windows runner):

```powershell
./test/audio/prepare-whisper.ps1
node test/audio/speech-e2e.js artifacts/ci-whisper/runtime.json artifacts/audio-diagnostics/speech.json
```

## Execution report for this Linux Cloud task

- Node tests: **24 passed**, including retained Phase 2 tests and new DSP/classification/reporting assertions. The expected FAIL/INCONCLUSIVE scenarios in unit tests are synthetic classifier checks, not real-audio outcomes.
- Existing Chromium two-source integration: **passed**.
- Existing native pure conversion/lifecycle harness: **10 PASS groups** (Linux execution of pure code, not Windows audio).
- Test-only native probe: builds/cross-publishes for Windows; no WASAPI capture/render APIs are run on Linux.
- Actual licensed WAV checksum/format and production segmentation were exercised locally: four partial jobs and one 11.9-second final job.
- Linux platform guard: **INCONCLUSIVE**, explicitly “Not Windows; no native audio API invoked.” This validates reporting/guard behavior only.
- Real Whisper transcription in Cloud: **BLOCKED / not executed**. The model host `huggingface.co` returns proxy CONNECT 403; there is no usable local CI model. It is not reported as a pass or replaced by mocked inference. In Actions the real-model job is required; a failed model download fails it.
- Windows real rendering/process isolation and Windows CI run status: **not verified here**. GitHub API access also returns Forbidden; the pushed branch triggers the existing workflow, but no successful new run/artifact is claimed from this workspace. Windows checks must be read from the run diagnostics.

Changed files: the existing workflow; new `native/AudioIntegrationProbe/{AudioIntegrationProbe.csproj,Program.cs}`; new `test/audio/{assets.json,FIXTURE_LICENSE.md,prepare-whisper.ps1,common.js,speech-e2e.js,windows-probe.js,ensure-result.js,run-windows.ps1}`; new `test/audio-integration.test.js`; this report. Production application files are unchanged. No claim of actual game, GPU or Vanguard compatibility follows from these tests.
