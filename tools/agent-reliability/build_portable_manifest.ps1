param(
    [Parameter(Mandatory=$true)][string]$Root,
    [Parameter(Mandatory=$true)][string]$SourceSha,
    [string]$SourceBranch = "",
    [string]$RuntimeIdentifier = "win-x64"
)
$ErrorActionPreference = "Stop"
$rootPath = (Resolve-Path -LiteralPath $Root).Path
if ($SourceSha -notmatch '^[0-9a-fA-F]{40}$') { throw "SourceSha must be an exact 40-character Git SHA." }
$manifestPath = Join-Path $rootPath "portable-manifest.json"
if (Test-Path -LiteralPath $manifestPath) { Remove-Item -LiteralPath $manifestPath -Force }
$files = Get-ChildItem -LiteralPath $rootPath -File -Recurse
$byPath = @{}
foreach ($file in $files) {
  $relative = [IO.Path]::GetRelativePath($rootPath, $file.FullName).Replace('\','/')
  if ($relative -eq 'portable-manifest.json') { continue }
  if ($relative.StartsWith('../') -or [IO.Path]::IsPathRooted($relative)) { throw 'Package path escaped root.' }
  $byPath[$relative] = $file
}
$paths = [string[]]$byPath.Keys
[Array]::Sort($paths, [StringComparer]::OrdinalIgnoreCase)
$entries = [Collections.Generic.List[object]]::new()
$canonical = [Text.StringBuilder]::new()
[long]$total = 0
foreach ($relative in $paths) {
  $file = $byPath[$relative]
  $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  [long]$bytes = $file.Length
  $total = $total + $bytes
  $entries.Add([ordered]@{ path=$relative; bytes=$bytes; sha256=$hash })
  [void]$canonical.Append($relative).Append([char]10).Append($bytes.ToString([Globalization.CultureInfo]::InvariantCulture)).Append([char]10).Append($hash).Append([char]10)
}
$sha = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical.ToString()))
$contentSha = [Convert]::ToHexString($sha).ToLowerInvariant()
$app = Join-Path $rootPath 'H2Notes.Avalonia.exe'
if (-not (Test-Path -LiteralPath $app)) { throw 'Published package has no H2Notes.Avalonia.exe.' }
$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($app).ProductVersion
if ([string]::IsNullOrWhiteSpace($productVersion)) { $productVersion = 'unknown' }
$manifest = [ordered]@{ schemaVersion=1; sourceSha=$SourceSha.ToLowerInvariant(); sourceBranch=$SourceBranch; runtimeIdentifier=$RuntimeIdentifier; selfContained=$true; productVersion=$productVersion; fileCount=$entries.Count; totalBytes=$total; contentSha256=$contentSha; files=$entries }
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
Write-Host "Portable manifest: files=$($entries.Count) bytes=$total contentSha256=$contentSha source=$SourceSha"