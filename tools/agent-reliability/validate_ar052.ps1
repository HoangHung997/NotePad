$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force artifacts/ar052 | Out-Null
if(git status --porcelain){throw 'Dirty checkout: AR-052 validation refused'}
$sha=git rev-parse HEAD
@{
 task='AR-052';code_sha=$sha;working_tree='CLEAN';sdk=(dotnet --version);OS=[Environment]::OSVersion.ToString()
 E1='canonical goal/evidence rehydration and resume identity contracts'
 E2='same-TaskId fresh-turn protocol matrix over Ollama/Chat/Responses with durable archive restart fixtures'
 E3='NOT_REQUIRED'
 E4='DEFERRED_BY_USER_AWAITING_ENVIRONMENT: real configured model/provider combinations not run'
 E5='DEFERRED_BY_USER'
 old_provider_continuation_reused=$false;auto_provider_fallback=$false;mutation_replay_claim=$false
}|ConvertTo-Json -Depth 8|Set-Content artifacts/ar052/identity.json

dotnet restore H2Notes.Avalonia.slnx 2>&1|Tee-Object artifacts/ar052/restore.log
if($LASTEXITCODE){throw 'Restore failed'}
dotnet build H2Notes.Avalonia.slnx -c Release --no-restore 2>&1|Tee-Object artifacts/ar052/build.log
if($LASTEXITCODE){throw 'Build failed'}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter AR-052 2>&1|Tee-Object artifacts/ar052/ar052-tests.log
$fx=$LASTEXITCODE;$ft=Get-Content artifacts/ar052/ar052-tests.log -Raw
$fm=[regex]::Matches($ft,'RESULT: (\d+) passed, (\d+) failed');$fp=[regex]::Matches($ft,'(?m)^PASS ').Count
$fv=$fx -eq 0 -and $fm.Count -eq 1 -and $fm[0].Groups[2].Value -eq '0' -and [int]$fm[0].Groups[1].Value -eq $fp -and $fp -eq 8
if(!$fv){
 @{task='AR-052';code_sha=$sha;focused_result=($fm.Value -join ';');focused_pass_lines=$fp;E1='FAIL';E2='NOT_RUN_AFTER_FOCUSED_FAILURE';E4='DEFERRED_BY_USER_AWAITING_ENVIRONMENT';clean_end=(!(git status --porcelain))} |
  ConvertTo-Json -Depth 6|Set-Content artifacts/ar052/validation.json
 throw 'AR-052 focused resume/rebase tests failed'
}

$failed=@();$retained=@()
foreach($case in @('AR-041','AR-042','AR-051','AR-031','AR-050')){
 dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter $case 2>&1|Tee-Object "artifacts/ar052/$case.log"
 $x=$LASTEXITCODE;$t=Get-Content "artifacts/ar052/$case.log" -Raw;$m=[regex]::Matches($t,'RESULT: (\d+) passed, (\d+) failed');$p=[regex]::Matches($t,'(?m)^PASS ').Count
 $v=$x -eq 0 -and $m.Count -eq 1 -and $m[0].Groups[2].Value -eq '0' -and [int]$m[0].Groups[1].Value -eq $p -and $p -gt 0
 $retained+=@{name=$case;exit=$x;result=($m.Value -join ';');pass_lines=$p};if(!$v){$failed+=$case}
}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build 2>&1|Tee-Object artifacts/ar052/full.log
$x=$LASTEXITCODE;$t=Get-Content artifacts/ar052/full.log -Raw;$m=[regex]::Matches($t,'RESULT: (\d+) passed, (\d+) failed');$p=[regex]::Matches($t,'(?m)^PASS ').Count
$full=$x -eq 0 -and $m.Count -eq 1 -and $m[0].Groups[2].Value -eq '0' -and [int]$m[0].Groups[1].Value -eq $p -and $p -gt 0

tools/agent-reliability/run_agent_suites.ps1 -OutputDirectory artifacts/ar052/all-agent-suites
$ax=$LASTEXITCODE;if($ax){$failed+='AGENT-SUITES'}

@{
 task='AR-052';code_sha=$sha;focused_result=($fm.Value -join ';');focused_pass_lines=$fp
 retained=$retained;full_result=($m.Value -join ';');full_pass_lines=$p;agent_suites_exit=$ax
 E1='PASS';E2=if($full -and $ax -eq 0 -and $failed.Count -eq 0){'PASS'}else{'FAIL'}
 E3='NOT_REQUIRED';E4='DEFERRED_BY_USER_AWAITING_ENVIRONMENT';E5='DEFERRED_BY_USER'
 protocol_matrix=@('Ollama','OpenAiChat','OpenAiResponses')
 old_provider_continuation_reused=$false;auto_provider_fallback=$false;mutation_replay_claim=$false
 fresh_turn_required=$true;fresh_permission_for_mutation=$true
 clean_end=(!(git status --porcelain));external_side_effects='none';personal_documents='not accessed';credentials='not accessed'
}|ConvertTo-Json -Depth 10|Set-Content artifacts/ar052/validation.json

if(!$full){$failed+='FULL'};if(git status --porcelain){$failed+='DIRTY-END'}
if($failed.Count){throw ('AR-052 validation failed: '+($failed -join ', '))}
