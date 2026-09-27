$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force artifacts/ar072 | Out-Null
if(git status --porcelain){throw 'Dirty checkout: AR-072 validation refused'}
$sha=git rev-parse HEAD
@{
 task='AR-072';code_sha=$sha;working_tree='CLEAN';sdk=(dotnet --version);OS=[Environment]::OSVersion.ToString()
 E1='comparison contract/mapping/recommendation harness'
 E2='deterministic same-corpus benchmark current H2 AgentRuntime vs frozen AgentRunner baseline'
 external_engine='NOT_TESTED_NO_AUTHORIZED_PACKAGE_OR_ENDPOINT'
 production_switch=$false;concurrent_planner_control=$false;model_quality_claim=$false
 E3='NOT_RUN';E4='NOT_RUN';E5='DEFERRED_BY_USER'
}|ConvertTo-Json -Depth 8|Set-Content artifacts/ar072/identity.json

dotnet restore H2Notes.Avalonia.slnx 2>&1|Tee-Object artifacts/ar072/restore.log
if($LASTEXITCODE){throw 'Restore failed'}
dotnet build H2Notes.Avalonia.slnx -c Release --no-restore 2>&1|Tee-Object artifacts/ar072/build.log
if($LASTEXITCODE){throw 'Build failed'}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter AR-072 2>&1|Tee-Object artifacts/ar072/ar072-tests.log
$fx=$LASTEXITCODE;$ft=Get-Content artifacts/ar072/ar072-tests.log -Raw
$fm=[regex]::Matches($ft,'RESULT: (\d+) passed, (\d+) failed');$fp=[regex]::Matches($ft,'(?m)^PASS ').Count
$fv=$fx -eq 0 -and $fm.Count -eq 1 -and $fm[0].Groups[2].Value -eq '0' -and [int]$fm[0].Groups[1].Value -eq $fp -and $fp -eq 6
if(!$fv){throw 'AR-072 focused comparison tests failed'}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --ar072-benchmark artifacts/ar072/benchmark 20 2>&1|Tee-Object artifacts/ar072/benchmark.log
$bx=$LASTEXITCODE
if($bx){throw 'AR-072 benchmark command failed'}
$comparison=Get-Content artifacts/ar072/benchmark/comparison.json -Raw|ConvertFrom-Json
$benchmarkPass=(
 $comparison.Iterations -eq 20 -and
 $comparison.Current.Completions -eq 20 -and $comparison.Current.Errors -eq 0 -and
 $comparison.Candidate.Completions -eq 20 -and $comparison.Candidate.Errors -eq 0 -and
 $comparison.Candidate.MissingMandatoryMappings -gt 0 -and
 $comparison.Recommendation -eq 'KEEP_CURRENT' -and
 -not $comparison.ProductionSwitchRequested -and
 -not $comparison.ConcurrentPlannerControl -and
 $comparison.ExternalCandidate.Status -eq 'NotTested' -and
 $comparison.ExternalCandidate.LicenseReview -eq 'NotEvaluated'
)
if(!$benchmarkPass){throw 'AR-072 benchmark evidence is inconsistent'}

$failed=@();$retained=@()
foreach($case in @('AR-071','AR-090','AR-082','AR-080','AR-042','AR-041')){
 dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter $case 2>&1|Tee-Object "artifacts/ar072/$case.log"
 $x=$LASTEXITCODE;$t=Get-Content "artifacts/ar072/$case.log" -Raw;$m=[regex]::Matches($t,'RESULT: (\d+) passed, (\d+) failed');$p=[regex]::Matches($t,'(?m)^PASS ').Count
 $v=$x -eq 0 -and $m.Count -eq 1 -and $m[0].Groups[2].Value -eq '0' -and [int]$m[0].Groups[1].Value -eq $p -and $p -gt 0
 $retained+=@{name=$case;exit=$x;result=($m.Value -join ';');pass_lines=$p};if(!$v){$failed+=$case}
}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build 2>&1|Tee-Object artifacts/ar072/full.log
$x=$LASTEXITCODE;$t=Get-Content artifacts/ar072/full.log -Raw;$m=[regex]::Matches($t,'RESULT: (\d+) passed, (\d+) failed');$p=[regex]::Matches($t,'(?m)^PASS ').Count
$full=$x -eq 0 -and $m.Count -eq 1 -and $m[0].Groups[2].Value -eq '0' -and [int]$m[0].Groups[1].Value -eq $p -and $p -gt 0

tools/agent-reliability/run_agent_suites.ps1 -OutputDirectory artifacts/ar072/all-agent-suites
$ax=$LASTEXITCODE;if($ax){$failed+='AGENT-SUITES'}

@{
 task='AR-072';code_sha=$sha;focused_result=($fm.Value -join ';');focused_pass_lines=$fp
 benchmark=@{
   iterations=$comparison.Iterations
   corpusId=$comparison.CorpusId
   currentP50Ms=$comparison.Current.P50Milliseconds
   currentP95Ms=$comparison.Current.P95Milliseconds
   candidateP50Ms=$comparison.Candidate.P50Milliseconds
   candidateP95Ms=$comparison.Candidate.P95Milliseconds
   candidateMissingMandatoryMappings=$comparison.Candidate.MissingMandatoryMappings
   candidateAdapterCost=$comparison.Candidate.AdapterCostClass
   recommendation=$comparison.Recommendation
   externalCandidateStatus=$comparison.ExternalCandidate.Status
 }
 retained=$retained;full_result=($m.Value -join ';');full_pass_lines=$p;agent_suites_exit=$ax
 E1='PASS';E2=if($full -and $ax -eq 0 -and $failed.Count -eq 0){'PASS'}else{'FAIL'}
 E3='NOT_RUN';E4='NOT_RUN';E5='DEFERRED_BY_USER'
 production_switch=$false;concurrent_planner_control=$false;external_engine_claim=$false;model_quality_claim=$false
 clean_end=(!(git status --porcelain));external_side_effects='none';credentials='not accessed';personal_documents='not accessed'
}|ConvertTo-Json -Depth 10|Set-Content artifacts/ar072/validation.json

if(!$full){$failed+='FULL'};if(git status --porcelain){$failed+='DIRTY-END'}
if($failed.Count){throw ('AR-072 validation failed: '+($failed -join ', '))}
