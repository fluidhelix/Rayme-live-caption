param([string]$OutputDirectory = 'artifacts/release', [string]$Commit = $env:GITHUB_SHA)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/..").Path
$pins = Get-Content "$PSScriptRoot/assets.json" -Raw | ConvertFrom-Json
if (!$Commit) { $Commit = (& git -C $repo rev-parse HEAD) }
if ($Commit -notmatch '^[a-f0-9]{40}$') { throw 'A full source commit is required' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$output = (Resolve-Path $OutputDirectory).Path
$work = Join-Path $output 'build'
New-Item -ItemType Directory -Force $work | Out-Null
function Fetch-Verified($url, $file, $expected) {
    if (!(Test-Path $file)) { Invoke-WebRequest $url -OutFile "$file.part"; Move-Item "$file.part" $file }
    if ((Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw "Pinned download checksum mismatch: $file" }
}
Fetch-Verified $pins.node.url "$work/node.zip" $pins.node.sha256
Expand-Archive -Force "$work/node.zip" "$work/node"
$node = "$work/node/node-v$($pins.node.version)-win-x64"
Fetch-Verified $pins.whisper.url "$work/whisper.tar.gz" $pins.whisper.sha256
& tar -xzf "$work/whisper.tar.gz" -C $work
if ($LASTEXITCODE -ne 0) { throw 'Whisper extraction failed' }
$source = "$work/whisper.cpp-$($pins.whisper.commit)"
$configure = @('-S', $source, '-B', "$work/cpu", '-DCMAKE_BUILD_TYPE=Release', '-DCMAKE_POLICY_VERSION_MINIMUM=3.10', '-DCMAKE_POLICY_DEFAULT_CMP0091=NEW', '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded', '-DGGML_NATIVE=OFF', '-DGGML_CUDA=OFF', '-DGGML_OPENMP=OFF', '-DGGML_AVX=OFF', '-DGGML_AVX2=OFF', '-DGGML_FMA=OFF', '-DGGML_F16C=OFF', '-DWHISPER_BUILD_TESTS=OFF', '-DWHISPER_CURL=OFF')
$pathmap = '/pathmap:"' + $source.Replace('\','/') + '"=/_/whisper'
# The pinned server uses narrow main(argv), while its model loader explicitly
# decodes UTF-8 to UTF-16. Windows' documented per-process UTF-8 code page keeps
# argv consistent with that loader for all user/model directory names.
$manifest = (Join-Path $PSScriptRoot 'whisper-utf8.manifest').Replace('\','/')
$linkFlags = '/MANIFEST:EMBED /MANIFESTINPUT:"' + $manifest + '"'
$configure += @("-DCMAKE_C_FLAGS=$pathmap", "-DCMAKE_CXX_FLAGS=$pathmap", "-DCMAKE_EXE_LINKER_FLAGS=$linkFlags")
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
$cudaConfigure += @("-DCMAKE_C_FLAGS=$pathmap", "-DCMAKE_CXX_FLAGS=$pathmap", "-DCMAKE_CUDA_FLAGS=-Xcompiler=$pathmap", "-DCMAKE_EXE_LINKER_FLAGS=$linkFlags")
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
& npm ci --prefix "$PSScriptRoot/node" --ignore-scripts --omit=dev --omit=optional --no-audit --no-fund
if ($LASTEXITCODE -ne 0) { throw 'Locked production dependency install failed' }
function Publish-Native($project, $dest) {
    & dotnet restore "$repo/native/$project" -r win-x64 --locked-mode '-p:PublishSingleFile=true' "-p:RuntimeFrameworkVersion=$($pins.dotnetRuntime)"
    if ($LASTEXITCODE -ne 0) { throw "Locked native restore failed: $project" }
    & dotnet publish "$repo/native/$project" --no-restore -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:RaymeDistribution=true' '-p:DebugType=None' '-p:DebugSymbols=false' '-p:ContinuousIntegrationBuild=true' "-p:RuntimeFrameworkVersion=$($pins.dotnetRuntime)" "-p:PathMap=$repo=/_/src" -o $dest
    if ($LASTEXITCODE -ne 0) { throw "Native publish failed: $project" }
}
Publish-Native RaymeLauncher "$work/launcher"
Publish-Native CaptionOverlay "$work/overlay"
Publish-Native LoopbackCapture "$work/helper"
$legal = "$work/licenses"
New-Item -ItemType Directory -Force $legal | Out-Null
Copy-Item "$source/LICENSE" "$legal/WhisperCpp.txt"
Fetch-Verified $pins.modelLicense.url "$legal/OpenAI-Whisper.txt" $pins.modelLicense.sha256
Copy-Item "$PSScriptRoot/legal/*.txt" $legal
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget/packages' }
Copy-Item "$nuget/microsoft.netcore.app.runtime.win-x64/$($pins.dotnetRuntime)/LICENSE.TXT" "$legal/DotNet.txt"
Copy-Item "$nuget/microsoft.netcore.app.runtime.win-x64/$($pins.dotnetRuntime)/THIRD-PARTY-NOTICES.TXT" "$legal/DotNet-ThirdParty.txt"
Copy-Item "$nuget/microsoft.windowsdesktop.app.runtime.win-x64/$($pins.dotnetRuntime)/LICENSE" "$legal/DotNet-Desktop.txt"
$package = "$output/Rayme-Smart-Live-Captions"
if (Test-Path $package) { throw 'Package output already exists. Use a clean build directory.' }
$options = @{ repo=$repo; out=$package; launcher="$work/launcher/Rayme.exe"; overlay="$work/overlay/CaptionOverlay.exe"; helper="$work/helper/LoopbackCapture.exe"; node=$node; whisper=$server.DirectoryName; cuda=$cuda; licenses=$legal; commit=$Commit }
$options | ConvertTo-Json | Set-Content "$work/assemble.json" -Encoding utf8
& node "$PSScriptRoot/assemble.js" "$work/assemble.json"
if ($LASTEXITCODE -ne 0) { throw 'Package allowlist/integrity validation failed' }
$zip = "$output/Rayme-Smart-Live-Captions-v$($pins.version)-win-x64.zip"
Compress-Archive -Path $package -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($zip))" | Set-Content "$output/SHA256SUMS.txt" -Encoding ascii
@{ zipBytes=(Get-Item $zip).Length; unpackedBytes=(Get-ChildItem $package -Recurse -File | Measure-Object Length -Sum).Sum; commit=$Commit; version=$pins.version } | ConvertTo-Json | Set-Content "$output/PACKAGE-SIZE.json"
