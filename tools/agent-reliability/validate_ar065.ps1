$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force artifacts/ar065 | Out-Null
if (git status --porcelain) { throw 'Dirty checkout: validation refused' }
@{code_sha=(git rev-parse HEAD);working_tree='CLEAN';OS=[System.Environment]::OSVersion.ToString();sdk=(dotnet --version);task='AR-065';E1='Pure wire projection and safe diagnostics';E2='Production HTTP/SSE and WebSocket transports with injected scripted handler/socket';E3='AWAITING_ENVIRONMENT';E4='AWAITING_ENVIRONMENT: actual H2 UI and authorized OpenAI profile not exercised';E5='AR-083 DEFERRED_BY_USER';network='No external provider calls; no credentials or personal files';run=$env:GITHUB_RUN_ID;attempt=$env:GITHUB_RUN_ATTEMPT} | ConvertTo-Json -Depth 6 | Set-Content artifacts/ar065/identity.json
python -c "import hashlib,json,pathlib,subprocess,zipfile; ps=subprocess.check_output(['git','ls-files'],text=True).splitlines(); ps=[p for p in ps if pathlib.Path(p).suffix.lower() in ['.cs','.csproj','.props','.targets','.md','.json','.yml','.yaml','.ps1','.slnx','.py']]; z=zipfile.ZipFile('artifacts/ar065/committed-source.zip','w',zipfile.ZIP_DEFLATED); rows=[]; [(z.writestr(p,b),rows.append({'path':p,'sha256':hashlib.sha256(b).hexdigest(),'blob':hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()})) for p in ps for b in [subprocess.check_output(['git','show','HEAD:'+p])]]; z.close(); pathlib.Path('artifacts/ar065/source-receipt.json').write_text(json.dumps(rows,indent=2))"
if ($LASTEXITCODE) { throw 'Source receipt failed' }
dotnet restore H2Notes.Avalonia.slnx 2>&1 | Tee-Object artifacts/ar065/restore.log
if ($LASTEXITCODE) { throw 'Restore failed' }
dotnet build H2Notes.Avalonia.slnx -c Release --no-restore 2>&1 | Tee-Object artifacts/ar065/build.log
if ($LASTEXITCODE) { throw 'Build failed' }
python tools/agent-reliability/test_ar065_wire_control.py
$control=$LASTEXITCODE
$failed=@();$results=@()
foreach ($case in @('AR-065-1','AR-065-2','AR-065-3','FULL')) {
  $env:H2_AR065_EVIDENCE_DIR = Join-Path (Resolve-Path artifacts/ar065) ("receipts-" + $case)
  $env:H2_AR040_EVIDENCE_DIR = Join-Path (Resolve-Path artifacts/ar065) ("job-receipts-" + $case)
  $filter=if($case -eq 'FULL'){@()}else{@('--filter','AR-065')}
  dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- @filter 2>&1 | Tee-Object "artifacts/ar065/$case.log"
  $exit=$LASTEXITCODE;$text=Get-Content "artifacts/ar065/$case.log" -Raw
  $match=[regex]::Matches($text,'RESULT: (\d+) passed, (\d+) failed')
  $passedLines=[regex]::Matches($text,'(?m)^PASS ').Count
  $valid=$match.Count -eq 1 -and $match[0].Groups[2].Value -eq '0' -and [int]$match[0].Groups[1].Value -eq $passedLines -and $passedLines -gt 0
  $results+=@{name=$case;exit=$exit;result=($match.Value -join ';');pass_lines=$passedLines}
  if($exit -ne 0 -or !$valid){$failed+=$case}
}
try { & tools/agent-reliability/run_agent_suites.ps1 -OutputDirectory artifacts/ar065/agent-suites; $agent=0 }
catch { $agent=1; Write-Host $_ }
@{code_sha=(git rev-parse HEAD);control_exit=$control;agent_exit=$agent;failed=$failed;results=$results;clean_end=(!(git status --porcelain))} | ConvertTo-Json -Depth 8 | Set-Content artifacts/ar065/validation.json
if($control -ne 0 -or $agent -ne 0 -or $failed.Count){ throw 'AR-065 validation failed. Preserve all logs; no acceptance upgrade.' }
