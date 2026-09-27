$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force artifacts/ar069 | Out-Null
if (git status --porcelain) { throw 'Dirty checkout: AR-069 validation refused' }

$sha = git rev-parse HEAD
@{
  task='AR-069'
  code_sha=$sha
  working_tree='CLEAN'
  OS=[System.Environment]::OSVersion.ToString()
  sdk=(dotnet --version)
  acceptance_required='E4'
  integration_evidence='E2 exact-SHA critical repair corpus'
  E4='DEFERRED_BY_USER: real H2 UI + authorized OpenAI/native Office/visual acceptance will run from final full build'
  external_side_effects='none'
  user_documents='not accessed'
  credentials='not accessed'
  run=$env:GITHUB_RUN_ID
  attempt=$env:GITHUB_RUN_ATTEMPT
} | ConvertTo-Json -Depth 8 | Set-Content artifacts/ar069/identity.json

python -c "import hashlib,json,pathlib,subprocess,zipfile; ps=subprocess.check_output(['git','ls-files'],text=True).splitlines(); ps=[p for p in ps if pathlib.Path(p).suffix.lower() in ['.cs','.csproj','.props','.targets','.md','.json','.yml','.yaml','.ps1','.slnx','.py']]; z=zipfile.ZipFile('artifacts/ar069/committed-source.zip','w',zipfile.ZIP_DEFLATED); rows=[]; [(z.writestr(p,b),rows.append({'path':p,'sha256':hashlib.sha256(b).hexdigest(),'blob':hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()})) for p in ps for b in [subprocess.check_output(['git','show','HEAD:'+p])]]; z.close(); pathlib.Path('artifacts/ar069/source-receipt.json').write_text(json.dumps(rows,indent=2))"
if ($LASTEXITCODE) { throw 'Source receipt failed' }

dotnet restore H2Notes.Avalonia.slnx 2>&1 | Tee-Object artifacts/ar069/restore.log
if ($LASTEXITCODE) { throw 'Restore failed' }
dotnet build H2Notes.Avalonia.slnx -c Release --no-restore 2>&1 | Tee-Object artifacts/ar069/build.log
if ($LASTEXITCODE) { throw 'Build failed' }

python tools/agent-reliability/test_ar065_wire_control.py 2>&1 | Tee-Object artifacts/ar069/ar065-wire-control.log
$ar065Control=$LASTEXITCODE

$failed=@()
$results=@()
foreach ($case in @('AR-065','AR-066','AR-067','AR-068','H2M-110')) {
  $safe=$case.Replace('/','-')
  $env:H2_AR065_EVIDENCE_DIR = Join-Path (Resolve-Path artifacts/ar069) ("ar065-receipts-" + $safe)
  $env:H2_AR066_EVIDENCE = Join-Path (Resolve-Path artifacts/ar069) ("ar066-receipts-" + $safe)
  $env:H2_AR040_EVIDENCE_DIR = Join-Path (Resolve-Path artifacts/ar069) ("job-receipts-" + $safe)
  dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter $case 2>&1 | Tee-Object "artifacts/ar069/$safe.log"
  $exit=$LASTEXITCODE
  $text=Get-Content "artifacts/ar069/$safe.log" -Raw
  $match=[regex]::Matches($text,'RESULT: (\d+) passed, (\d+) failed')
  $passLines=[regex]::Matches($text,'(?m)^PASS ').Count
  $valid=$match.Count -eq 1 -and $match[0].Groups[2].Value -eq '0' -and [int]$match[0].Groups[1].Value -eq $passLines -and $passLines -gt 0
  $results+=@{name=$case;exit=$exit;result=($match.Value -join ';');pass_lines=$passLines}
  if($exit -ne 0 -or !$valid){$failed+=$case}
}

dotnet run --project experiments/H2AgentLab/H2AgentLab.csproj -c Release --no-build -- --mb-repair-runtime-test artifacts/ar069/ar067-runtime 2>&1 | Tee-Object artifacts/ar069/ar067-runtime.log
$ar067Runtime=$LASTEXITCODE

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build 2>&1 | Tee-Object artifacts/ar069/full.log
$fullExit=$LASTEXITCODE
$fullText=Get-Content artifacts/ar069/full.log -Raw
$fullMatch=[regex]::Matches($fullText,'RESULT: (\d+) passed, (\d+) failed')
$fullPassLines=[regex]::Matches($fullText,'(?m)^PASS ').Count
$fullValid=$fullMatch.Count -eq 1 -and $fullMatch[0].Groups[2].Value -eq '0' -and [int]$fullMatch[0].Groups[1].Value -eq $fullPassLines -and $fullPassLines -gt 0
if($fullExit -ne 0 -or !$fullValid){$failed+='FULL'}

try { & tools/agent-reliability/run_agent_suites.ps1 -OutputDirectory artifacts/ar069/agent-suites; $agent=0 }
catch { $agent=1; Write-Host $_ }

$runtimeText=Get-Content artifacts/ar069/ar067-runtime.log -Raw
$legacyGeneric = $fullText -match 'Chưa hoàn thành: công cụ vẫn còn lỗi'
$matrix=@(
  @{issue='ISSUE-1';repair='AR-065';e2='PASS_IF_GATE_GREEN';real='DEFERRED_BY_USER';missing='real H2 UI + authorized OpenAI GPT-5.6 Luna read-only/tool_search/deferred-tool/file-create sequence'},
  @{issue='ISSUE-2';repair='AR-066';e2='PASS_IF_GATE_GREEN';real='DEFERRED_BY_USER';missing='real H2 UI + native Office live unsaved/multi-window/reconnect corpus; truthful AutoCAD/browser live readiness'},
  @{issue='ISSUE-3';repair='AR-067';e2='PASS_IF_GATE_GREEN';real='DEFERRED_BY_USER';missing='real H2 UI + authorized model/tool recovery and specific blocked-final corpus'},
  @{issue='ISSUE-4';repair='AR-068';e2='PASS_IF_GATE_GREEN';real='DEFERRED_BY_USER';missing='interactive/visual final-build Command Center collapse/acknowledgement acceptance'}
)
@{
  task='AR-069'
  code_sha=$sha
  wire_control_exit=$ar065Control
  ar067_runtime_exit=$ar067Runtime
  ar067_runtime_result=([regex]::Match($runtimeText,'RESULT: \d+ passed, \d+ failed').Value)
  focused=$results
  full_exit=$fullExit
  full_result=($fullMatch.Value -join ';')
  full_pass_lines=$fullPassLines
  agent_exit=$agent
  legacy_generic_host_final_present=$legacyGeneric
  failed=$failed
  matrix=$matrix
  e4='DEFERRED_BY_USER'
  e4_pass_claim=$false
  clean_end=(!(git status --porcelain))
} | ConvertTo-Json -Depth 10 | Set-Content artifacts/ar069/critical-matrix.json

if($ar065Control -ne 0){$failed+='AR-065-WIRE-CONTROL'}
if($ar067Runtime -ne 0){$failed+='AR-067-RUNTIME'}
if($agent -ne 0){$failed+='AGENT-SUITES'}
if($legacyGeneric){$failed+='LEGACY-GENERIC-FINAL'}
if(git status --porcelain){$failed+='DIRTY-END'}
if($failed.Count){
  @{code_sha=$sha;failed=$failed;e4='DEFERRED_BY_USER';e4_pass_claim=$false} | ConvertTo-Json -Depth 8 | Set-Content artifacts/ar069/validation.json
  throw ('AR-069 integration gate failed: ' + ($failed -join ', '))
}
@{code_sha=$sha;result='PASS_E2_INTEGRATION_GATE';focused=$results;full_result=($fullMatch.Value -join ';');agent='PASS';e4='DEFERRED_BY_USER';e4_pass_claim=$false;clean_end=$true} | ConvertTo-Json -Depth 10 | Set-Content artifacts/ar069/validation.json
