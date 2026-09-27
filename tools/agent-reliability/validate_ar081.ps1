$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force artifacts/ar081 | Out-Null
if(git status --porcelain){throw 'Dirty checkout: AR-081 validation refused'}
$sha=git rev-parse HEAD
@{
 task='AR-081';code_sha=$sha;working_tree='CLEAN';sdk=(dotnet --version);OS=[Environment]::OSVersion.ToString()
 E1='typed UI reliability projection, dedup, lazy/bounded controls and accessibility metadata'
 E2='Avalonia headless product-surface rendering on canonical AgentChatSurface/turn/artifact/approval controls'
 E3='NOT_REQUIRED';E4='DEFERRED_BY_USER_AWAITING_ENVIRONMENT: native DPI/IME/keyboard/screen-reader interaction not run'
 E5='DEFERRED_BY_USER';native_ui_claim=$false;private_chain_of_thought_claim=$false
}|ConvertTo-Json -Depth 8|Set-Content artifacts/ar081/identity.json

dotnet restore H2Notes.Avalonia.slnx 2>&1|Tee-Object artifacts/ar081/restore.log
if($LASTEXITCODE){throw 'Restore failed'}
dotnet build H2Notes.Avalonia.slnx -c Release --no-restore 2>&1|Tee-Object artifacts/ar081/build.log
if($LASTEXITCODE){throw 'Build failed'}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter AR-081 2>&1|Tee-Object artifacts/ar081/ar081-tests.log
$fx=$LASTEXITCODE;$ft=Get-Content artifacts/ar081/ar081-tests.log -Raw
$fm=[regex]::Matches($ft,'RESULT: (\d+) passed, (\d+) failed');$fp=[regex]::Matches($ft,'(?m)^PASS ').Count
$fv=$fx -eq 0 -and $fm.Count -eq 1 -and $fm[0].Groups[2].Value -eq '0' -and [int]$fm[0].Groups[1].Value -eq $fp -and $fp -eq 6
if(!$fv){
 @{task='AR-081';code_sha=$sha;focused_result=($fm.Value -join ';');focused_pass_lines=$fp;E1='FAIL';E2='NOT_RUN_AFTER_FOCUSED_FAILURE';E4='DEFERRED_BY_USER_AWAITING_ENVIRONMENT';clean_end=(!(git status --porcelain))} |
  ConvertTo-Json -Depth 6|Set-Content artifacts/ar081/validation.json
 throw 'AR-081 focused UI reliability tests failed'
}

$failed=@();$retained=@()
foreach($case in @('AR-080','AR-042','AR-041','AR-068','H2M-132')){
 dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter $case 2>&1|Tee-Object "artifacts/ar081/$case.log"
 $x=$LASTEXITCODE;$t=Get-Content "artifacts/ar081/$case.log" -Raw;$m=[regex]::Matches($t,'RESULT: (\d+) passed, (\d+) failed');$pass=[regex]::Matches($t,'(?m)^PASS ').Count
 $v=$x -eq 0 -and $m.Count -eq 1 -and $m[0].Groups[2].Value -eq '0' -and [int]$m[0].Groups[1].Value -eq $pass -and $pass -gt 0
 $retained+=@{name=$case;exit=$x;result=($m.Value -join ';');pass_lines=$pass};if(!$v){$failed+=$case}
}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build 2>&1|Tee-Object artifacts/ar081/full.log
$x=$LASTEXITCODE;$t=Get-Content artifacts/ar081/full.log -Raw;$m=[regex]::Matches($t,'RESULT: (\d+) passed, (\d+) failed');$pass=[regex]::Matches($t,'(?m)^PASS ').Count
$full=$x -eq 0 -and $m.Count -eq 1 -and $m[0].Groups[2].Value -eq '0' -and [int]$m[0].Groups[1].Value -eq $pass -and $pass -gt 0

tools/agent-reliability/run_agent_suites.ps1 -OutputDirectory artifacts/ar081/all-agent-suites
$ax=$LASTEXITCODE;if($ax){$failed+='AGENT-SUITES'}

@{
 task='AR-081';code_sha=$sha;focused_result=($fm.Value -join ';');focused_pass_lines=$fp
 retained=$retained;full_result=($m.Value -join ';');full_pass_lines=$pass;agent_suites_exit=$ax
 E1='PASS';E2=if($full -and $ax -eq 0 -and $failed.Count -eq 0){'PASS'}else{'FAIL'}
 E4='DEFERRED_BY_USER_AWAITING_ENVIRONMENT';native_ui_claim=$false;private_chain_of_thought_claim=$false
 clean_end=(!(git status --porcelain));external_side_effects='none';personal_documents='not accessed';credentials='not accessed'
}|ConvertTo-Json -Depth 10|Set-Content artifacts/ar081/validation.json
if(!$full){$failed+='FULL'};if(git status --porcelain){$failed+='DIRTY-END'}
if($failed.Count){throw ('AR-081 validation failed: '+($failed -join ', '))}
