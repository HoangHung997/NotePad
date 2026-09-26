$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force artifacts/ar082 | Out-Null
if(git status --porcelain){throw 'Dirty checkout: AR-082 validation refused'}
$sha=git rev-parse HEAD
$branch=git branch --show-current
@{task='AR-082';code_sha=$sha;working_tree='CLEAN';sdk=(dotnet --version);OS=[Environment]::OSVersion.ToString();E1='manifest/privacy/dependency contracts';E2='exact self-contained package from Unicode/spaces path on fresh Windows runner profile';E3='PASS_CLEAN_WINDOWS_RUNNER_PROFILE_ONLY';E4='DEFERRED_BY_USER_AWAITING_ENVIRONMENT';E5='DEFERRED_BY_USER';physical_clean_machine_claim=$false;two_pc_claim=$false;network_provider_probe=$false}|ConvertTo-Json -Depth 8|Set-Content artifacts/ar082/identity.json

dotnet restore H2Notes.Avalonia.slnx 2>&1|Tee-Object artifacts/ar082/restore.log
if($LASTEXITCODE){throw 'Restore failed'}
dotnet build H2Notes.Avalonia.slnx -c Release --no-restore 2>&1|Tee-Object artifacts/ar082/build.log
if($LASTEXITCODE){throw 'Build failed'}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter AR-082 2>&1|Tee-Object artifacts/ar082/ar082-tests.log
$fx=$LASTEXITCODE;$ft=Get-Content artifacts/ar082/ar082-tests.log -Raw
$fm=[regex]::Matches($ft,'RESULT: (\d+) passed, (\d+) failed');$fp=[regex]::Matches($ft,'(?m)^PASS ').Count
$focused=$fx -eq 0 -and $fm.Count -eq 1 -and $fm[0].Groups[2].Value -eq '0' -and [int]$fm[0].Groups[1].Value -eq $fp -and $fp -eq 5
if(!$focused){throw 'AR-082 focused tests failed'}

$publish=Join-Path $env:RUNNER_TEMP 'ar082-publish-source'
Remove-Item -LiteralPath $publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish .\src\H2Notes.Avalonia\H2Notes.Avalonia.csproj -c Release -r win-x64 --self-contained true -o $publish 2>&1|Tee-Object artifacts/ar082/publish.log
if($LASTEXITCODE){throw 'AR-082 publish failed'}
& .\tools\agent-reliability\prepare_agent_python.ps1 -Destination (Join-Path $publish 'python')
if($LASTEXITCODE){throw 'AR-082 Agent Python packaging failed'}
& .\tools\agent-reliability\build_portable_manifest.ps1 -Root $publish -SourceSha $sha -SourceBranch $branch -RuntimeIdentifier win-x64

$portable=Join-Path $env:RUNNER_TEMP 'AR082 Gói sạch Việt Nam\Ứng dụng 测试'
Remove-Item -LiteralPath (Split-Path $portable -Parent) -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $portable -Force|Out-Null
# -LiteralPath intentionally does not expand wildcards; enumerate exact children and preserve Unicode paths.
Get-ChildItem -LiteralPath $publish -Force|ForEach-Object {
  Copy-Item -LiteralPath $_.FullName -Destination $portable -Recurse -Force
}

$cleanSettings=Join-Path $env:RUNNER_TEMP 'AR082 clean profile Việt Nam'
Remove-Item -LiteralPath $cleanSettings -Recurse -Force -ErrorAction SilentlyContinue
$env:H2_NOTES_SETTINGS_DIRECTORY=$cleanSettings
$oldSearch=$env:H2_BRAVE_SEARCH_API_KEY;$oldBrowser=$env:H2_BROWSER_CDP_ENDPOINT;$oldDotnet=$env:DOTNET_ROOT;$oldPath=$env:PATH
$env:H2_BRAVE_SEARCH_API_KEY=$null;$env:H2_BROWSER_CDP_ENDPOINT=$null
$env:DOTNET_ROOT='Z:\missing-dotnet-runtime'
$env:PATH="$env:SystemRoot\System32;$env:SystemRoot"
$pwshExe=Join-Path $PSHOME 'pwsh.exe'
if(-not (Test-Path -LiteralPath $pwshExe -PathType Leaf)){throw 'PowerShell child executable is unavailable for portable verifier isolation'}
function Invoke-PortableVerify([string]$root,[string]$output) {
  # External-process stdout is pipeline output in PowerShell. If it escapes this
  # function together with $LASTEXITCODE, the caller receives an object[] and
  # `if($exit)` becomes true even when the real process exit code is 0.
  # Buffer and replay diagnostics through Write-Host, then return one typed int.
  $lines = @(& $pwshExe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $root 'VERIFY-PORTABLE.ps1') -OutputDirectory $output 2>&1)
  [int]$exitCode = $LASTEXITCODE
  foreach($line in $lines){ Write-Host $line }
  return $exitCode
}
$cleanOut=Join-Path $env:RUNNER_TEMP 'ar082-clean-output'
Remove-Item -LiteralPath $cleanOut -Recurse -Force -ErrorAction SilentlyContinue
$cleanExit=Invoke-PortableVerify $portable $cleanOut
$cleanReport=Join-Path $cleanOut 'portable-check.json'
if(Test-Path -LiteralPath $cleanReport){Copy-Item -LiteralPath $cleanReport -Destination artifacts/ar082/clean-profile-portable-check.json -Force}
if([int]$cleanExit -ne 0){throw 'Clean-profile portable verification failed'}
$clean=Get-Content -LiteralPath $cleanReport -Raw|ConvertFrom-Json
if(!$clean.passed -or $clean.manifest.sourceSha -ne $sha){throw 'Clean portable report did not bind exact source SHA'}
foreach($id in @('ollama.endpoint','ai.online_credentials','web.search','browser.live_tab')){
  $row=$clean.dependencies|Where-Object id -eq $id
  if($row.state -ne 'NeedsConfiguration'){throw "Clean profile fabricated readiness for $id"}
}
$ocr=$clean.dependencies|Where-Object id -eq 'ocr.local'
if($ocr.code -ne 'optional_ocr_pack_not_bundled'){throw 'Standard portable unexpectedly requires/bundles OCR pack'}

# Configured profile/vault presence: synthetic DPAPI bytes, never used for a network request.
$configured=Join-Path $env:RUNNER_TEMP 'AR082 configured profile'
Remove-Item -LiteralPath $configured -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path (Join-Path $configured 'credentials') -Force|Out-Null
$apiId=[Guid]::NewGuid();$ollamaId=[Guid]::NewGuid()
$config=[ordered]@{DataFolder=$null;WorkspaceLocation=$null;DeviceId='ar082-ci';DesktopSession=$null;Ai=[ordered]@{Profiles=@([ordered]@{Id=$ollamaId;Name='CI Local';Protocol=0;BaseUrl='http://localhost:11434';Model='fixture-local';TimeoutSeconds=180;WaitForCompletion=$true;RequestReasoningSummary=$false;ReasoningEffort='';OllamaThinking=$null;RequestBudget=@{}},[ordered]@{Id=$apiId;Name='CI API';Protocol=2;BaseUrl='https://example.test/v1';Model='fixture-api';TimeoutSeconds=180;WaitForCompletion=$true;RequestReasoningSummary=$false;ReasoningEffort='';OllamaThinking=$null;RequestBudget=@{}});SelectedId=$apiId;Pdf=@{};ProjectAccessConversationIds=@()};WorkAssistant=@{};CommandCenter=@{};ProjectLayouts=@{}}
$config|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $configured 'local-config-v2.json') -Encoding utf8NoBOM
$sentinel='AR082_FAKE_SECRET_NEVER_OUTPUT'
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
$plain=[Text.Encoding]::UTF8.GetBytes($sentinel)
try{$protected=[Security.Cryptography.ProtectedData]::Protect($plain,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser);[IO.File]::WriteAllBytes((Join-Path $configured ('credentials\'+$apiId.ToString('N')+'.bin')),$protected)}finally{[Array]::Clear($plain,0,$plain.Length)}
$env:H2_NOTES_SETTINGS_DIRECTORY=$configured;$env:H2_BRAVE_SEARCH_API_KEY=$sentinel;$env:H2_BROWSER_CDP_ENDPOINT='http://127.0.0.1:9222'
$configuredOut=Join-Path $env:RUNNER_TEMP 'ar082-configured-output'
Remove-Item -LiteralPath $configuredOut -Recurse -Force -ErrorAction SilentlyContinue
$configuredExit=Invoke-PortableVerify $portable $configuredOut
$configuredReport=Join-Path $configuredOut 'portable-check.json'
if(Test-Path -LiteralPath $configuredReport){Copy-Item -LiteralPath $configuredReport -Destination artifacts/ar082/configured-profile-portable-check.json -Force}
if([int]$configuredExit -ne 0){throw 'Configured-profile portable verification failed'}
$configuredText=Get-Content -LiteralPath $configuredReport -Raw
if($configuredText.Contains($sentinel)){throw 'Portable preflight leaked synthetic credential'}
$configuredJson=$configuredText|ConvertFrom-Json
foreach($id in @('ollama.endpoint','ai.online_credentials','web.search','browser.live_tab')){
  $row=$configuredJson.dependencies|Where-Object id -eq $id
  if($row.state -ne 'Degraded'){throw "Configured provider $id was not reported as configured/not-live-probed"}
}

# Negative package: helper missing must fail manifest/helper preflight.
$badHelper=Join-Path $env:RUNNER_TEMP 'ar082-bad-helper';Remove-Item $badHelper -Recurse -Force -ErrorAction SilentlyContinue;Copy-Item $portable $badHelper -Recurse
Remove-Item -LiteralPath (Join-Path $badHelper 'H2AgentLab.OfficeHost.exe') -Force
$badHelperOut=Join-Path $env:RUNNER_TEMP 'ar082-bad-helper-out';Remove-Item $badHelperOut -Recurse -Force -ErrorAction SilentlyContinue
$badHelperExit=Invoke-PortableVerify $badHelper $badHelperOut
if([int]$badHelperExit -eq 0){throw 'Portable with missing OfficeHost unexpectedly passed'}

# Negative bootstrap: hostfxr missing must fail before app start with typed bootstrap code.
$badRuntime=Join-Path $env:RUNNER_TEMP 'ar082-bad-runtime';Remove-Item $badRuntime -Recurse -Force -ErrorAction SilentlyContinue;Copy-Item $portable $badRuntime -Recurse
Remove-Item -LiteralPath (Join-Path $badRuntime 'hostfxr.dll') -Force
$badRuntimeOut=Join-Path $env:RUNNER_TEMP 'ar082-bad-runtime-out';Remove-Item $badRuntimeOut -Recurse -Force -ErrorAction SilentlyContinue
$badRuntimeExit=Invoke-PortableVerify $badRuntime $badRuntimeOut
if([int]$badRuntimeExit -eq 0){throw 'Portable with missing hostfxr unexpectedly passed'}
$boot=Get-Content -LiteralPath (Join-Path $badRuntimeOut 'portable-bootstrap-check.json') -Raw|ConvertFrom-Json
if($boot.code -ne 'runtime_component_missing'){throw 'Missing runtime did not produce typed bootstrap diagnostic'}

# Standard package must contain no local state or multi-GB OCR model tree.
$forbidden=Get-ChildItem -LiteralPath $portable -Recurse -Force|Where-Object { $_.FullName -match '[\\/](credentials|agent-runtime|workspace-v2|journal-v2|task-records)([\\/]|$)' -or $_.Name -eq 'local-config-v2.json' }
if($forbidden){throw 'Portable contains machine-local state'}
if(Test-Path -LiteralPath (Join-Path $portable 'ocr-runtime\models')){throw 'Standard portable unexpectedly contains OCR model tree'}

$env:DOTNET_ROOT=$oldDotnet;$env:PATH=$oldPath;$env:H2_BRAVE_SEARCH_API_KEY=$oldSearch;$env:H2_BROWSER_CDP_ENDPOINT=$oldBrowser

$failed=@();$retained=@()
foreach($case in @('AR-081','AR-080','AR-042','AR-041','AR-062','AR-060')){
 dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter $case 2>&1|Tee-Object "artifacts/ar082/$case.log"
 $x=$LASTEXITCODE;$t=Get-Content "artifacts/ar082/$case.log" -Raw;$m=[regex]::Matches($t,'RESULT: (\d+) passed, (\d+) failed');$p=[regex]::Matches($t,'(?m)^PASS ').Count
 $v=$x -eq 0 -and $m.Count -eq 1 -and $m[0].Groups[2].Value -eq '0' -and [int]$m[0].Groups[1].Value -eq $p -and $p -gt 0
 $retained+=@{name=$case;exit=$x;result=($m.Value -join ';');pass_lines=$p};if(!$v){$failed+=$case}
}
dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build 2>&1|Tee-Object artifacts/ar082/full.log
$x=$LASTEXITCODE;$t=Get-Content artifacts/ar082/full.log -Raw;$m=[regex]::Matches($t,'RESULT: (\d+) passed, (\d+) failed');$p=[regex]::Matches($t,'(?m)^PASS ').Count
$full=$x -eq 0 -and $m.Count -eq 1 -and $m[0].Groups[2].Value -eq '0' -and [int]$m[0].Groups[1].Value -eq $p -and $p -gt 0
tools/agent-reliability/run_agent_suites.ps1 -OutputDirectory artifacts/ar082/all-agent-suites
$ax=$LASTEXITCODE;if($ax){$failed+='AGENT-SUITES'}

$manifest=Get-Content -LiteralPath (Join-Path $portable 'portable-manifest.json') -Raw|ConvertFrom-Json
Copy-Item -LiteralPath (Join-Path $portable 'portable-manifest.json') -Destination artifacts/ar082/portable-manifest.json
if(-not (Test-Path -LiteralPath artifacts/ar082/clean-profile-portable-check.json)){Copy-Item -LiteralPath $cleanReport -Destination artifacts/ar082/clean-profile-portable-check.json}
if(-not (Test-Path -LiteralPath artifacts/ar082/configured-profile-portable-check.json)){Copy-Item -LiteralPath $configuredReport -Destination artifacts/ar082/configured-profile-portable-check.json}
@{task='AR-082';code_sha=$sha;focused_result=($fm.Value -join ';');focused_pass_lines=$fp;retained=$retained;full_result=($m.Value -join ';');full_pass_lines=$p;agent_suites_exit=$ax;package_content_sha256=$manifest.contentSha256;package_file_count=$manifest.fileCount;package_total_bytes=$manifest.totalBytes;unicode_space_path=$portable;E1='PASS';E2=if($full -and $ax -eq 0 -and $failed.Count -eq 0){'PASS'}else{'FAIL'};E3='PASS_CLEAN_WINDOWS_RUNNER_PROFILE_ONLY';E4='DEFERRED_BY_USER_AWAITING_ENVIRONMENT';E5='DEFERRED_BY_USER';physical_clean_machine_claim=$false;native_provider_claim=$false;two_pc_claim=$false;network_provider_probe=$false;clean_end=(!(git status --porcelain))}|ConvertTo-Json -Depth 10|Set-Content artifacts/ar082/validation.json
if(!$full){$failed+='FULL'};if(git status --porcelain){$failed+='DIRTY-END'}
if($failed.Count){throw ('AR-082 validation failed: '+($failed -join ', '))}