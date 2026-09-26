$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force artifacts/ar041 | Out-Null
if(git status --porcelain){throw 'Dirty checkout: AR-041 validation refused'}
$sha=git rev-parse HEAD
@{
 task='AR-041';code_sha=$sha;working_tree='CLEAN';sdk=(dotnet --version);OS=[Environment]::OSVersion.ToString()
 E1='restart reconciliation state machine, exact resource rebind, idempotency and fresh-permission contract'
 E2='durable local Agent journal reload + production adapter reconciliation over isolated restart fixtures'
 E3='DEFERRED_BY_USER / AWAITING_ENVIRONMENT: real native Office/GUI crash-restart acceptance not run'
 E4='NOT_REQUIRED';E5='DEFERRED_BY_USER';replay_claim=$false;exactly_once_claim=$false
}|ConvertTo-Json -Depth 8|Set-Content artifacts/ar041/identity.json

dotnet restore H2Notes.Avalonia.slnx 2>&1|Tee-Object artifacts/ar041/restore.log
if($LASTEXITCODE){throw 'Restore failed'}
dotnet build H2Notes.Avalonia.slnx -c Release --no-restore 2>&1|Tee-Object artifacts/ar041/build.log
if($LASTEXITCODE){throw 'Build failed'}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter AR-041 2>&1|Tee-Object artifacts/ar041/ar041-tests.log
$fx=$LASTEXITCODE;$ft=Get-Content artifacts/ar041/ar041-tests.log -Raw
$fm=[regex]::Matches($ft,'RESULT: (\d+) passed, (\d+) failed');$fp=[regex]::Matches($ft,'(?m)^PASS ').Count
$fv=$fx -eq 0 -and $fm.Count -eq 1 -and $fm[0].Groups[2].Value -eq '0' -and [int]$fm[0].Groups[1].Value -eq $fp -and $fp -eq 6
if(!$fv){
 @{task='AR-041';code_sha=$sha;focused_result=($fm.Value -join ';');focused_pass_lines=$fp;E1='FAIL';E2='NOT_RUN_AFTER_FOCUSED_FAILURE';E3='DEFERRED_BY_USER_AWAITING_ENVIRONMENT';clean_end=(!(git status --porcelain))} |
  ConvertTo-Json -Depth 6|Set-Content artifacts/ar041/validation.json
 throw 'AR-041 focused restart-reconcile tests failed'
}

$failed=@();$retained=@()
foreach($case in @('AR-031','AR-040','AR-024','AR-033','AR-022')){
 dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter $case 2>&1|Tee-Object "artifacts/ar041/$case.log"
 $x=$LASTEXITCODE;$t=Get-Content "artifacts/ar041/$case.log" -Raw;$m=[regex]::Matches($t,'RESULT: (\d+) passed, (\d+) failed');$p=[regex]::Matches($t,'(?m)^PASS ').Count
 $v=$x -eq 0 -and $m.Count -eq 1 -and $m[0].Groups[2].Value -eq '0' -and [int]$m[0].Groups[1].Value -eq $p -and $p -gt 0
 $retained+=@{name=$case;exit=$x;result=($m.Value -join ';');pass_lines=$p};if(!$v){$failed+=$case}
}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build 2>&1|Tee-Object artifacts/ar041/full.log
$x=$LASTEXITCODE;$t=Get-Content artifacts/ar041/full.log -Raw;$m=[regex]::Matches($t,'RESULT: (\d+) passed, (\d+) failed');$p=[regex]::Matches($t,'(?m)^PASS ').Count
$full=$x -eq 0 -and $m.Count -eq 1 -and $m[0].Groups[2].Value -eq '0' -and [int]$m[0].Groups[1].Value -eq $p -and $p -gt 0

tools/agent-reliability/run_agent_suites.ps1 -OutputDirectory artifacts/ar041/all-agent-suites
$ax=$LASTEXITCODE;if($ax){$failed+='AGENT-SUITES'}

@{
 task='AR-041';code_sha=$sha;focused_result=($fm.Value -join ';');focused_pass_lines=$fp
 retained=$retained;full_result=($m.Value -join ';');full_pass_lines=$p;agent_suites_exit=$ax
 E1='PASS';E2=if($full -and $ax -eq 0 -and $failed.Count -eq 0){'PASS'}else{'FAIL'}
 E3='DEFERRED_BY_USER_AWAITING_ENVIRONMENT';E4='NOT_RUN';E5='DEFERRED_BY_USER'
 replay_claim=$false;exactly_once_claim=$false;fresh_permission_required=$true
 clean_end=(!(git status --porcelain));external_side_effects='none';personal_documents='not accessed';credentials='not accessed'
}|ConvertTo-Json -Depth 10|Set-Content artifacts/ar041/validation.json
if(!$full){$failed+='FULL'};if(git status --porcelain){$failed+='DIRTY-END'}
if($failed.Count){throw ('AR-041 validation failed: '+($failed -join ', '))}
