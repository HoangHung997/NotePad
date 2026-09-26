$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force artifacts/ar060 | Out-Null
if(git status --porcelain){throw 'Dirty checkout: AR-060 validation refused'}
$sha=git rev-parse HEAD
@{
 task='AR-060';code_sha=$sha;working_tree='CLEAN';sdk=(dotnet --version);OS=[Environment]::OSVersion.ToString()
 E1='Brave search contract, public fetch redirect/body policy, browser tab/action contracts, prompt-injection/untrusted-data markers'
 E2='production WebResearch composition with configurable Brave search and local-CDP browser provider; deterministic HTTP/browser fixtures'
 E3='DEFERRED_BY_USER_AWAITING_ENVIRONMENT: live Brave credential/search and real browser interaction not run'
 E4='DEFERRED_BY_USER_AWAITING_ENVIRONMENT: H2 UI + configured model + live web/browser not run'
 E5='DEFERRED_BY_USER';browser_action_claim=$false;live_search_claim=$false
}|ConvertTo-Json -Depth 8|Set-Content artifacts/ar060/identity.json

dotnet restore H2Notes.Avalonia.slnx 2>&1|Tee-Object artifacts/ar060/restore.log
if($LASTEXITCODE){throw 'Restore failed'}
dotnet build H2Notes.Avalonia.slnx -c Release --no-restore 2>&1|Tee-Object artifacts/ar060/build.log
if($LASTEXITCODE){throw 'Build failed'}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter AR-060 2>&1|Tee-Object artifacts/ar060/ar060-tests.log
$fx=$LASTEXITCODE;$ft=Get-Content artifacts/ar060/ar060-tests.log -Raw
$fm=[regex]::Matches($ft,'RESULT: (\d+) passed, (\d+) failed');$fp=[regex]::Matches($ft,'(?m)^PASS ').Count
$fv=$fx -eq 0 -and $fm.Count -eq 1 -and $fm[0].Groups[2].Value -eq '0' -and [int]$fm[0].Groups[1].Value -eq $fp -and $fp -eq 6
if(!$fv){
 @{task='AR-060';code_sha=$sha;focused_result=($fm.Value -join ';');focused_pass_lines=$fp;E1='FAIL';E2='NOT_RUN_AFTER_FOCUSED_FAILURE';E3='DEFERRED_BY_USER_AWAITING_ENVIRONMENT';E4='DEFERRED_BY_USER_AWAITING_ENVIRONMENT';clean_end=(!(git status --porcelain))} |
  ConvertTo-Json -Depth 6|Set-Content artifacts/ar060/validation.json
 throw 'AR-060 focused tests failed'
}

$failed=@();$retained=@()
foreach($case in @('AR-052','AR-042','AR-041','AR-024')){
 dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter $case 2>&1|Tee-Object "artifacts/ar060/$case.log"
 $x=$LASTEXITCODE;$t=Get-Content "artifacts/ar060/$case.log" -Raw;$m=[regex]::Matches($t,'RESULT: (\d+) passed, (\d+) failed');$p=[regex]::Matches($t,'(?m)^PASS ').Count
 $v=$x -eq 0 -and $m.Count -eq 1 -and $m[0].Groups[2].Value -eq '0' -and [int]$m[0].Groups[1].Value -eq $p -and $p -gt 0
 $retained+=@{name=$case;exit=$x;result=($m.Value -join ';');pass_lines=$p};if(!$v){$failed+=$case}
}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build 2>&1|Tee-Object artifacts/ar060/full.log
$x=$LASTEXITCODE;$t=Get-Content artifacts/ar060/full.log -Raw;$m=[regex]::Matches($t,'RESULT: (\d+) passed, (\d+) failed');$p=[regex]::Matches($t,'(?m)^PASS ').Count
$full=$x -eq 0 -and $m.Count -eq 1 -and $m[0].Groups[2].Value -eq '0' -and [int]$m[0].Groups[1].Value -eq $p -and $p -gt 0

tools/agent-reliability/run_agent_suites.ps1 -OutputDirectory artifacts/ar060/all-agent-suites
$ax=$LASTEXITCODE;if($ax){$failed+='AGENT-SUITES'}

@{
 task='AR-060';code_sha=$sha;focused_result=($fm.Value -join ';');focused_pass_lines=$fp
 retained=$retained;full_result=($m.Value -join ';');full_pass_lines=$p;agent_suites_exit=$ax
 E1='PASS';E2=if($full -and $ax -eq 0 -and $failed.Count -eq 0){'PASS'}else{'FAIL'}
 E3='DEFERRED_BY_USER_AWAITING_ENVIRONMENT';E4='DEFERRED_BY_USER_AWAITING_ENVIRONMENT';E5='DEFERRED_BY_USER'
 live_search_claim=$false;live_browser_claim=$false;external_form_submission_claim=$false
 clean_end=(!(git status --porcelain));personal_documents='not accessed';credentials='not accessed';external_side_effects='none'
}|ConvertTo-Json -Depth 10|Set-Content artifacts/ar060/validation.json
if(!$full){$failed+='FULL'};if(git status --porcelain){$failed+='DIRTY-END'}
if($failed.Count){throw ('AR-060 validation failed: '+($failed -join ', '))}
