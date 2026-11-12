param([Parameter(Mandatory)][string]$OutputDirectory, [Parameter(Mandatory)][string]$IdentityFile, [string]$Commit=$env:GITHUB_SHA)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/..").Path
if (!$Commit) { $Commit = (& git -C $repo rev-parse HEAD) }
$pins = Get-Content "$PSScriptRoot/assets.json" -Raw | ConvertFrom-Json
. "$PSScriptRoot/whisper-common.ps1"
$identity = Get-Content $IdentityFile -Raw | ConvertFrom-Json
if ($identity.key -notmatch '^whisper-win-x64-v1-[a-f0-9]{64}$') { throw 'Invalid native build identity' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$output = (Resolve-Path $OutputDirectory).Path
if (Get-ChildItem $output) { throw 'Native runtime output must be empty' }
# Extract outside the checkout so upstream git probes cannot embed an app commit.
$workBase = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$work = Join-Path $workBase ('rayme-whisper-' + $identity.key.Substring($identity.key.Length - 32))
New-Item -ItemType Directory -Force $work | Out-Null
Fetch-Verified $pins.whisper.url "$work/whisper.tar.gz" $pins.whisper.sha256
& tar -xzf "$work/whisper.tar.gz" -C $work
if ($LASTEXITCODE -ne 0) { throw 'Whisper extraction failed' }
$source = "$work/whisper.cpp-$($pins.whisper.commit)"
$configure = @('-S', $source, '-B', "$work/cpu", '-G', 'Visual Studio 17 2022', '-A', 'x64', '-T', "version=$env:VCToolsVersion", "-DCMAKE_SYSTEM_VERSION=$($identity.inputs.toolchain.windowsSdk)", '-DCMAKE_BUILD_TYPE=Release', '-DCMAKE_POLICY_VERSION_MINIMUM=3.10', '-DCMAKE_POLICY_DEFAULT_CMP0091=NEW', '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded', '-DGGML_NATIVE=OFF', '-DGGML_CUDA=OFF', '-DGGML_OPENMP=OFF', '-DGGML_AVX=OFF', '-DGGML_AVX2=OFF', '-DGGML_FMA=OFF', '-DGGML_F16C=OFF', '-DWHISPER_BUILD_TESTS=OFF', '-DWHISPER_CURL=OFF')
$pathmap = '/pathmap:"' + $source.Replace('\','/') + '"=/_/whisper'
# The pinned server uses narrow main(argv), while its model loader explicitly
# decodes UTF-8 to UTF-16. Windows' documented per-process UTF-8 code page keeps
# argv consistent with that loader for all user/model directory names.
$manifest = (Join-Path $PSScriptRoot 'whisper-utf8.manifest').Replace('\','/')
# CMake merges .manifest target sources for both VS and Ninja. Do not use
# global linker flags: those also affect CUDA's temporary compiler probes.
Add-Content -Path "$source/examples/server/CMakeLists.txt" -Encoding utf8 -Value "`ntarget_sources(whisper-server PRIVATE `"$manifest`")"
$configure += @("-DCMAKE_C_FLAGS=$pathmap", "-DCMAKE_CXX_FLAGS=$pathmap")
& cmake @configure
if ($LASTEXITCODE -ne 0) { throw 'CPU Whisper configure failed' }
& cmake --build "$work/cpu" --config Release --target whisper-server --parallel 2
if ($LASTEXITCODE -ne 0) { throw 'CPU Whisper build failed' }
$server = Get-ChildItem "$work/cpu/bin" -Recurse -Filter whisper-server.exe | Select-Object -First 1
if (!$server) { throw 'CPU Whisper executable missing' }
& $server.FullName --help
if ($LASTEXITCODE -ne 0) { throw 'Compiled CPU runtime cannot execute' }
# Merge only pinned NVIDIA toolkit components for BUILDING. No toolkit/compiler is shipped.
$toolkit = "$work/cuda-toolkit"
New-Item -ItemType Directory -Force $toolkit | Out-Null
foreach ($component in $pins.cuda.components.PSObject.Properties) {
    $name = $component.Name; $asset = $component.Value
    Fetch-Verified $asset.url "$work/$name.zip" $asset.sha256
    Expand-Archive -Force "$work/$name.zip" "$work/$name"
    $componentRoot = (Get-ChildItem "$work/$name" -Directory | Select-Object -First 1).FullName
    Get-ChildItem $componentRoot | Where-Object { $_.Name -ne 'LICENSE' } | ForEach-Object { Copy-Item $_.FullName $toolkit -Recurse -Force }
}
$cudaConfigure = @('-S', $source, '-B', "$work/cuda-build", '-G', 'Ninja', '-DCMAKE_BUILD_TYPE=Release', '-DCMAKE_POLICY_VERSION_MINIMUM=3.10', '-DCMAKE_POLICY_DEFAULT_CMP0091=NEW', '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded', "-DCMAKE_CUDA_COMPILER=$toolkit/bin/nvcc.exe", "-DCUDAToolkit_ROOT=$toolkit", '-DCMAKE_CUDA_ARCHITECTURES=86;89', '-DGGML_NATIVE=OFF', '-DGGML_CUDA=ON', '-DGGML_CUDA_NO_VMM=ON', '-DGGML_OPENMP=OFF', '-DGGML_AVX=OFF', '-DGGML_AVX2=OFF', '-DGGML_FMA=OFF', '-DGGML_F16C=OFF', '-DWHISPER_BUILD_TESTS=OFF', '-DWHISPER_CURL=OFF')
$cudaConfigure += @("-DCMAKE_C_FLAGS=$pathmap", "-DCMAKE_CXX_FLAGS=$pathmap", "-DCMAKE_CUDA_FLAGS=-Xcompiler=$pathmap")
& cmake @cudaConfigure
if ($LASTEXITCODE -ne 0) { throw 'CUDA Whisper configure failed' }
& cmake --build "$work/cuda-build" --target whisper-server --parallel 2
if ($LASTEXITCODE -ne 0) { throw 'CUDA Whisper build failed' }
$cudaServer = Get-ChildItem "$work/cuda-build/bin" -Recurse -Filter whisper-server.exe | Select-Object -First 1
if (!$cudaServer) { throw 'CUDA server executable missing' }
$cuda = "$work/cuda-runtime"
New-Item -ItemType Directory -Force $cuda | Out-Null
Copy-Item "$($cudaServer.DirectoryName)/*.dll" $cuda
Copy-Item $cudaServer.FullName $cuda
foreach ($dll in @('cudart64_12.dll', 'cublas64_12.dll', 'cublasLt64_12.dll')) { Copy-Item "$toolkit/bin/$dll" $cuda }
& "$cuda/whisper-server.exe" --help
if ($LASTEXITCODE -ne 0) { throw 'CUDA executable/DLL startup check failed (does not verify GPU inference)' }
foreach ($kind in @('cpu', 'cuda')) { New-Item -ItemType Directory -Force "$output/$kind" | Out-Null }
Copy-Item "$($server.DirectoryName)/*.dll" "$output/cpu"
Copy-Item $server.FullName "$output/cpu"
Copy-Item "$cuda/*" "$output/cuda"
New-Item -ItemType Directory -Force "$output/licenses" | Out-Null
Copy-Item "$source/LICENSE" "$output/licenses/WhisperCpp.txt"
Copy-Item "$PSScriptRoot/legal/NVIDIA-cudart.txt", "$PSScriptRoot/legal/NVIDIA-cublas.txt" "$output/licenses"
Assert-WhisperManifests $output "$work/manifest-diagnostics"
& node "$PSScriptRoot/runtime-bundle.js" seal $output $IdentityFile $Commit
if ($LASTEXITCODE -ne 0) { throw 'Native runtime sealing failed' }
