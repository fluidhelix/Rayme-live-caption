param([Parameter(Mandatory)][string]$OutputFile)
$ErrorActionPreference = 'Stop'
function File-Version([string]$Name) {
    $command = Get-Command $Name -ErrorAction Stop
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($command.Source).FileVersionRaw.ToString()
    if ($version -eq '0.0.0.0') { throw "Could not identify native tool version: $Name" }
    return $version
}
$vswhere = "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe"
$vs = & $vswhere -latest -version '[17.0,18.0)' -property installationVersion
if ($LASTEXITCODE -ne 0 -or !$vs) { throw 'VS2022 version identification failed' }
$cmake = & cmake --version
if ($LASTEXITCODE -ne 0) { throw 'CMake version identification failed' }
$ninja = & ninja --version
if ($LASTEXITCODE -ne 0) { throw 'Ninja version identification failed' }
$nodeVersion = & node --version
if ($LASTEXITCODE -ne 0) { throw 'Node version identification failed' }
$tools = @{ cl=(File-Version 'cl.exe'); link=(File-Version 'link.exe'); lib=(File-Version 'lib.exe'); rc=(File-Version 'rc.exe'); mt=(File-Version 'mt.exe'); cmake=$cmake[0]; ninja=$ninja; windowsSdk=$env:WindowsSDKVersion.TrimEnd('\'); vcTools=$env:VCToolsVersion; visualStudio=$vs; node=$nodeVersion }
$ambient = [ordered]@{}
foreach ($name in @('CFLAGS','CXXFLAGS','CUDAFLAGS','LDFLAGS','CL','_CL_','LINK','_LINK_','INCLUDE','LIB','LIBPATH')) { $ambient[$name] = [Environment]::GetEnvironmentVariable($name) }
$tools.ambientFlagsSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($ambient | ConvertTo-Json -Compress)))).ToLowerInvariant()
$tools.imageVersion = $env:ImageVersion
New-Item -ItemType Directory -Force (Split-Path ([IO.Path]::GetFullPath($OutputFile)) -Parent) | Out-Null
$toolFile = [IO.Path]::GetFullPath($OutputFile) + '.tools.json'
$tools | ConvertTo-Json | Set-Content $toolFile -Encoding utf8
$repo = (Resolve-Path "$PSScriptRoot/..").Path
$key = & node "$PSScriptRoot/runtime-bundle.js" identity $repo $toolFile $OutputFile
if ($LASTEXITCODE -ne 0) { throw 'Native runtime identity generation failed' }
Write-Host "Native runtime toolchain: $($tools | ConvertTo-Json -Compress)"
Write-Host "Native runtime key: $key"
if ($env:GITHUB_OUTPUT) { "key=$key" | Out-File $env:GITHUB_OUTPUT -Encoding utf8 -Append }
