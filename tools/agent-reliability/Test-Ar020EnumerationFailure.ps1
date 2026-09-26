# AR-020 E1 mutation control: remove only the FALSE-result guard while retaining
# today's injection seam. This is old behavior, NOT a checkout of the old runtime.
# No Office application/model is started and nothing is pushed by this script.
param([string]$OutputDirectory = 'artifacts/ar020')
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (git status --porcelain) { throw 'Enumeration control must begin clean' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$source = 'experiments/H2AgentLab.OfficeHost/OfficeNativeWindowProbe.cs'
$path = (Resolve-Path $source).Path
$original = [IO.File]::ReadAllBytes($path)
$hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
$text = [Text.Encoding]::UTF8.GetString($original)
$guard = [regex]'(?m)^            if\(!enumerationSucceeded && !truncated\)\r?\n                return new\(\[\], \[new\("native_object_unavailable",rootHandle\)\],false,roots.Count\);\r?\n'
if ($guard.Matches($text).Count -ne 1) { throw 'Guard changed; re-review the mutation control' }
try {
    [IO.File]::WriteAllText($path, $guard.Replace($text, '', 1), [Text.UTF8Encoding]::new($false))
    @{
        test_source_sha = (git rev-parse HEAD)
        working_tree = 'INTENTIONALLY_DIRTY_MUTATION_CONTROL'
        mutation = 'Remove only EnumWindows FALSE-result guard; keep current injection seam and tests'
        source = $source
        positive_sha256 = $hash.ToLowerInvariant()
        control_sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        native_office = 'NOT_RUN'
    } | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'enumeration-control-identity.json')
    dotnet build H2Notes.Avalonia.slnx -c Release --no-restore 2>&1 | Tee-Object (Join-Path $OutputDirectory 'enumeration-control-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Compile failure is not valid negative evidence' }
    $log = Join-Path $OutputDirectory 'enumeration-control.log'
    dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter 'AR-020 enumeration failure' 2>&1 | Tee-Object $log
    $exit = $LASTEXITCODE
    $observed = Get-Content -LiteralPath $log -Raw
    if ($exit -eq 0 -or $observed -notmatch 'RESULT: 0 passed, 3 failed' -or
        -not $observed.Contains('Failed EnumWindows was reported as a successful empty discovery.') -or
        -not $observed.Contains('A failed root scan became a valid capture or lost its failure class.')) {
        throw 'Expected three semantic failures were not reproduced'
    }
} finally {
    [IO.File]::WriteAllBytes($path, $original)
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $hash) {
        throw 'Exact original checkout bytes were not restored'
    }
    $status = @(git status --porcelain)
    $status | Set-Content (Join-Path $OutputDirectory 'enumeration-control-restored-status.txt')
    if ($status.Count) { throw 'Positive checkout is not clean after mutation control' }
}
Write-Output 'PASS: three expected old-behavior failures; original source restored clean. E1 control only.'
# The next workflow step MUST rebuild the positive source before running its tests.
