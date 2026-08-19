[CmdletBinding()]
param(
    [switch] $SkipPython
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

function Invoke-DotNet {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Invoke-Python {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Arguments)

    $venvPython = if ($IsWindows) { ".venv\Scripts\python.exe" } else { ".venv/bin/python" }
    $python = if (Test-Path $venvPython) { $venvPython } else { "python" }

    & $python @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$python $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

try {
    Write-Host "==> Documentation discovery drift" -ForegroundColor Cyan
    Invoke-DotNet run --project tools/Aws2Azure.Documentation -- --check

    Write-Host "==> Configuration examples and copy-paste command references" -ForegroundColor Cyan
    Invoke-DotNet run --project tools/Aws2Azure.DocsQuality

    Write-Host "==> Retrieval-evaluation dataset and undated maturity claims" -ForegroundColor Cyan
    Invoke-DotNet run --project tools/Aws2Azure.DocsEval

    Write-Host "==> Gap-doc schema and generated-artifact freshness" -ForegroundColor Cyan
    Invoke-DotNet run --project tools/Aws2Azure.GapDocs -- --validate
    Invoke-DotNet run --project tools/Aws2Azure.GapDocs
    $gapDocsDiff = git status --porcelain --untracked-files=all -- docs/site src/Aws2Azure.Core/Generated
    if ($gapDocsDiff) {
        Write-Host $gapDocsDiff
        throw "Generated gap-doc artefacts are out of date. Run 'dotnet run --project tools/Aws2Azure.GapDocs' and commit the result."
    }

    if (-not $SkipPython) {
        if (-not (Test-Path ".venv") -and -not (Test-Path ".venv/bin/python") -and -not (Test-Path ".venv\Scripts\python.exe")) {
            Write-Warning "No .venv found; falling back to 'python' on PATH. Create one with:"
            Write-Warning "  python3 -m venv .venv && .venv/bin/python -m pip install -r requirements-docs.txt"
        }

        Write-Host "==> MkDocs strict build" -ForegroundColor Cyan
        Invoke-Python -m mkdocs build --strict

        Write-Host "==> Built-site link, anchor, and search validation" -ForegroundColor Cyan
        Invoke-Python .github/scripts/validate_docs_site.py site /aws2azure/
    }
    else {
        Write-Warning "Skipped MkDocs build and site validation (-SkipPython). Run them separately before relying on a green result."
    }

    Write-Host ""
    Write-Host "Documentation quality suite: clean." -ForegroundColor Green
}
finally {
    Pop-Location
}
