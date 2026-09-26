$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force artifacts/ar090 | Out-Null
if(git status --porcelain){throw 'Dirty checkout: AR-090 validation refused'}
$sha=git rev-parse HEAD
@{
 task='AR-090';code_sha=$sha;working_tree='CLEAN';sdk=(dotnet --version);OS=[Environment]::OSVersion.ToString()
 audit='final parity/status/evidence/package handoff'
 E5='DEFERRED_BY_USER';project_complete_claim=$false;implementation_ready_label=$true
}|ConvertTo-Json -Depth 6|Set-Content artifacts/ar090/identity.json

dotnet restore H2Notes.Avalonia.slnx 2>&1|Tee-Object artifacts/ar090/restore.log
if($LASTEXITCODE){throw 'Restore failed'}
dotnet build H2Notes.Avalonia.slnx -c Release --no-restore 2>&1|Tee-Object artifacts/ar090/build.log
if($LASTEXITCODE){throw 'Build failed'}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter AR-090 2>&1|Tee-Object artifacts/ar090/ar090-tests.log
$fx=$LASTEXITCODE;$ft=Get-Content artifacts/ar090/ar090-tests.log -Raw
$fm=[regex]::Matches($ft,'RESULT: (\d+) passed, (\d+) failed');$fp=[regex]::Matches($ft,'(?m)^PASS ').Count
$focused=$fx -eq 0 -and $fm.Count -eq 1 -and $fm[0].Groups[2].Value -eq '0' -and [int]$fm[0].Groups[1].Value -eq $fp -and $fp -eq 7
if(!$focused){
 @{task='AR-090';code_sha=$sha;focused_result=($fm.Value -join ';');focused_pass_lines=$fp;audit='FAIL';clean_end=(!(git status --porcelain))}|
  ConvertTo-Json -Depth 6|Set-Content artifacts/ar090/validation.json
 throw 'AR-090 focused audit failed'
}

dotnet run --project experiments/H2AgentLab/H2AgentLab.csproj -c Release --no-build -- --mb-final-architecture-report-test artifacts/ar090/mb120-history 2>&1|Tee-Object artifacts/ar090/mb120-history.log
$mb=$LASTEXITCODE
if($mb){throw 'Historical MB-120 compatibility report guard failed'}

$failed=@();$retained=@()
foreach($case in @('AR-082','AR-081','AR-080','AR-042','AR-041','AR-062','AR-060')){
 dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter $case 2>&1|Tee-Object "artifacts/ar090/$case.log"
 $x=$LASTEXITCODE;$t=Get-Content "artifacts/ar090/$case.log" -Raw;$m=[regex]::Matches($t,'RESULT: (\d+) passed, (\d+) failed');$p=[regex]::Matches($t,'(?m)^PASS ').Count
 $v=$x -eq 0 -and $m.Count -eq 1 -and $m[0].Groups[2].Value -eq '0' -and [int]$m[0].Groups[1].Value -eq $p -and $p -gt 0
 $retained+=@{name=$case;exit=$x;result=($m.Value -join ';');pass_lines=$p};if(!$v){$failed+=$case}
}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build 2>&1|Tee-Object artifacts/ar090/full.log
$x=$LASTEXITCODE;$t=Get-Content artifacts/ar090/full.log -Raw;$m=[regex]::Matches($t,'RESULT: (\d+) passed, (\d+) failed');$p=[regex]::Matches($t,'(?m)^PASS ').Count
$full=$x -eq 0 -and $m.Count -eq 1 -and $m[0].Groups[2].Value -eq '0' -and [int]$m[0].Groups[1].Value -eq $p -and $p -gt 0

tools/agent-reliability/run_agent_suites.ps1 -OutputDirectory artifacts/ar090/all-agent-suites
$ax=$LASTEXITCODE;if($ax){$failed+='AGENT-SUITES'}

@{
 task='AR-090';code_sha=$sha;focused_result=($fm.Value -join ';');focused_pass_lines=$fp
 historical_mb120_exit=$mb;retained=$retained;full_result=($m.Value -join ';');full_pass_lines=$p
 agent_suites_exit=$ax;audit=if($focused -and $mb -eq 0 -and $full -and $ax -eq 0 -and $failed.Count -eq 0){'PASS'}else{'FAIL'}
 handoff_label='IMPLEMENTATION_READY_FOR_USER_TEST'
 E5='DEFERRED_BY_USER';project_complete_claim=$false;multi_pc_claim=$false
 native_e4_pass_claim=$false;optional_ar071_ar072_selected=$false
 clean_end=(!(git status --porcelain));external_side_effects='none';personal_documents='not accessed';credentials='not accessed'
}|ConvertTo-Json -Depth 10|Set-Content artifacts/ar090/validation.json

if(!$full){$failed+='FULL'};if(git status --porcelain){$failed+='DIRTY-END'}
if($failed.Count){throw ('AR-090 validation failed: '+($failed -join ', '))}
