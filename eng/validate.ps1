[CmdletBinding()]
param(
    [ValidateSet("fast", "pr", "unit", "conformance", "integration", "perf", "footprint", "aot")]
    [string] $Mode = "fast",

    [string] $RuntimeIdentifier,

    [switch] $RequireAot
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$RuntimeIdentifier = if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    if ($IsWindows) { "win-x64" } elseif ($IsMacOS) { "osx-x64" } else { "linux-x64" }
} else {
    $RuntimeIdentifier
}
$previousPerf = $env:AWS2AZURE_PERF
$previousFootprint = $env:AWS2AZURE_FOOTPRINT
Push-Location $repoRoot

function Invoke-DotNet {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Invoke-Build {
    Invoke-DotNet build -c Release --nologo
}

function Invoke-AotPublish {
    Invoke-DotNet @(
        "publish",
        "src/Aws2Azure.Proxy",
        "-c", "Release",
        "-r", $RuntimeIdentifier,
        "--nologo",
        "-p:ILLinkTreatWarningsAsErrors=true",
        "-p:IlcTreatWarningsAsErrors=true"
    )
}

try {
    switch ($Mode) {
        "fast" {
            Invoke-Build
            Invoke-DotNet test tests/Aws2Azure.Conformance -c Release --no-build --nologo
        }
        "pr" {
            Invoke-Build
            Invoke-DotNet test tests/Aws2Azure.UnitTests -c Release --no-build --nologo
            Invoke-DotNet test tests/Aws2Azure.Conformance -c Release --no-build --nologo
            Invoke-DotNet run --project tools/Aws2Azure.GapDocs --no-build -c Release -- --validate
            if ($RequireAot -or $IsLinux) {
                Invoke-AotPublish
            } else {
                Write-Warning "AOT publish skipped on this host. Run mode 'aot' with the native toolchain installed; CI requires linux-x64."
            }
        }
        "unit" {
            Invoke-DotNet test tests/Aws2Azure.UnitTests -c Release --nologo
        }
        "conformance" {
            Invoke-DotNet test tests/Aws2Azure.Conformance -c Release --nologo
        }
        "integration" {
            Invoke-DotNet test tests/Aws2Azure.IntegrationTests -c Release --nologo
        }
        "perf" {
            $env:AWS2AZURE_PERF = "1"
            Invoke-DotNet test tests/Aws2Azure.PerfTests -c Release --nologo `
                --filter "Category!=RelativeGate" `
                --logger "console;verbosity=normal"
            Invoke-DotNet test tests/Aws2Azure.PerfTests -c Release --no-build --nologo `
                --filter "Category=RelativeGate" `
                --logger "console;verbosity=normal"
        }
        "footprint" {
            $env:AWS2AZURE_FOOTPRINT = "1"
            Invoke-DotNet test tests/Aws2Azure.FootprintTests -c Release --nologo `
                --logger "console;verbosity=normal"
        }
        "aot" {
            Invoke-AotPublish
        }
    }
}
finally {
    $env:AWS2AZURE_PERF = $previousPerf
    $env:AWS2AZURE_FOOTPRINT = $previousFootprint
    Pop-Location
}
