$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force artifacts/ar068 | Out-Null
if (git status --porcelain) { throw 'Dirty checkout: AR-068 validation refused' }
@{
  task='AR-068'
  code_sha=(git rev-parse HEAD)
  working_tree='CLEAN'
  OS=[System.Environment]::OSVersion.ToString()
  sdk=(dotnet --version)
  E1='Deterministic projection/local-state and acknowledgement identity tests'
  E2='Headless production Avalonia Command Center + LocalConfiguration persistence'
  E4='DEFERRED_BY_USER: interactive/visual full-build acceptance will run later'
  external_side_effects='none'
} | ConvertTo-Json -Depth 6 | Set-Content artifacts/ar068/identity.json

dotnet restore H2Notes.Avalonia.slnx 2>&1 | Tee-Object artifacts/ar068/restore.log
if ($LASTEXITCODE) { throw 'Restore failed' }
dotnet build H2Notes.Avalonia.slnx -c Release --no-restore 2>&1 | Tee-Object artifacts/ar068/build.log
if ($LASTEXITCODE) { throw 'Build failed' }

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter AR-068 2>&1 | Tee-Object artifacts/ar068/focused.log
if ($LASTEXITCODE) { throw 'AR-068 focused tests failed' }
dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build 2>&1 | Tee-Object artifacts/ar068/full.log
if ($LASTEXITCODE) { throw 'Full H2 regression failed' }

try { & tools/agent-reliability/run_agent_suites.ps1 -OutputDirectory artifacts/ar068/agent-suites; $agent=0 }
catch { $agent=1; Write-Host $_ }
if ($agent -ne 0) { throw 'Agent regression suites failed' }

$focused=Get-Content artifacts/ar068/focused.log -Raw
$full=Get-Content artifacts/ar068/full.log -Raw
@{
  code_sha=(git rev-parse HEAD)
  focused_result=([regex]::Match($focused,'RESULT: \d+ passed, \d+ failed').Value)
  full_result=([regex]::Match($full,'RESULT: \d+ passed, \d+ failed').Value)
  clean_end=(!(git status --porcelain))
} | ConvertTo-Json -Depth 8 | Set-Content artifacts/ar068/validation.json
