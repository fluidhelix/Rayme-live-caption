param([Parameter(Mandatory)][string]$WhisperRuntimeDirectory, [Parameter(Mandatory)][string]$WhisperRuntimeKey, [string]$OutputDirectory = 'artifacts/release', [string]$Commit = $env:GITHUB_SHA)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/..").Path
$pins = Get-Content "$PSScriptRoot/assets.json" -Raw | ConvertFrom-Json
if (!$Commit) { $Commit = (& git -C $repo rev-parse HEAD) }
if ($Commit -notmatch '^[a-f0-9]{40}$') { throw 'A full source commit is required' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$output = (Resolve-Path $OutputDirectory).Path
$work = Join-Path $output 'build'
New-Item -ItemType Directory -Force $work | Out-Null
. "$PSScriptRoot/whisper-common.ps1"
Fetch-Verified $pins.node.url "$work/node.zip" $pins.node.sha256
Expand-Archive -Force "$work/node.zip" "$work/node"
$node = "$work/node/node-v$($pins.node.version)-win-x64"
# npm.ps1 reparses its caller's command and can reevaluate $PSScriptRoot in
# the wrong scope. Invoke the pinned CLI directly and fail before compilation.
$dependencies = (Resolve-Path "$PSScriptRoot/node").Path
$lockFile = Join-Path $dependencies 'package-lock.json'
if (!(Test-Path -LiteralPath $lockFile -PathType Leaf)) { throw "Missing production npm lockfile: $lockFile" }
$lockVersion = (Get-Content -LiteralPath $lockFile -Raw | ConvertFrom-Json -AsHashtable).lockfileVersion
$lockHash = (Get-FileHash -LiteralPath $lockFile -Algorithm SHA256).Hash
Push-Location -LiteralPath $dependencies
try {
    $prefix = & "$node/node.exe" "$node/node_modules/npm/bin/npm-cli.js" prefix
    if ($LASTEXITCODE -ne 0 -or $prefix -ine $dependencies) { throw "Unexpected production npm prefix: $prefix" }
    Write-Host "Production npm ci: cwd=$((Get-Location).Path); prefix=$prefix; lockfile=$lockFile; lockfileVersion=$lockVersion; SHA256=$lockHash"
    & "$node/node.exe" "$node/node_modules/npm/bin/npm-cli.js" ci --ignore-scripts --omit=dev --omit=optional --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Locked production dependency install failed' }
    if ((Get-FileHash -LiteralPath $lockFile -Algorithm SHA256).Hash -cne $lockHash) { throw 'Production npm lockfile changed during ci' }
} finally { Pop-Location }
$runtime = (Resolve-Path $WhisperRuntimeDirectory).Path
& node "$PSScriptRoot/runtime-bundle.js" verify $runtime $WhisperRuntimeKey
if ($LASTEXITCODE -ne 0) { throw 'Reusable native runtime integrity/identity check failed' }
$runtimeInfo = Get-Content "$runtime/RUNTIME.json" -Raw | ConvertFrom-Json
foreach ($kind in @('cpu', 'cuda')) {
    & "$runtime/$kind/whisper-server.exe" --help
    if ($LASTEXITCODE -ne 0) { throw "Reusable $kind runtime cannot execute" }
}
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
Copy-Item "$runtime/licenses/*.txt" $legal
Fetch-Verified $pins.modelLicense.url "$legal/OpenAI-Whisper.txt" $pins.modelLicense.sha256
Copy-Item "$PSScriptRoot/legal/*.txt" $legal
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget/packages' }
Copy-Item "$nuget/microsoft.netcore.app.runtime.win-x64/$($pins.dotnetRuntime)/LICENSE.TXT" "$legal/DotNet.txt"
Copy-Item "$nuget/microsoft.netcore.app.runtime.win-x64/$($pins.dotnetRuntime)/THIRD-PARTY-NOTICES.TXT" "$legal/DotNet-ThirdParty.txt"
Copy-Item "$nuget/microsoft.windowsdesktop.app.runtime.win-x64/$($pins.dotnetRuntime)/LICENSE" "$legal/DotNet-Desktop.txt"
$package = "$output/Rayme-Smart-Live-Captions"
if (Test-Path $package) { throw 'Package output already exists. Use a clean build directory.' }
$options = @{ repo=$repo; out=$package; launcher="$work/launcher/Rayme.exe"; overlay="$work/overlay/CaptionOverlay.exe"; helper="$work/helper/LoopbackCapture.exe"; node=$node; whisper="$runtime/cpu"; cuda="$runtime/cuda"; licenses=$legal; commit=$Commit; nativeRuntimeKey=$runtimeInfo.key; nativeRuntimeProducerCommit=$runtimeInfo.producerCommit }
$options | ConvertTo-Json | Set-Content "$work/assemble.json" -Encoding utf8
& node "$PSScriptRoot/assemble.js" "$work/assemble.json"
if ($LASTEXITCODE -ne 0) { throw 'Package allowlist/integrity validation failed' }
Assert-WhisperManifests "$package/App/whisper" "$work/manifest-diagnostics"
$zip = "$output/Rayme-Smart-Live-Captions-v$($pins.version)-win-x64.zip"
Compress-Archive -Path $package -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($zip))" | Set-Content "$output/SHA256SUMS.txt" -Encoding ascii
@{ zipBytes=(Get-Item $zip).Length; unpackedBytes=(Get-ChildItem $package -Recurse -File | Measure-Object Length -Sum).Sum; commit=$Commit; version=$pins.version } | ConvertTo-Json | Set-Content "$output/PACKAGE-SIZE.json"
