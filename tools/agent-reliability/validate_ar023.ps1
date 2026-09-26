$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force artifacts/ar023 | Out-Null
if(git status --porcelain){throw 'Dirty checkout: AR-023 validation refused'}
$sha=git rev-parse HEAD
@{
 task='AR-023';code_sha=$sha;working_tree='CLEAN';OS=[Environment]::OSVersion.ToString();sdk=(dotnet --version)
 E1='Word page/cursor/content-version contracts and preservation guards'
 E2='fixture long-document paging + concrete OfficeHost IPC + production H2OfficeRuntimeTools paged dispatch'
 E3='DEFERRED_BY_USER: real Word acceptance will be run from the final full build'
 E4='NOT_REQUIRED_FOR_AR023_IMPLEMENTATION'
 E5='DEFERRED_BY_USER'
 external_side_effects='none';user_documents='not accessed';credentials='not accessed'
}|ConvertTo-Json -Depth 8|Set-Content artifacts/ar023/identity.json

dotnet restore H2Notes.Avalonia.slnx 2>&1|Tee-Object artifacts/ar023/restore.log
if($LASTEXITCODE){throw 'Restore failed'}
dotnet build H2Notes.Avalonia.slnx -c Release --no-restore 2>&1|Tee-Object artifacts/ar023/build.log
if($LASTEXITCODE){throw 'Build failed'}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter AR-023 2>&1|Tee-Object artifacts/ar023/ar023-tests.log
$fx=$LASTEXITCODE;$ft=Get-Content artifacts/ar023/ar023-tests.log -Raw
$fm=[regex]::Matches($ft,'RESULT: (\d+) passed, (\d+) failed');$fp=[regex]::Matches($ft,'(?m)^PASS ').Count
$fv=$fx -eq 0 -and $fm.Count -eq 1 -and $fm[0].Groups[2].Value -eq '0' -and [int]$fm[0].Groups[1].Value -eq $fp -and $fp -ge 9

$office=(Resolve-Path 'experiments/H2AgentLab.OfficeHost/bin/Release/net10.0-windows/H2AgentLab.OfficeHost.exe').Path
dotnet run --project experiments/H2AgentLab/H2AgentLab.csproj -c Release --no-build -- --v2-office-host-test artifacts/ar023/office-host $office 2>&1|Tee-Object artifacts/ar023/office-host.log
$ox=$LASTEXITCODE;$ot=Get-Content artifacts/ar023/office-host.log -Raw
$op=$ot -match 'PASS 0810B AR-023 bounded Word paragraph pages'

$failed=@();$retained=@()
foreach($case in @('AR-022','AR-021','AR-020','AR-012','AR-001')){
 dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter $case 2>&1|Tee-Object "artifacts/ar023/$case.log"
 $x=$LASTEXITCODE;$t=Get-Content "artifacts/ar023/$case.log" -Raw;$m=[regex]::Matches($t,'RESULT: (\d+) passed, (\d+) failed');$p=[regex]::Matches($t,'(?m)^PASS ').Count
 $v=$x -eq 0 -and $m.Count -eq 1 -and $m[0].Groups[2].Value -eq '0' -and [int]$m[0].Groups[1].Value -eq $p -and $p -gt 0
 $retained+=@{name=$case;exit=$x;result=($m.Value -join ';');pass_lines=$p};if(!$v){$failed+=$case}
}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build 2>&1|Tee-Object artifacts/ar023/full.log
$x=$LASTEXITCODE;$t=Get-Content artifacts/ar023/full.log -Raw;$m=[regex]::Matches($t,'RESULT: (\d+) passed, (\d+) failed');$p=[regex]::Matches($t,'(?m)^PASS ').Count
$full=$x -eq 0 -and $m.Count -eq 1 -and $m[0].Groups[2].Value -eq '0' -and [int]$m[0].Groups[1].Value -eq $p -and $p -gt 0

tools/agent-reliability/run_agent_suites.ps1 -OutputDirectory artifacts/ar023/all-agent-suites
$ax=$LASTEXITCODE;if($ax){$failed+='AGENT-SUITES'}

@{
 task='AR-023';code_sha=$sha;focused_result=($fm.Value -join ';');focused_pass_lines=$fp
 office_host_exit=$ox;office_paged_word_ipc_pass=$op;retained=$retained
 full_result=($m.Value -join ';');full_pass_lines=$p;agent_suites_exit=$ax
 E1=if($fv){'PASS'}else{'FAIL'}
 E2=if($fv -and $ox -eq 0 -and $op -and $full -and $ax -eq 0){'PASS'}else{'FAIL'}
 E3='DEFERRED_BY_USER';E4='NOT_RUN';E5='DEFERRED_BY_USER';e3_e4_e5_pass_claim=$false
 clean_end=(!(git status --porcelain))
}|ConvertTo-Json -Depth 10|Set-Content artifacts/ar023/validation.json

if(!$fv){$failed+='AR-023-FOCUSED'}
if($ox -ne 0 -or !$op){$failed+='OFFICE-HOST-IPC'}
if(!$full){$failed+='FULL'}
if(git status --porcelain){$failed+='DIRTY-END'}
if($failed.Count){throw ('AR-023 validation failed: '+($failed -join ', '))}
