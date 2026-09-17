param(
    [Parameter(Mandatory=$true)][string]$Destination,
    [string]$SourcePython = (Join-Path $env:LOCALAPPDATA 'H2AgentLab/runtime/python'),
    [switch]$Zip
)
$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $target) { throw 'Choose a NEW destination. Existing packages/data are never overwritten.' }
$zipPath = $target + '.zip'
if ($Zip -and (Test-Path -LiteralPath $zipPath)) { throw 'ZIP already exists; choose a new destination.' }
if (-not (Test-Path -LiteralPath (Join-Path $SourcePython 'python312.dll'))) { throw 'Prepare the private CPython 3.12 runtime first.' }

# Publish the complete executable dependency graph, not a copy of the build exe.
& dotnet publish (Join-Path $PSScriptRoot 'H2AgentLab.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $target --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed. Incomplete directory retained for diagnosis; do not distribute it.' }
& (Join-Path $PSScriptRoot 'Setup-Runtime.ps1') -SourcePython $SourcePython -Destination (Join-Path $target 'python')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PORTABLE.md') -Destination $target

foreach ($skill in @('documents','spreadsheets','pdf','coding','computer-use')) {
    if (-not (Test-Path -LiteralPath (Join-Path $target "skills/$skill/SKILL.md"))) { throw "Missing bundled skill: $skill" }
}
foreach ($file in @('H2AgentLab.exe','runtime/worker.py','runtime-guide.md','python/.ready','coreclr.dll','hostfxr.dll','PresentationFramework.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $target $file))) { throw "Incomplete portable package: $file" }
}
# Known-code check has no model/network calls and writes only synthetic fixtures outside the package.
$checkRoot = $target + '-installation-check'
if (Test-Path -LiteralPath $checkRoot) { throw 'Installation check directory already exists.' }
$arguments = '--verify-install "' + $checkRoot + '"'
$process = Start-Process -FilePath (Join-Path $target 'H2AgentLab.exe') -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Portable runtime verification failed. See $checkRoot/installation-check.txt. No ZIP produced." }

$manifest = Get-ChildItem -LiteralPath $target -Recurse -File | Where-Object { $_.Name -notin @('.execution.lock') } | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($target, $_.FullName).Replace('\','/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
}
[ordered]@{ format = 1; platform = 'Windows x64'; createdUtc = [DateTime]::UtcNow.ToString('O'); files = @($manifest) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $target 'package-manifest.json') -Encoding utf8
if ($Zip) {
    # ZipFile includes hidden resource files and preserves the complete directory layout.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($target, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $true)
    Get-Item -LiteralPath $zipPath | Select-Object FullName,Length
    Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
}
Write-Output "Verified portable package: $target"
Write-Output "Evidence: $checkRoot/installation-check.txt"
