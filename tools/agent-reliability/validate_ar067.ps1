$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force artifacts/ar067 | Out-Null
if (git status --porcelain) { throw 'Dirty checkout: validation refused' }
@{
  code_sha=(git rev-parse HEAD)
  working_tree='CLEAN'
  OS=[System.Environment]::OSVersion.ToString()
  sdk=(dotnet --version)
  task='AR-067'
  E1='Typed outcome and bounded recovery logic'
  E2='Scripted AgentRuntime + production adapter + H2 surfaces'
  E3='Not required for AR-067 focused recovery semantics'
  E4='DEFERRED: live model acceptance can be run later; model-unavailable path is scripted'
  network='No external provider calls; no credentials or personal files'
  run=$env:GITHUB_RUN_ID
  attempt=$env:GITHUB_RUN_ATTEMPT
} | ConvertTo-Json -Depth 6 | Set-Content artifacts/ar067/identity.json

dotnet restore H2Notes.Avalonia.slnx 2>&1 | Tee-Object artifacts/ar067/restore.log
if ($LASTEXITCODE) { throw 'Restore failed' }
dotnet build H2Notes.Avalonia.slnx -c Release --no-restore 2>&1 | Tee-Object artifacts/ar067/build.log
if ($LASTEXITCODE) { throw 'Build failed' }

dotnet run --project experiments/H2AgentLab/H2AgentLab.csproj -c Release --no-build -- --mb-repair-runtime-test artifacts/ar067/mb-repair 2>&1 | Tee-Object artifacts/ar067/mb-repair.log
if ($LASTEXITCODE) { throw 'AgentRuntime repair/recovery tests failed' }

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter AR-067 2>&1 | Tee-Object artifacts/ar067/h2-focused.log
if ($LASTEXITCODE) { throw 'AR-067 production/UI focused tests failed' }

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build 2>&1 | Tee-Object artifacts/ar067/h2-full.log
if ($LASTEXITCODE) { throw 'Full H2 regression failed' }

try { & tools/agent-reliability/run_agent_suites.ps1 -OutputDirectory artifacts/ar067/agent-suites; $agent=0 }
catch { $agent=1; Write-Host $_ }
if ($agent -ne 0) { throw 'Agent regression suites failed' }

$focused=Get-Content artifacts/ar067/h2-focused.log -Raw
$full=Get-Content artifacts/ar067/h2-full.log -Raw
$repair=Get-Content artifacts/ar067/mb-repair.log -Raw
@{
  code_sha=(git rev-parse HEAD)
  repair_result=([regex]::Match($repair,'RESULT: \d+ passed, \d+ failed').Value)
  focused_result=([regex]::Match($focused,'RESULT: \d+ passed, \d+ failed').Value)
  full_result=([regex]::Match($full,'RESULT: \d+ passed, \d+ failed').Value)
  generic_host_final_present=($focused -match 'Chưa hoàn thành: công cụ vẫn còn lỗi')
  clean_end=(!(git status --porcelain))
} | ConvertTo-Json -Depth 8 | Set-Content artifacts/ar067/validation.json
if ($focused -match 'Chưa hoàn thành: công cụ vẫn còn lỗi') { throw 'Legacy generic host final leaked into AR-067 focused acceptance' }
