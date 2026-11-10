param(
  [string]$Helper = "native/bin/LoopbackCapture.exe",
  [string]$Producer = "artifacts/audio-test-tools/AudioIntegrationProbe.exe"
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path $Helper) -or !(Test-Path $Producer)) { throw 'Download the production helper and windows-audio-test-tools artifacts for this commit first. See AUDIO_TESTING.md.' }
& "$PSScriptRoot/prepare-whisper.ps1" -OutputDirectory artifacts/audio-inputs -FixtureOnly
# Deliberately audible synthetic tones/speech, no microphone, no security/routing changes.
node "$PSScriptRoot/windows-probe.js" $Helper $Producer artifacts/audio-inputs/jfk.wav artifacts/audio-diagnostics/local-windows.json --require-audio
if ($LASTEXITCODE -ne 0) { throw 'Audio integration did not PASS. Read artifacts/audio-diagnostics/local-windows.md (INCONCLUSIVE also fails strict local mode).' }
