# Phase 4 — portable distribution, explicit shutdown and NVIDIA processing

Baseline: `1d5c96596c55d326571eac63221cc5434757b705`, fetched from the existing feature branch before work. The user reports that baseline's modern WPF overlay/Controls/tray passed Windows CI and physical synthetic-caption testing. Those results are retained as baseline evidence, not claimed for this new portable package. Work continues on `feature/windows-loopback-captions`, draft PR #1. No merge or stable release is authorized.

## Launcher diagnosis and independent runtime reuse (current correction)

The late `3962d5d` failure has a concrete platform mismatch: the portable job ran on Windows Server 2022 (build 20348), while Rayme's preserved launcher requirement is Windows build 22000 or newer. The rejection happened before `--diagnostics` was initialized and displayed a modal MessageBox. The acceptance test then combined a 30-second timeout and nonzero exit into one assertion without draining its redirected pipes. This explains a definite blocker; the next Windows run must still supply its actual exit/timeout/exception evidence before any additional startup cause is claimed.

Application packaging/acceptance now uses `windows-2025` (build 26100). The VS2022 native compiler stays on `windows-2022`. The launcher retains the same Windows/API and x64 restrictions. Diagnostic mode initializes first, emits safe structured stage/reason/type/HResult/native-code records within a 32 KiB budget, and exits nonzero instead of opening dialogs on unsupported platforms, duplicate instances or errors. Normal user startup dialogs and Retry/Cancel remain. No raw exception messages, URLs, handoff tokens or transcripts are logged. Integrity verification still hashes every file and rejects missing, modified, extra and escaping entries.

The portable driver keeps the 30-second verification bound. It separately records timeout, exit code, elapsed time and bounded stdout/stderr tails in `portable-test-diagnostics/launcher-verify.json`. It drains pipes continuously, kills timed-out probes, redacts paths/URLs/capabilities and suppresses .NET apphost GUI errors. Session probes also save bounded diagnostic records and distinguish early process exits from timeouts. Tests cover both noninteractive diagnostic crashes and normal interactive Retry/Cancel recovery.

Native compilation is independent of application packaging:

1. `runtime-inputs` fingerprints pinned whisper.cpp/CUDA sources, architectures and assets, native build/helper scripts, UTF-8 manifest, licenses, actual MSVC/compiler/linker/librarian/RC/MT versions, VS/SDK/CMake/Ninja/Node versions, image version and a hash of ambient compiler/include/library flags. App backend/UI/launcher/test changes are excluded.
2. A trusted push's `whisper-runtime` job serializes producers by that exact key. It restores an exact cache or a trusted matching artifact before compiling. PRs never compile or populate the producer cache; a Linux consumer waits for the trusted push artifact and verifies its identity and every SHA256.
3. On a cache/artifact miss, the producer performs both real builds, `--help` and UTF-8 PE-resource checks, seals all executables/DLLs/licenses/metadata with SHA256, and uploads an immutable artifact immediately. Cache saving follows. These steps do not depend on later app acceptance, so a subsequent failure cannot discard the native bundle. A cache eviction can fall back to the persisted trusted artifact. Producer lookup rejects PR/fork/unrelated-workflow artifacts, but accepts a verified runtime from a run whose later app checks failed.
4. App packaging has no compiler fallback. It requires the exact bundle key, rechecks every SHA256 and both executables, then publishes the .NET apps, assembles the ZIP, rechecks both packaged manifests, and runs every existing extracted-ZIP, native lifecycle, real inference and security/release gate. `VERSION.json` records the runtime key and original producer commit. Reused bytes do not exempt the application from acceptance.

A **first independent cache fill can still require one CUDA build**: previous failed runs did not persist the compiled binaries. No such compilation was run in this Linux correction session. App-only fixes after the first producer completes can use those exact binaries even if application acceptance failed. A legitimate native input/compiler change requires a new build. Cache/artifact storage availability and real Windows cross-run reuse remain pending CI observation; no speed measurement or new Windows PASS is claimed here.

Current local evidence: 63 Node tests pass (including cache-key invalidation, SHA256 tampering/missing/extra-file rejection and trusted-artifact selection/wait policy); 19 launcher platform/integrity/bounded-output/timeout assertions pass; launcher and portable driver compile with zero warnings/errors; all four PowerShell scripts parse; both workflows pass actionlint. The actual managed launcher on Linux exits 1 immediately with structured `unsupported-platform` stderr in diagnostic mode. Windows PE execution, GUI recovery and actual cache hits remain unverified for this correction. Runtime unit fixtures are explicitly not compiled binaries or inference results.

New focused files: `StartupChecks.cs`, `DiagnosticCapture.cs`, `RaymeLauncher.Tests`, `build-whisper.ps1`, `whisper-common.ps1`, `runtime-inputs.ps1`, `runtime-bundle.js`, `find-runtime.cjs`, and runtime-bundle regressions. Existing launcher/portable tests, assembler metadata, packaging script and CI orchestration were updated. Original Discord/audio/inference implementations and production pins remain intact.

## Official delivery and current verification boundary

The official end-user destination is **https://github.com/fluidhelix/Rayme-live-caption/releases**, with these two custom assets:

- `Rayme-Smart-Live-Captions-v1.0.0-win-x64.zip`
- `SHA256SUMS.txt`

The workflow builds and validates the complete ZIP, then calls `release.yml` only after every required gate succeeds. Publication uses an annotated `v1.0.0-rc.<run number>` tag pointing to that exact validated commit, creates an unpublished draft, uploads and checks both assets, and finally exposes a **prerelease**. It never creates `v1.0.0`, marks a release stable/latest, merges a PR, or publishes after a failed required job. A failed upload leaves an unpublished draft rather than a partially published release. Existing RC tags cannot be overwritten. Release notes include exact archive/extracted sizes, source commit and whether the optional NVIDIA hardware test ran.

**A verified published Release cannot be claimed from this cloud session.** GitHub API reads of Actions/Releases return `403 Forbidden`, and Windows/CUDA execution is unavailable on this Linux executor. The implementation and gated publication workflow are prepared and pushed; the new Windows run and official Release page must be checked externally. If a required Windows test or repository publishing permission fails, publication remains blocked. There is deliberately no alternative Google Drive/end-user artifact distribution and no bypass for those gates.

The development artifact `Rayme-Smart-Live-Captions-win-x64` exists for build review; it is not the official download location. UI screenshots, test fixtures, validation JSON and CI diagnostics remain separate from the Release assets. Stable promotion requires explicit authorization after final physical acceptance of this exact package.

## Shutdown and supervision

The original defect was unconditional browser launch in `native-overlay.js` for every overlay exit. That code is removed. Rayme now distinguishes:

| Action | Result |
|---|---|
| Hide | Keep overlay/backend/inference running in the notification area. |
| Close Controls or Speech setup | Close only that native control surface. |
| Quit Rayme / native main-window close | Send an explicit quit intent on the owned private stdout pipe; stop capture, inference, backend and overlay. No browser is launched. |
| Unexpected overlay exit, including exit code 0 | Stop the session and let `Rayme.exe` offer Retry or Cancel. An exit code is not user intent. |
| Backend or initialized inference-process failure | Stop descendants and show a native explanation/recovery choice. |
| Launcher termination | Windows closes the owning Job, terminating the entire owned tree. |

The backend generates a fresh random exit capability for each owned WPF launch and passes it alongside the authenticated URL over stdin. Only a matching `quit-rayme` control frame is accepted. The URL and capability are absent from argv, persistent settings and diagnostic logs. WebSocket clients cannot send a quit command. The caption server's authentication and exact Origin validation remain intact.

`Rayme.exe` creates Node **suspended**, assigns it to a Windows kill-on-close Job, then resumes it. The overlay, Whisper, helpers and NVIDIA diagnostic queries remain descendants. This avoids the spawn-before-assignment race. Backend shutdown cancels setup/downloads and inference, closes capture workers, waits for serialized controls, closes the native window/server, and signals its explicit shutdown reason to the launcher. A 15-second supervisor deadline enforces cleanup if graceful shutdown stalls. Cleanup occurs before a Retry prompt or a new session. A per-session named mutex prevents repeated clicks from creating competing instances.

The launcher is a self-contained Windows GUI executable, not a batch file or shell. Startup/integrity problems use native dialogs. It sanitizes the child environment, uses the bundled Node executable by absolute Unicode path, keeps standard pipes private, and does not depend on globally installed Node/.NET or a command prompt. Its optional `--diagnostics` output contains lifecycle types/PIDs, not captions or credentials.

## Portable layout and integrity

```text
Rayme-Smart-Live-Captions/
  Rayme.exe
  READ-ME-FIRST.txt
  RELEASE-NOTES.md
  VERSION.json
  INTEGRITY.json
  LICENSE.txt
  Licenses/*.txt
  App/
    node/node.exe
    backend/<explicit production JS/HTML/JSON allowlist>
    backend/node_modules/ws/<required runtime files and license>
    native/CaptionOverlay.exe
    native/LoopbackCapture.exe
    whisper/cpu/whisper-server.exe + CPU DLLs
    whisper/cuda/whisper-server.exe + CUDA DLLs + licensed NVIDIA runtimes
```

Every shipped file is enumerated and SHA256-hashed; the launcher checks contents before startup. The complete ZIP gets `SHA256SUMS.txt`. The builder and verifier reject unexpected/unlisted files and forbidden paths. No `.env`, credentials, user settings, models, transcripts, captured audio, fixtures, tests, developer scripts, original Discord entry files, npm tooling, SDK or PDBs are copied. The end-user WPF build excludes its embedded synthetic smoke class; the independent Windows package tests exercise the actual distributed program from outside the ZIP. The ordinary development WPF GUI smoke test is still required separately.

The Node backend is bundled as ordinary CommonJS files plus **only** its locked `ws` dependency; no unsupported Node single-executable assumptions are made. Portable mode does not load dotenv or the original Discord dependency tree. `VERSION.json` records the source and runtime versions. The file manifest checks corruption; it is not an Authenticode signature. Executables remain unsigned. Defender is checked in CI where its service is available; detections fail publication, and unavailable scanning is explicitly reported without an approval claim. No security features are disabled.

### Pinned components

| Component | Pin / integrity |
|---|---|
| Node Windows x64 | 22.23.5; official archive SHA256 in `distribution/assets.json`. Locally downloaded and verified. PE imports inspected: Windows system DLLs only. |
| Production Node dependency | ws 8.21.3; separate npm lock with SHA512, installed without scripts or optional native peers. |
| .NET SDK / bundled runtime | 10.0.404 / 10.0.15; self-contained x64 apphost/runtime; NuGet lock files and locked release restores. |
| Audio helper | Existing NAudio.Core/Wasapi 3.3.0; transitive integrity recorded in the NuGet lock. |
| whisper.cpp | Existing commit d1be6fde11ac6e0407606b4e42fe72d34add8037 and source archive SHA256, unchanged from the proven integration pin. |
| CUDA toolchain/runtime | 12.8.1 redistributable manifest; nvcc 12.8.93, cudart/CCCL 12.8.90, cuBLAS 12.8.4.1, individual official archive SHA256 pins. |
| Downloaded production model | Large-v3-Turbo, published upstream SHA1 `4af2b29d7ec73d781377bfd1758ca957a807e941` from the pinned whisper.cpp model catalog. The SHA1 is upstream's content pin; ZIP/components use SHA256. |
| CI actions / browser checks | Actions pinned to commit SHAs, SDK/Node/Python versions fixed, Windows Playwright dependencies pinned with wheel hashes. |

CPU and CUDA runtimes are compiled separately. Both executables embed Windows’ documented `activeCodePage=UTF-8` manifest: the pinned server uses narrow `main(argv)` while its model loader explicitly decodes UTF-8 to UTF-16. The package acceptance test selects a real model whose filename contains spaces, accents and Japanese characters; this Windows execution is still pending. CPU builds avoid native CPU tuning, AVX/FMA/F16C assumptions, CUDA and external OpenMP runtime dependencies. Both use static MSVC CRT linking. CUDA targets compute 8.6/8.9 (RTX 30/40, including RTX 4080). CUDA VMM is disabled to avoid requiring a driver DLL merely to load/help-check the CUDA binary; actual CUDA initialization still requires the installed NVIDIA driver. The complete compiler/toolkit is build-only. Only permitted CUDA redistributable DLLs ship, with their unaltered licenses. Microsoft/NVIDIA driver installers are not bundled or run.

This is pinned-source reproducibility, not a claim that all compiler/host-image updates produce byte-identical executables. Each generated package has its own recorded hashes.

## First run, models and offline behavior

The native window/server start before model resolution. Missing/moved/corrupt model settings lead to Speech setup, not a terminal-only error. Users can browse/paste an existing ggml `.bin` path, or explicitly download Large-v3-Turbo. The UI explains approximate 1.63 GB download, at least 2.2 GB free model disk space and 4 GB free RAM, license/attribution and processing modes. Existing GGUF/shortcuts/invalid headers are rejected; actual native loading plus `/health` is required before a model setting is accepted.

Downloads show byte progress, retain valid partial files for Range-based retry, verify the pinned upstream hash, and rename into place only after verification. A fully received partial is recovered offline; invalid resume responses offer a fresh retry without manual file editing. Checksum failures remove the damaged partial without damaging existing valid files/settings. Missing Internet access remains a recoverable setup error. Setup can be interrupted by Quit without corrupting a valid model. Accepted model paths and processing mode are stored atomically in `%LOCALAPPDATA%\Rayme\Runtime\runtime.json`; appearance/placement keep the existing `%LOCALAPPDATA%\Rayme\CaptionOverlay` schema. Keep externally selected models outside the program directory. Completed local setup needs no Internet connection during ordinary captioning.

No production model is silently replaced by tiny.en. That model remains an explicit CI input outside the ZIP. Downloaded/selected models initialize speech recognition, but audio capture still requires an explicit application/device enable action. The original segmentation, accuracy assertions, annotations, fair scheduling, default multimedia endpoint fix and real inference requirements remain intact.

## Auto / NVIDIA GPU (CUDA) / CPU

Auto is the default in both Controls and Speech setup. It prefers compatible NVIDIA hardware with a driver supporting the bundled CUDA 12.8.1 (Windows driver 572.61 or newer), including RTX 4080. Other hardware, missing/incompatible drivers or failed CUDA initialization safely fall back to CPU. Explicit CUDA instead reports a useful error if it cannot initialize CUDA; it never calls a CPU context “GPU.” No `.env` editing or toolkit installation is required.

Mode changes stop capture and the old model process before reloading. Source generations remain monotonic across manager replacements, preventing old in-flight events from being mistaken for a new source. Capture remains off until the user enables sources again. Failed setup/mode changes do not overwrite the last successfully persisted model selection.

Actual-device evidence is separate from requested configuration:

1. `nvidia-smi` discovers a compatible device/driver; UUID visibility selects that specific GPU.
2. A bounded startup-only parser observes the actual native `using CUDA... backend` initialization. `WHISPER_GPU=true` is not proof. Silent native CPU fallback is detected.
3. Before a real inference succeeds, the UI says CUDA is initialized but inference remains unverified.
4. After successful inference, an independent NVIDIA query must report the **current Whisper PID** before GPU activity is marked verified. If unavailable, it remains explicitly unverified. Queries run asynchronously so they cannot delay captions, and are canceled on shutdown/mode change. No transcripts/native inference logs are retained.

No NVIDIA hardware is present in this Linux environment, so **real CUDA inference is unverified here**. Hosted Windows CPU CI only builds/loads the runtime and exercises safe fallback/CPU behavior. An optional workflow-dispatch NVIDIA acceptance job runs on a configured `[self-hosted, Windows, X64, rayme-cuda]` runner and requires actual CUDA backend, real inference, the existing speech accuracy assertions and NVIDIA PID evidence. If requested and failed/canceled, publication is blocked. If not requested, the prerelease explicitly states GPU hardware acceptance is unverified. That test uses the CI model unless physical production-model validation is performed separately.

## Validation results and limits

| Evidence | Result from this session |
|---|---|
| Linux Node regressions | 63 tests pass, including new private quit intent, exit-code-zero crash distinction, no browser launch, setup/path/download/integrity recovery, CPU/GPU mode/fallback/evidence races, package allowlist/tampering and prerelease fail-closed guards. GPU/control unit fixtures are not real GPU inference claims. |
| Native state/settings/placement | Existing 50 assertions pass. |
| Actual native ClientWebSocket → production Node server | Existing nine .NET assertions and Node authentication/reconnection/cross-client checks pass. |
| Browser controls | Existing Playwright/Chromium test passes. |
| Native managed audio conversion/policy | All existing format, silence, process identity/discovery, activation cleanup and buffer tests pass on Linux. No real endpoint is credited. |
| Native executable builds | Launcher, WPF frontend, helper and Windows acceptance driver compile. The three self-contained Windows x64 executables cross-publish successfully without symbols. |
| Official NVIDIA assets | Pinned cuBLAS/cudart archives downloaded and SHA256 checked locally; included DLL imports inspected. No external VC++ runtime import was found in those NVIDIA DLLs. Licenses and redistribution lists reviewed. This does not execute CUDA kernels. |
| Windows CPU/CUDA compile and full ZIP | **Not executed/verified here.** The required Windows portable job performs these checks; Linux native cross-publishing alone is not a verified complete distribution. |
| Packaged Windows first run / mode switch / UI / Job cleanup | Acceptance driver compiles; execution is **pending Windows CI/physical verification**. |
| Windows real Whisper / real audio | Existing strict jobs retained; new packaged CPU speech test uses the same production modules/assertions and actual bundled server. This session cannot credit a new Windows PASS. |
| Physical RTX 4080 / production Large-v3-Turbo / three monitors | **Unverified for this package.** The baseline physical UI pass is separate. |
| Official Release publication | **Unverified:** GitHub API returns 403. No stable/final tag or merge was performed. |

The Windows portable test extracts the real ZIP into a clean path containing spaces and non-ASCII characters. It hides globally installed runtime paths and directly starts `Rayme.exe`, verifies first-run missing/invalid models, selects a real CI model through native UI Automation, verifies persisted paths and CPU/Auto switching with one Whisper process, closes Controls, hides the overlay, sends actual native window close, and checks normal quit, unexpected overlay termination, abrupt launcher termination, no new browser processes and no remaining owned processes. It does not pretend to have active WASAPI capture when no endpoint is available. The existing real Windows audio test and physical acceptance remain responsible for hardware capture.

The full package's exact size cannot be honestly measured before the Windows runtime build. NVIDIA runtime DLLs alone are about 789 MB uncompressed; local native executables total about 322 MB, plus Node/Whisper/other files. Expect an application over 1 GB extracted, excluding the 1.63 GB model. The build computes exact `PACKAGE-SIZE.json` and publishes archive/extracted sizes in each Release's notes; no estimated number is labeled measured.

## Clean Windows acceptance checklist

1. Confirm the matching Windows run succeeded and an official **prerelease** exists with its annotated RC tag, exact source commit, ZIP and SHA256SUMS. Do not substitute a development/source archive or claim this step from Linux cross-builds.
2. On Windows 11 x64 without developer runtimes, verify/extract the entire ZIP and double-click Rayme.exe. No terminal/SDK/npm installation should be needed. Test a folder with spaces/non-ASCII names and normal user permissions.
3. Test missing model, invalid file, a moved model, interrupted/retried download, failed checksum and insufficient disk. Select/download the real Large-v3-Turbo model and verify actual readiness. Restart offline from the saved selection. No source should enable itself.
4. On the RTX 4080, choose Auto and inspect the actual initialized backend/driver. Run real conversation inference and confirm independent GPU PID evidence; inspect NVIDIA tools as corroboration. Test explicit CUDA, CPU, and switching with/without available VRAM. Update only through official NVIDIA driver channels if needed. Never infer acceleration from a checkbox.
5. Enable two real application sources and verify attribution, source replacement, pause/resume, clear across clients, vocal annotations and accuracy. Keep the prior real isolation/default-device tests intact. Measure actual CPU/GPU/game performance and latency with the production model.
6. Test all three monitors/scales, tray/overflow/keyboard recovery, lock/click-through and display removal. These native features retain their existing architecture/preferences.
7. Hide must continue; closing Controls/Setup must continue; Quit Rayme must stop capture, Whisper, backend and UI without Chrome. Repeat launches and quit with active capture and during download/loading. Terminate overlay/backend/launcher separately and verify controlled recovery plus no orphaned workers or competing Whisper instances.
8. Review unsigned-executable/security behavior without disabling Defender/SmartScreen. A hosted scan cannot guarantee trust on every Windows machine. Record exact tag/commit and findings before requesting stable promotion.

## Files and developer commands

New: `native/RaymeLauncher`, `native/PortablePackage.Tests`, guided setup/processing WPF partials, backend setup/processing/device parser/catalog, `distribution` (pinned assets, minimal dependency lock, allowlist assembler, Windows builder, licenses/user docs, fail-closed publisher), package/GPU tests, hashed browser requirements and `release.yml`. Updated: native/backend lifecycle, server setup/processing state/commands, startup-only device facts, test backend-root injection, Windows workflow and reports. Original Discord files, capture implementation, endpoint selection, production segmentation/scheduling and all accuracy/checksum requirements are preserved.

```powershell
npm ci
npm test
dotnet run --project native/CaptionOverlay.Tests -c Release
node test/native-overlay-session.js
dotnet run --project native/LoopbackCapture.Tests -c Release
python test/overlay-browser.py
# Windows developer/CI build host: pinned .NET SDK, MSVC 2022 x64, CMake/Ninja and npm
# First resolve the VS2022 x64 toolchain and compile/seal the native bundle once:
./distribution/runtime-inputs.ps1 -OutputFile artifacts/native-inputs.json
./distribution/build-whisper.ps1 -OutputDirectory artifacts/whisper-runtime -IdentityFile artifacts/native-inputs.json
$key = (Get-Content artifacts/native-inputs.json -Raw | ConvertFrom-Json).key
# Package/accept on a >=22000 host. CI downloads the already sealed bundle:
./distribution/build.ps1 -WhisperRuntimeDirectory artifacts/whisper-runtime -WhisperRuntimeKey $key -OutputDirectory artifacts/release
# Use the exact ZIP extracted to a clean Unicode/spaced directory:
node test/distribution/verify-package.js <extracted-Rayme-directory>
dotnet run --project native/PortablePackage.Tests -c Release -- <extracted-Rayme-directory> <explicit-CI-model>
# Optional real NVIDIA machine, not a mocked test:
node test/distribution/gpu-inference.js <extracted-Rayme-directory> <model> <checksum-verified-jfk.wav>
```

Developer toolchain requirements above apply only to building/testing the source. Recipients use the single official ZIP and Rayme.exe. The existing checkout launcher keeps its legacy developer runtime/environment setup; the portable app supplies the guided bundled-runtime experience.
