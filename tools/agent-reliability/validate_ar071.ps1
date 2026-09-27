$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force artifacts/ar071 | Out-Null
if(git status --porcelain){throw 'Dirty checkout: AR-071 validation refused'}
$sha=git rev-parse HEAD
@{
 task='AR-071';code_sha=$sha;working_tree='CLEAN';sdk=(dotnet --version);OS=[Environment]::OSVersion.ToString()
 E1='task-local PluginManager trial staging, cache probe and fail-closed rollback contracts'
 E2='real ZIP package admission + SafeWorkspace copy/diff/readback through AdapterTrialManager'
 E3='DEFERRED_BY_USER_AWAITING_ENVIRONMENT: no real third-party app/version adapter trial run'
 E4='NOT_REQUIRED';E5='DEFERRED_BY_USER';auto_promotion=$false;second_catalog=$false
}|ConvertTo-Json -Depth 8|Set-Content artifacts/ar071/identity.json

dotnet restore H2Notes.Avalonia.slnx 2>&1|Tee-Object artifacts/ar071/restore.log
if($LASTEXITCODE){throw 'Restore failed'}
dotnet build H2Notes.Avalonia.slnx -c Release --no-restore 2>&1|Tee-Object artifacts/ar071/build.log
if($LASTEXITCODE){throw 'Build failed'}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter AR-071 2>&1|Tee-Object artifacts/ar071/ar071-tests.log
$fx=$LASTEXITCODE;$ft=Get-Content artifacts/ar071/ar071-tests.log -Raw
$fm=[regex]::Matches($ft,'RESULT: (\d+) passed, (\d+) failed');$fp=[regex]::Matches($ft,'(?m)^PASS ').Count
$fv=$fx -eq 0 -and $fm.Count -eq 1 -and $fm[0].Groups[2].Value -eq '0' -and [int]$fm[0].Groups[1].Value -eq $fp -and $fp -eq 6
if(!$fv){
 @{task='AR-071';code_sha=$sha;focused_result=($fm.Value -join ';');focused_pass_lines=$fp;E1='FAIL';E2='NOT_RUN_AFTER_FOCUSED_FAILURE';E3='DEFERRED_BY_USER_AWAITING_ENVIRONMENT';clean_end=(!(git status --porcelain))} |
 ConvertTo-Json -Depth 6|Set-Content artifacts/ar071/validation.json
 throw 'AR-071 focused tests failed'
}

$failed=@();$retained=@()
# AR-070 is an independent Agent suite (--ar070-recovery-policy-test), not an H2Notes.Tests name filter.
# It is validated by run_agent_suites.ps1 below.
foreach($case in @('AR-064','AR-082','AR-042','AR-041')){
 dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter $case 2>&1|Tee-Object "artifacts/ar071/$case.log"
 $x=$LASTEXITCODE;$t=Get-Content "artifacts/ar071/$case.log" -Raw;$m=[regex]::Matches($t,'RESULT: (\d+) passed, (\d+) failed');$p=[regex]::Matches($t,'(?m)^PASS ').Count
 $v=$x -eq 0 -and $m.Count -eq 1 -and $m[0].Groups[2].Value -eq '0' -and [int]$m[0].Groups[1].Value -eq $p -and $p -gt 0
 $retained+=@{name=$case;exit=$x;result=($m.Value -join ';');pass_lines=$p};if(!$v){$failed+=$case}
}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build 2>&1|Tee-Object artifacts/ar071/full.log
$x=$LASTEXITCODE;$t=Get-Content artifacts/ar071/full.log -Raw;$m=[regex]::Matches($t,'RESULT: (\d+) passed, (\d+) failed');$p=[regex]::Matches($t,'(?m)^PASS ').Count
$full=$x -eq 0 -and $m.Count -eq 1 -and $m[0].Groups[2].Value -eq '0' -and [int]$m[0].Groups[1].Value -eq $p -and $p -gt 0

tools/agent-reliability/run_agent_suites.ps1 -OutputDirectory artifacts/ar071/all-agent-suites
$ax=$LASTEXITCODE;if($ax){$failed+='AGENT-SUITES'}

@{
 task='AR-071';code_sha=$sha;focused_result=($fm.Value -join ';');focused_pass_lines=$fp
 retained=$retained;full_result=($m.Value -join ';');full_pass_lines=$p;agent_suites_exit=$ax;ar070_via_required_agent_suites=($ax -eq 0)
 E1='PASS';E2=if($full -and $ax -eq 0 -and $failed.Count -eq 0){'PASS'}else{'FAIL'}
 E3='DEFERRED_BY_USER_AWAITING_ENVIRONMENT';E4='NOT_RUN';E5='DEFERRED_BY_USER'
 auto_promotion=$false;second_catalog=$false;clean_end=(!(git status --porcelain))
}|ConvertTo-Json -Depth 10|Set-Content artifacts/ar071/validation.json
if(!$full){$failed+='FULL'};if(git status --porcelain){$failed+='DIRTY-END'}
if($failed.Count){throw ('AR-071 validation failed: '+($failed -join ', '))}
