param([string]$WhisperSource)
$ErrorActionPreference = 'Stop'
# Execute only the argument expressions from the actual preparation script, without
# downloading models or substituting its checksum/inference checks.
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    "$PSScriptRoot/prepare-whisper.ps1", [ref]$tokens, [ref]$parseErrors
)
if ($parseErrors.Count) { throw "Preparation script parse error: $parseErrors" }
function Read-Arguments([string]$name) {
    $assignment = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -eq ('$' + $name)
    }, $true)
    if (!$assignment) { throw "Missing $name argument array" }
    $nativeCall = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -eq 'cmake' -and $node.CommandElements.Count -eq 2 -and
        $node.CommandElements[1] -is [System.Management.Automation.Language.VariableExpressionAst] -and
        $node.CommandElements[1].Splatted -and
        $node.CommandElements[1].VariablePath.UserPath -eq $name
    }, $true)
    if (!$nativeCall) { throw "CMake must consume the actual $name array" }
    # These expressions are trusted repository code, evaluated in this test's scope.
    [string[]]@(& ([scriptblock]::Create($assignment.Right.Extent.Text)))
}
function Configure-And-Check {
    [string[]]$arguments = @(Read-Arguments 'configureArguments')
    $policy = @($arguments | Where-Object { $_ -like '-DCMAKE_POLICY_VERSION_MINIMUM=*' })
    if ($policy.Count -ne 1 -or $policy[0] -cne '-DCMAKE_POLICY_VERSION_MINIMUM=3.10') {
        throw "Incorrect policy argument: $($arguments -join ' | ')"
    }
    if ($arguments -contains '.10') { throw 'Policy suffix became a path argument' }
    & cmake @arguments
    if ($LASTEXITCODE -ne 0) { throw 'CMake configure argument round-trip failed' }
    $cache = Get-Content "$root/build/CMakeCache.txt"
    if (!($cache -cmatch '^CMAKE_POLICY_VERSION_MINIMUM:[^=]+=3\.10$')) {
        throw 'Native CMake did not receive the exact 3.10 value'
    }
}
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('whisper argument test ' + [guid]::NewGuid())
$originalMode = $PSNativeCommandArgumentPassing
try {
    $source = Join-Path $temporary 'source with spaces'
    New-Item -ItemType Directory -Force $source | Out-Null
    @'
cmake_minimum_required(VERSION 3.10)
project(ArgumentRoundTrip NONE)
if(NOT "${CMAKE_POLICY_VERSION_MINIMUM}" STREQUAL "3.10")
    message(FATAL_ERROR "Incorrect policy version received")
endif()
add_custom_target(whisper-server COMMAND "${CMAKE_COMMAND}" -E true)
'@ | Set-Content (Join-Path $source 'CMakeLists.txt')
    foreach ($mode in @('Legacy', 'Standard', 'Windows')) {
        $PSNativeCommandArgumentPassing = $mode
        $root = Join-Path $temporary "$mode build with spaces"
        Configure-And-Check
        [string[]]$arguments = @(Read-Arguments 'buildArguments')
        & cmake @arguments
        if ($LASTEXITCODE -ne 0) { throw 'CMake build argument round-trip failed' }
        Write-Host "PASS quoted configure/build arrays: $mode"
    }
    if ($WhisperSource) {
        $source = (Resolve-Path $WhisperSource).Path
        $root = Join-Path $temporary 'real pinned Whisper build'
        Configure-And-Check
        Write-Host 'PASS actual pinned Whisper source configuration'
    }
} finally {
    $PSNativeCommandArgumentPassing = $originalMode
    Remove-Item $temporary -Recurse -Force -ErrorAction SilentlyContinue
}
