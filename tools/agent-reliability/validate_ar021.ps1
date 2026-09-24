$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force artifacts/ar021 | Out-Null
if (git status --porcelain) { throw 'Dirty checkout: AR-021 validation refused' }

$sha = git rev-parse HEAD
@{
  task='AR-021'
  code_sha=$sha
  working_tree='CLEAN'
  OS=[System.Environment]::OSVersion.ToString()
  sdk=(dotnet --version)
  E1='A1 range/page/cursor/field contract and deterministic bounds'
  E2='Fixture backend + concrete OfficeHost named-pipe + production H2OfficeRuntimeTools range dispatch'
  E3='AWAITING_ENVIRONMENT: real native Excel workbook corpus has not been run'
  E4='AWAITING_ENVIRONMENT: real H2 UI + configured model Excel task has not been run'
  E5='DEFERRED_BY_USER: two-PC/NAS acceptance remains deferred'
  external_side_effects='none'
  user_documents='not accessed'
  credentials='not accessed'
} | ConvertTo-Json -Depth 8 | Set-Content artifacts/ar021/identity.json

dotnet restore H2Notes.Avalonia.slnx 2>&1 | Tee-Object artifacts/ar021/restore.log
if ($LASTEXITCODE) { throw 'Restore failed' }
dotnet build H2Notes.Avalonia.slnx -c Release --no-restore 2>&1 | Tee-Object artifacts/ar021/build.log
if ($LASTEXITCODE) { throw 'Build failed' }

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter AR-021 2>&1 | Tee-Object artifacts/ar021/ar021-tests.log
$ar021Exit=$LASTEXITCODE
$ar021Text=Get-Content artifacts/ar021/ar021-tests.log -Raw
$ar021Match=[regex]::Matches($ar021Text,'RESULT: (\d+) passed, (\d+) failed')
$ar021PassLines=[regex]::Matches($ar021Text,'(?m)^PASS ').Count
$ar021Valid=$ar021Exit -eq 0 -and $ar021Match.Count -eq 1 -and $ar021Match[0].Groups[2].Value -eq '0' -and [int]$ar021Match[0].Groups[1].Value -eq $ar021PassLines -and $ar021PassLines -ge 6

$office=(Resolve-Path 'experiments/H2AgentLab.OfficeHost/bin/Release/net10.0-windows/H2AgentLab.OfficeHost.exe').Path
dotnet run --project experiments/H2AgentLab/H2AgentLab.csproj -c Release --no-build -- --v2-office-host-test artifacts/ar021/office-host $office 2>&1 | Tee-Object artifacts/ar021/office-host.log
$officeExit=$LASTEXITCODE
$officeText=Get-Content artifacts/ar021/office-host.log -Raw
$officeRangePass=$officeText -match 'PASS 0804B AR-021 bounded Excel range page'

$failed=@()
$retained=@()
foreach ($case in @('AR-020','AR-012','AR-001')) {
  dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter $case 2>&1 | Tee-Object "artifacts/ar021/$case.log"
  $exit=$LASTEXITCODE
  $text=Get-Content "artifacts/ar021/$case.log" -Raw
  $match=[regex]::Matches($text,'RESULT: (\d+) passed, (\d+) failed')
  $passLines=[regex]::Matches($text,'(?m)^PASS ').Count
  $valid=$exit -eq 0 -and $match.Count -eq 1 -and $match[0].Groups[2].Value -eq '0' -and [int]$match[0].Groups[1].Value -eq $passLines -and $passLines -gt 0
  $retained+=@{name=$case;exit=$exit;result=($match.Value -join ';');pass_lines=$passLines}
  if(!$valid){$failed+=$case}
}

dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build 2>&1 | Tee-Object artifacts/ar021/full.log
$fullExit=$LASTEXITCODE
$fullText=Get-Content artifacts/ar021/full.log -Raw
$fullMatch=[regex]::Matches($fullText,'RESULT: (\d+) passed, (\d+) failed')
$fullPassLines=[regex]::Matches($fullText,'(?m)^PASS ').Count
$fullValid=$fullExit -eq 0 -and $fullMatch.Count -eq 1 -and $fullMatch[0].Groups[2].Value -eq '0' -and [int]$fullMatch[0].Groups[1].Value -eq $fullPassLines -and $fullPassLines -gt 0

@{
  task='AR-021'
  code_sha=$sha
  ar021_result=($ar021Match.Value -join ';')
  ar021_pass_lines=$ar021PassLines
  office_host_exit=$officeExit
  office_range_ipc_pass=$officeRangePass
  retained=$retained
  full_result=($fullMatch.Value -join ';')
  full_pass_lines=$fullPassLines
  E1=if($ar021Valid){'PASS'}else{'FAIL'}
  E2=if($ar021Valid -and $officeExit -eq 0 -and $officeRangePass){'PASS'}else{'FAIL'}
  E3='AWAITING_ENVIRONMENT'
  E4='AWAITING_ENVIRONMENT'
  E5='DEFERRED_BY_USER'
  e3_e4_e5_pass_claim=$false
  clean_end=(!(git status --porcelain))
} | ConvertTo-Json -Depth 10 | Set-Content artifacts/ar021/validation.json

if(!$ar021Valid){$failed+='AR-021-FOCUSED'}
if($officeExit -ne 0 -or !$officeRangePass){$failed+='OFFICE-HOST-IPC'}
if(!$fullValid){$failed+='FULL'}
if(git status --porcelain){$failed+='DIRTY-END'}
if($failed.Count){ throw ('AR-021 validation failed: ' + ($failed -join ', ')) }
