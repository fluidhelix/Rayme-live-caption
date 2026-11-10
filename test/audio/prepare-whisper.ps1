param([string]$OutputDirectory = "artifacts/ci-whisper", [switch]$FixtureOnly)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$assets = Get-Content "$PSScriptRoot/assets.json" -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$root = (Resolve-Path $OutputDirectory).Path
function Fetch-Verified($url, $file, $algorithm, $expected) {
    if (!(Test-Path $file)) { Invoke-WebRequest -Uri $url -OutFile "$file.part"; Move-Item "$file.part" $file }
    if ((Get-FileHash $file -Algorithm $algorithm).Hash.ToLowerInvariant() -ne $expected) { throw "Checksum mismatch: $file" }
}
Fetch-Verified $assets.fixtureUrl "$root/jfk.wav" SHA256 $assets.fixtureSha256
if ($FixtureOnly) { return }
Fetch-Verified $assets.modelUrl "$root/ggml-tiny.en.bin" SHA1 $assets.modelSha1
Fetch-Verified $assets.sourceUrl "$root/whisper-source.tar.gz" SHA256 $assets.sourceSha256
$source = "$root/whisper.cpp-$($assets.whisperCommit)"
if (!(Test-Path $source)) { tar -xzf "$root/whisper-source.tar.gz" -C $root; if ($LASTEXITCODE -ne 0) { throw 'Source extraction failed' } }
# String literals avoid PowerShell parsing a dotted -D value as separate tokens.
$configureArguments = @(
    '-S', $source
    '-B', "$root/build"
    '-DCMAKE_BUILD_TYPE=Release'
    '-DCMAKE_POLICY_VERSION_MINIMUM=3.10'
    '-DGGML_NATIVE=OFF'
    '-DGGML_CUDA=OFF'
    '-DWHISPER_BUILD_TESTS=OFF'
    '-DWHISPER_CURL=OFF'
)
& cmake @configureArguments
if ($LASTEXITCODE -ne 0) { throw 'Whisper configure failed' }
$buildArguments = @(
    '--build', "$root/build"
    '--config', 'Release'
    '--target', 'whisper-server'
    '--parallel', '2'
)
& cmake @buildArguments
if ($LASTEXITCODE -ne 0) { throw 'Whisper build failed' }
$server = Get-ChildItem "$root/build/bin" -Recurse -Filter whisper-server.exe | Select-Object -First 1
if (!$server) { throw 'No Windows whisper-server build output' }
$runtime = @{ server = $server.FullName; model = "$root/ggml-tiny.en.bin"; fixture = "$root/jfk.wav" }
$runtime | ConvertTo-Json | Set-Content "$root/runtime.json"
