param([string]$OutputDirectory = "artifacts/ar001/all-agent-suites")
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$workflow = Get-Content .github/workflows/avalonia-ci.yml -Raw
# Reuse each actual Agent suite command from the existing required workflow.
$matches = [regex]::Matches($workflow, 'dotnet run --project \.\\experiments\\H2AgentLab\\H2AgentLab.csproj -c Release --no-build -- (?<flag>--[a-z0-9-]+) \$out')
$flags = @($matches | ForEach-Object { $_.Groups['flag'].Value } | Select-Object -Unique)
if ($flags.Count -lt 60 -or $flags -notcontains '--mb-scheduler-runtime-test' -or $flags -notcontains '--v2-provider-resilience-test') { throw 'Incomplete workflow suite inventory.' }
$results = @()
foreach ($flag in $flags) {
    $name = $flag.TrimStart('-')
    $directory = Join-Path $OutputDirectory $name
    $log = Join-Path $OutputDirectory ($name + '.log')
    Write-Host "Running independent required suite $flag"
    & dotnet run --project experiments/H2AgentLab/H2AgentLab.csproj -c Release --no-build -- $flag $directory 2>&1 | Tee-Object -FilePath $log
    $exit = $LASTEXITCODE
    $results += @{flag=$flag; exit_code=$exit; log=$log}
}
@{code_sha=(git rev-parse HEAD); evidence='E1/E2 fixtures only'; suites=$results} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputDirectory 'suite-results.json')
$failures = @($results | Where-Object {$_.exit_code -ne 0})
if ($failures.Count -gt 0) { throw ('Required suites failed: ' + (($failures | ForEach-Object {$_.flag}) -join ', ')) }
Write-Host "All $($flags.Count) independently invoked required Agent suites passed. This does not replace full CI/publish."
