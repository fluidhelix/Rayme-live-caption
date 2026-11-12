function Fetch-Verified($url, $file, $expected) {
    if (!(Test-Path $file)) { Invoke-WebRequest $url -OutFile "$file.part"; Move-Item "$file.part" $file }
    if ((Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw "Pinned download checksum mismatch: $file" }
}
function Find-ManifestTool {
    $command = Get-Command mt.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $kits = "${env:ProgramFiles(x86)}/Windows Kits/10/bin"
    $tool = Get-ChildItem $kits -Directory | Where-Object Name -Match '^\d+\.\d+\.\d+\.\d+$' | Sort-Object { [version]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName 'x64/mt.exe' } | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (!$tool) { throw 'Windows SDK manifest tool was not found' }
    return $tool
}
function Assert-WhisperManifests([string]$RuntimeRoot, [string]$DiagnosticsDirectory) {
    New-Item -ItemType Directory -Force $DiagnosticsDirectory | Out-Null
    $mt = Find-ManifestTool
foreach ($kind in @('cpu', 'cuda')) {
    $embedded = "$DiagnosticsDirectory/$kind-server.embedded.manifest"
    & $mt "-inputresource:$RuntimeRoot/$kind/whisper-server.exe;#1" "-out:$embedded"
    if ($LASTEXITCODE -ne 0) { throw "Could not extract packaged $kind Whisper manifest" }
    $document = [System.Xml.XmlDocument]::new()
    $document.Load($embedded)
    $namespaces = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespaces.AddNamespace('ws', 'http://schemas.microsoft.com/SMI/2019/WindowsSettings')
    $codePages = @($document.SelectNodes('//ws:activeCodePage', $namespaces))
    if ($codePages.Count -ne 1 -or $codePages[0].InnerText.Trim() -cne 'UTF-8') { throw "Packaged $kind Whisper is missing its UTF-8 activeCodePage" }
    Write-Host "PASS packaged $kind whisper-server RT_MANIFEST/#1 activeCodePage=UTF-8"
}
}
