$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force artifacts/ar070 | Out-Null
if (git status --porcelain) { throw 'Dirty checkout: AR-070 validation refused' }
@{
  task='AR-070'
  code_sha=(git rev-parse HEAD)
  working_tree='CLEAN'
  OS=[System.Environment]::OSVersion.ToString()
  sdk=(dotnet --version)
  E1='Typed recovery policy, no-progress guard, target/provenance invariants'
  E2='Concrete AgentRuntime + ToolOutcome + retained production regressions'
  E3='DEFERRED_BY_USER: real search/desktop/provider recovery environment will be tested from final full build'
  E4='DEFERRED_BY_USER: real H2 UI + configured model/provider recovery corpus will be tested from final full build'
  dependency_debt='AR-041/060/061 remain separate unfinished dependencies; uncertain-write/search/desktop live acceptance is not claimed'
  external_side_effects='none'
} | ConvertTo-Json -Depth 8 | Set-Content artifacts/ar070/identity.json

dotnet restore H2Notes.Avalonia.slnx 2>&1 | Tee-Object artifacts/ar070/restore.log
if ($LASTEXITCODE) { throw 'Restore failed' }
dotnet build H2Notes.Avalonia.slnx -c Release --no-restore 2>&1 | Tee-Object artifacts/ar070/build.log
if ($LASTEXITCODE) { throw 'Build failed' }

dotnet run --project experiments/H2AgentLab/H2AgentLab.csproj -c Release --no-build -- --ar070-recovery-policy-test artifacts/ar070/focused 2>&1 | Tee-Object artifacts/ar070/focused.log
if ($LASTEXITCODE) { throw 'AR-070 focused recovery-policy tests failed' }

$failed=@();$results=@()
foreach ($case in @('AR-020','AR-033','AR-066','AR-067','FULL')) {
  $filter=if($case -eq 'FULL'){@()}else{@('--filter',$case)}
  $env:H2_AR040_EVIDENCE_DIR = Join-Path (Resolve-Path artifacts/ar070) ("job-receipts-" + $case)
  dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- @filter 2>&1 | Tee-Object "artifacts/ar070/$case.log"
  $exit=$LASTEXITCODE;$text=Get-Content "artifacts/ar070/$case.log" -Raw
  $match=[regex]::Matches($text,'RESULT: (\d+) passed, (\d+) failed')
  $passLines=[regex]::Matches($text,'(?m)^PASS ').Count
  $valid=$match.Count -eq 1 -and $match[0].Groups[2].Value -eq '0' -and [int]$match[0].Groups[1].Value -eq $passLines -and $passLines -gt 0
  $results+=@{name=$case;exit=$exit;result=($match.Value -join ';');pass_lines=$passLines}
  if($exit -ne 0 -or !$valid){$failed+=$case}
}

try { & tools/agent-reliability/run_agent_suites.ps1 -OutputDirectory artifacts/ar070/agent-suites; $agent=0 }
catch { $agent=1; Write-Host $_ }

$focused=Get-Content artifacts/ar070/focused.log -Raw
$full=Get-Content artifacts/ar070/FULL.log -Raw
@{
  code_sha=(git rev-parse HEAD)
  focused_result=([regex]::Match($focused,'RESULT: \d+ passed, \d+ failed').Value)
  retained=$results
  agent_exit=$agent
  failed=$failed
  legacy_generic_host_final_present=($full -match 'Chưa hoàn thành: công cụ vẫn còn lỗi')
  dependency_debt=@('AR-041','AR-060','AR-061')
  E3='DEFERRED_BY_USER'
  E4='DEFERRED_BY_USER'
  e3_e4_pass_claim=$false
  clean_end=(!(git status --porcelain))
} | ConvertTo-Json -Depth 10 | Set-Content artifacts/ar070/validation.json
if($agent -ne 0 -or $failed.Count -or ($full -match 'Chưa hoàn thành: công cụ vẫn còn lỗi') -or (git status --porcelain)){
  throw 'AR-070 validation failed. Preserve logs; no acceptance upgrade.'
}
