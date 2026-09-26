param(
    [string]$OutputDirectory = ""
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $env:TEMP ("H2Notes-portable-check-" + [Guid]::NewGuid().ToString("N"))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
function Fail-Bootstrap([string]$code, [string]$message) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    [ordered]@{
        passed = $false
        code = $code
        message = $message
        phase = "bootstrap"
        noNetworkProbe = $true
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory "portable-bootstrap-check.json") -Encoding utf8NoBOM
    Write-Error "$code : $message"
    exit 1
}

$required = @(
    "portable-manifest.json",
    "H2Notes.Avalonia.exe",
    "hostfxr.dll",
    "hostpolicy.dll",
    "coreclr.dll"
)
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $name) -PathType Leaf)) {
        Fail-Bootstrap "runtime_component_missing" ("Missing required portable component: " + $name)
    }
}
if (Test-Path -LiteralPath $OutputDirectory) {
    Fail-Bootstrap "output_must_be_new" "Choose a new output directory for each portable verification run."
}

$app = Join-Path $root "H2Notes.Avalonia.exe"
$psi = [Diagnostics.ProcessStartInfo]::new($app)
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
[void]$psi.ArgumentList.Add("--verify-portable")
[void]$psi.ArgumentList.Add($OutputDirectory)
$process = [Diagnostics.Process]::Start($psi)
if ($null -eq $process) { Fail-Bootstrap "app_start_failed" "Could not start packaged H2 Notes preflight." }
if (-not $process.WaitForExit(120000)) {
    try { $process.Kill($true) } catch {}
    Fail-Bootstrap "portable_preflight_timeout" "Packaged preflight exceeded two minutes."
}
if ($process.ExitCode -ne 0) {
    Write-Error "Packaged H2 Notes preflight failed. See portable-check.json."
    exit $process.ExitCode
}
Write-Host "Portable preflight PASS: $OutputDirectory"
exit 0