param(
    [string]$Root = (Join-Path $PSScriptRoot "runtime"),
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$Root = [IO.Path]::GetFullPath($Root)

if (Test-Path $Root) {
    if (-not $Force) {
        throw "Runtime already exists: $Root. Use -Force only when you intentionally want to rebuild it."
    }
    Remove-Item -LiteralPath $Root -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $Root | Out-Null

$py = Get-Command py -ErrorAction SilentlyContinue
$python = Get-Command python -ErrorAction SilentlyContinue
if ($py) {
    $exe = $py.Source
    $prefix = @("-3.12")
} elseif ($python) {
    $exe = $python.Source
    $prefix = @()
} else {
    throw "Python 3.12 is required only to BUILD the portable OCR bundle. The finished H2 Notes bundle carries its own Python."
}

function Invoke-Python([string[]]$Arguments) {
    Write-Host "> $exe $($prefix + $Arguments -join ' ')"
    & $exe @prefix @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Python step failed with exit code $LASTEXITCODE" }
}

# Install every dependency first. MinerU changes shared PDF dependencies, so smoke tests run only afterwards.
Invoke-Python @((Join-Path $PSScriptRoot "install.py"), "--root", $Root, "--dependencies", "--budget-gib", "18")
Invoke-Python @((Join-Path $PSScriptRoot "install.py"), "--root", $Root, "--mineru-dependencies", "--budget-gib", "18")
Invoke-Python @((Join-Path $PSScriptRoot "install.py"), "--root", $Root, "--bundle-python", "--budget-gib", "18")

foreach ($engine in @("got-ocr", "docling", "mineru")) {
    Invoke-Python @((Join-Path $PSScriptRoot "install.py"), "--root", $Root,
        "--download-models", $engine, "--approve-model-download", "--budget-gib", "18")
}

$venvPython = Join-Path $Root "venv\Scripts\python.exe"
if (-not (Test-Path $venvPython)) { throw "Portable runtime did not create venv Python." }
foreach ($engine in @("got-ocr", "docling", "mineru")) {
    Write-Host "> smoke $engine"
    & $venvPython -I (Join-Path $PSScriptRoot "smoke.py") --root $Root --engine $engine --timeout 300
    if ($LASTEXITCODE -ne 0) { throw "OCR smoke failed: $engine" }
}

$manifest = Get-Content (Join-Path $Root "runtime.json") -Raw | ConvertFrom-Json
$notReady = @($manifest.engines.PSObject.Properties | Where-Object { -not $_.Value.ready })
if ($notReady.Count -gt 0) { throw "One or more OCR engines are not ready after smoke tests." }

@"
H2 Notes portable OCR runtime
Built: $(Get-Date -Format o)
Engines: GOT-OCR 2.0, Docling, MinerU
Copy this entire `ocr-runtime` folder beside H2Notes.Avalonia.exe.
The app repairs the bundled Python virtual-environment launcher automatically after the folder moves.
"@ | Set-Content -LiteralPath (Join-Path $Root "PORTABLE_READY.txt") -Encoding UTF8

Write-Host "Portable OCR runtime ready: $Root"
Write-Host "Copy the complete H2 Notes publish folder, including ocr-runtime, to another Windows x64 PC."