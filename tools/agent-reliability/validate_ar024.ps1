$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force artifacts/ar024 | Out-Null
if(git status --porcelain){throw 'Dirty checkout: AR-024 validation refused'}
$sha=git rev-parse HEAD
@{
 task='AR-024';code_sha=$sha;working_tree='CLEAN';sdk=(dotnet --version);OS=[Environment]::OSVersion.ToString()
 E1='production UI/bridge/tool-session contracts'
 E2='Global Work Assistant + Project Agent -> production adapter/runtime/H2OfficeRuntimeTools -> real OfficeHost process in fixture mode'
 E3='NOT_REQUIRED';E4='DEFERRED_BY_USER / AWAITING_ENVIRONMENT: configured model + real native Office not run'
 E5='DEFERRED_BY_USER';fake_adapter_e4_claim=$false;real_native_office_e4_claim=$false
}|ConvertTo-Json -Depth 8|Set-Content artifacts/ar024/identity.json

dotnet restore H2Notes.Avalonia.slnx 2>&1|Tee-Object artifacts/ar024/restore.log
if($LASTEXITCODE){throw 'Restore failed'}
dotnet build H2Notes.Avalonia.slnx -c Release --no-restore 2>&1|Tee-Object artifacts/ar024/build.log
if($LASTEXITCODE){throw 'Build failed'}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter AR-024 2>&1|Tee-Object artifacts/ar024/ar024-tests.log
$fx=$LASTEXITCODE;$ft=Get-Content artifacts/ar024/ar024-tests.log -Raw
$fm=[regex]::Matches($ft,'RESULT: (\d+) passed, (\d+) failed');$fp=[regex]::Matches($ft,'(?m)^PASS ').Count
$fv=$fx -eq 0 -and $fm.Count -eq 1 -and $fm[0].Groups[2].Value -eq '0' -and [int]$fm[0].Groups[1].Value -eq $fp -and $fp -eq 2
if(!$fv){
 @{task='AR-024';code_sha=$sha;focused_result=($fm.Value -join ';');focused_pass_lines=$fp;E1='FAIL';E2='NOT_RUN_AFTER_FOCUSED_FAILURE';E4='DEFERRED_BY_USER_AWAITING_ENVIRONMENT';clean_end=(!(git status --porcelain))} |
  ConvertTo-Json -Depth 6 | Set-Content artifacts/ar024/validation.json
 throw 'AR-024 focused production slice failed'
}

$office=(Resolve-Path 'experiments/H2AgentLab.OfficeHost/bin/Release/net10.0-windows/H2AgentLab.OfficeHost.exe').Path
dotnet run --project experiments/H2AgentLab/H2AgentLab.csproj -c Release --no-build -- --v2-office-host-test artifacts/ar024/office-host $office 2>&1|Tee-Object artifacts/ar024/office-host.log
$ox=$LASTEXITCODE;$ot=Get-Content artifacts/ar024/office-host.log -Raw
$op=$ot -match 'RESULT: 19 passed, 0 failed'

$failed=@();$retained=@()
foreach($case in @('AR-023','AR-022','AR-021','AR-020','AR-012','AR-001')){
 dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter $case 2>&1|Tee-Object "artifacts/ar024/$case.log"
 $x=$LASTEXITCODE;$t=Get-Content "artifacts/ar024/$case.log" -Raw;$m=[regex]::Matches($t,'RESULT: (\d+) passed, (\d+) failed');$p=[regex]::Matches($t,'(?m)^PASS ').Count
 $v=$x -eq 0 -and $m.Count -eq 1 -and $m[0].Groups[2].Value -eq '0' -and [int]$m[0].Groups[1].Value -eq $p -and $p -gt 0
 $retained+=@{name=$case;exit=$x;result=($m.Value -join ';');pass_lines=$p};if(!$v){$failed+=$case}
}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build 2>&1|Tee-Object artifacts/ar024/full.log
$x=$LASTEXITCODE;$t=Get-Content artifacts/ar024/full.log -Raw;$m=[regex]::Matches($t,'RESULT: (\d+) passed, (\d+) failed');$p=[regex]::Matches($t,'(?m)^PASS ').Count
$full=$x -eq 0 -and $m.Count -eq 1 -and $m[0].Groups[2].Value -eq '0' -and [int]$m[0].Groups[1].Value -eq $p -and $p -gt 0

tools/agent-reliability/run_agent_suites.ps1 -OutputDirectory artifacts/ar024/all-agent-suites
$ax=$LASTEXITCODE;if($ax){$failed+='AGENT-SUITES'}

@{
 task='AR-024';code_sha=$sha;focused_result=($fm.Value -join ';');focused_pass_lines=$fp
 office_host_exit=$ox;office_host_19_pass=$op;retained=$retained;full_result=($m.Value -join ';');full_pass_lines=$p
 agent_suites_exit=$ax;E1=if($fv){'PASS'}else{'FAIL'};E2=if($fv -and $ox -eq 0 -and $op -and $full -and $ax -eq 0){'PASS'}else{'FAIL'}
 E4='DEFERRED_BY_USER_AWAITING_ENVIRONMENT';e4_pass_claim=$false;E5='DEFERRED_BY_USER'
 clean_end=(!(git status --porcelain));external_side_effects='none';personal_documents='not accessed';credentials='not accessed'
}|ConvertTo-Json -Depth 10|Set-Content artifacts/ar024/validation.json
if(!$fv){$failed+='AR-024-FOCUSED'};if($ox -ne 0 -or !$op){$failed+='OFFICE-HOST'};if(!$full){$failed+='FULL'};if(git status --porcelain){$failed+='DIRTY-END'}
if($failed.Count){throw ('AR-024 validation failed: '+($failed -join ', '))}
