$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force artifacts/ar063 | Out-Null
if(git status --porcelain){throw 'Dirty checkout: AR-063 validation refused'}
$sha=git rev-parse HEAD
@{
 task='AR-063';code_sha=$sha;working_tree='CLEAN';sdk=(dotnet --version);OS=[Environment]::OSVersion.ToString()
 E1='closed/live CAD operation matrix + external COM selected-entity/attribute contracts'
 E2='production Work Assistant -> AgentRuntime -> live CAD bridge fixture + verifier; MB-113 typed bridge retained'
 E3='DEFERRED_BY_USER_AWAITING_ENVIRONMENT';E4='DEFERRED_BY_USER_AWAITING_ENVIRONMENT';E5='DEFERRED_BY_USER'
 live_native_claim=$false;general_dynamic_block_claim=$false;plot_claim=$false
}|ConvertTo-Json -Depth 8|Set-Content artifacts/ar063/identity.json

dotnet restore H2Notes.Avalonia.slnx 2>&1|Tee-Object artifacts/ar063/restore.log
if($LASTEXITCODE){throw 'Restore failed'}
dotnet build H2Notes.Avalonia.slnx -c Release --no-restore 2>&1|Tee-Object artifacts/ar063/build.log
if($LASTEXITCODE){throw 'Build failed'}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter AR-063 2>&1|Tee-Object artifacts/ar063/ar063-tests.log
$fx=$LASTEXITCODE;$ft=Get-Content artifacts/ar063/ar063-tests.log -Raw
$fm=[regex]::Matches($ft,'RESULT: (\d+) passed, (\d+) failed');$fp=[regex]::Matches($ft,'(?m)^PASS ').Count
$fv=$fx -eq 0 -and $fm.Count -eq 1 -and $fm[0].Groups[2].Value -eq '0' -and [int]$fm[0].Groups[1].Value -eq $fp -and $fp -eq 5
if(!$fv){
 @{task='AR-063';code_sha=$sha;focused_result=($fm.Value -join ';');focused_pass_lines=$fp;E1='FAIL';E2='NOT_RUN_AFTER_FOCUSED_FAILURE';clean_end=(!(git status --porcelain))} |
 ConvertTo-Json -Depth 6|Set-Content artifacts/ar063/validation.json
 throw 'AR-063 focused tests failed'
}

$out=Join-Path (Resolve-Path artifacts/ar063) 'mb113'
dotnet run --project experiments/H2AgentLab/H2AgentLab.csproj -c Release --no-build -- --mb-autocad-acceptance-test $out 2>&1|Tee-Object artifacts/ar063/mb113.log
$mx=$LASTEXITCODE;$mt=Get-Content artifacts/ar063/mb113.log -Raw
$mm=[regex]::Matches($mt,'RESULT: (\d+) passed, (\d+) failed');$mp=[regex]::Matches($mt,'(?m)^PASS ').Count
$mv=$mx -eq 0 -and $mm.Count -eq 1 -and $mm[0].Groups[2].Value -eq '0' -and [int]$mm[0].Groups[1].Value -eq $mp -and $mp -eq 6

$failed=@();$retained=@()
foreach($case in @('AR-062','AR-060','AR-024','AR-033','AR-012')){
 dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter $case 2>&1|Tee-Object "artifacts/ar063/$case.log"
 $x=$LASTEXITCODE;$t=Get-Content "artifacts/ar063/$case.log" -Raw;$m=[regex]::Matches($t,'RESULT: (\d+) passed, (\d+) failed');$p=[regex]::Matches($t,'(?m)^PASS ').Count
 $v=$x -eq 0 -and $m.Count -eq 1 -and $m[0].Groups[2].Value -eq '0' -and [int]$m[0].Groups[1].Value -eq $p -and $p -gt 0
 $retained+=@{name=$case;exit=$x;result=($m.Value -join ';');pass_lines=$p};if(!$v){$failed+=$case}
}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build 2>&1|Tee-Object artifacts/ar063/full.log
$x=$LASTEXITCODE;$t=Get-Content artifacts/ar063/full.log -Raw;$m=[regex]::Matches($t,'RESULT: (\d+) passed, (\d+) failed');$p=[regex]::Matches($t,'(?m)^PASS ').Count
$full=$x -eq 0 -and $m.Count -eq 1 -and $m[0].Groups[2].Value -eq '0' -and [int]$m[0].Groups[1].Value -eq $p -and $p -gt 0

tools/agent-reliability/run_agent_suites.ps1 -OutputDirectory artifacts/ar063/all-agent-suites
$ax=$LASTEXITCODE;if($ax){$failed+='AGENT-SUITES'}

@{
 task='AR-063';code_sha=$sha;focused_result=($fm.Value -join ';');focused_pass_lines=$fp
 mb113_result=($mm.Value -join ';');mb113_pass_lines=$mp;retained=$retained
 full_result=($m.Value -join ';');full_pass_lines=$p;agent_suites_exit=$ax
 E1='PASS';E2=if($mv -and $full -and $ax -eq 0 -and $failed.Count -eq 0){'PASS'}else{'FAIL'}
 E3='DEFERRED_BY_USER_AWAITING_ENVIRONMENT';E4='DEFERRED_BY_USER_AWAITING_ENVIRONMENT';E5='DEFERRED_BY_USER'
 live_native_claim=$false;general_dynamic_block_claim=$false;plot_claim=$false
 clean_end=(!(git status --porcelain));external_side_effects='none';personal_documents='not accessed';credentials='not accessed'
}|ConvertTo-Json -Depth 10|Set-Content artifacts/ar063/validation.json
if(!$mv){$failed+='MB-113'};if(!$full){$failed+='FULL'};if(git status --porcelain){$failed+='DIRTY-END'}
if($failed.Count){throw ('AR-063 validation failed: '+($failed -join ', '))}
