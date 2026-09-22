# Explicit native READ-ONLY helper. Run only with dedicated Office test documents.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OfficeHostPath,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [string]$ManifestPath,
    [switch]$AllowNativeOffice,
    [ValidateRange(5,120)][int]$DeadlineSeconds=30
)
$ErrorActionPreference='Stop'
if (-not $AllowNativeOffice) { throw 'Native access requires -AllowNativeOffice and dedicated test resources.' }
if (Test-Path -LiteralPath $OutputPath) { throw 'Output must be a new create-only path.' }
$helper=(Resolve-Path -LiteralPath $OfficeHostPath).Path
if ([IO.Path]::GetFileName($helper) -ne 'H2AgentLab.OfficeHost.exe') { throw 'Choose the exact packaged OfficeHost executable.' }
$output=[IO.Path]::GetFullPath($OutputPath)
$arguments=@('--native-catalog',$output,'--allow-native-office')
if ($ManifestPath) { $arguments=@('--native-markers',(Resolve-Path -LiteralPath $ManifestPath).Path,$output,'--allow-native-office') }
# Arguments are local explicit file paths. Never execute a shell/provider command from a document.
if (($arguments | Where-Object {$_ -match '["\r\n]'}).Count) { throw 'Invalid argument path.' }
$psi=New-Object Diagnostics.ProcessStartInfo
$psi.FileName=$helper
$psi.UseShellExecute=$false
$psi.CreateNoWindow=$true
$psi.WorkingDirectory=[IO.Path]::GetDirectoryName($helper)
$psi.Arguments=($arguments | ForEach-Object {'"'+$_+'"'}) -join ' '
$owned=[Diagnostics.Process]::Start($psi)
try {
    if (-not $owned.WaitForExit($DeadlineSeconds*1000)) {
        # Kill ONLY the helper process created above, never Word/Excel or another H2 helper.
        try {$owned.Kill();[void]$owned.WaitForExit(2000)} catch {}
        throw 'Native probe timed out. No marker PASS is recorded; leave Office open and inspect its modal/busy state.'
    }
    if ($owned.ExitCode -ne 0) { throw ('Native probe failed or is incomplete; helper exit '+$owned.ExitCode+'. Inspect the local output when present.') }
    Write-Host 'Read-only observation saved. A marker subset does not close the complete AR-020 E3 gate.'
} finally {$owned.Dispose()}
