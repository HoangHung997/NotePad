#!/usr/bin/env python3
"""One-time AR-001 patch delivery for the isolated GitHub workspace.
Refuses unrelated source changes. Does not invoke models, Office or user resources.
Actual C# sources/tests, not this delivery script, are the implementation authority.
"""
from pathlib import Path
import datetime
import json
import os
import re
import subprocess

ROOT = Path.cwd()
BASE = 'e573394963ea44398217721ed5f200f899234d31'
BRANCH = 'feature/h2-agent-reliability-ar-000'
LAB = 'experiments/H2AgentLab/'
TRACKER = 'docs/H2_AGENT_RELIABILITY_IMPLEMENTATION_TASKS.md'
OUTPUTS = [
 '.github/workflows/avalonia-ci.yml', 'docs/H2_AGENT_FINAL_ARCHITECTURE_REPORT.md', TRACKER,
 'docs/agent-reliability/AR-001/implementation.md',
 'experiments/H2AgentLab.OfficeHost/ComOfficeBackend.cs',
 'experiments/H2AgentLab.OfficeHost/FixtureOfficeBackend.cs',
 'experiments/H2AgentLab.OfficeHost/OfficeHostServer.cs',
 'experiments/H2AgentLab.OfficeProtocol/ExcelPatchLimits.cs',
 LAB+'Acceptance/MbFinalArchitectureReportTests.cs', LAB+'Integration/H2OfficeRuntimeTools.cs',
 LAB+'Integration/H2ProductionAgentAdapter.cs', LAB+'Office/OfficeHostClient.cs',
 LAB+'Runtime/MbRetireAgentToolsSwitchTests.cs', LAB+'Runtime/MbRetireV1ToolRegistryAdapterTests.cs',
 LAB+'V2ArchitectureTests.cs', 'tests/H2Notes.Tests/H2AgentReliabilityContractTests.cs',
 'tests/H2Notes.Tests/H2Notes.Tests.csproj', 'tests/H2Notes.Tests/H2WorkAssistantRepairTests.cs',
 'tests/H2Notes.Tests/Program.cs']

def git(*args): return subprocess.check_output(['git', *args], text=True).strip()
def require(ok, reason):
    if not ok: raise RuntimeError(reason)
def replace(path, old, new):
    p=ROOT/path
    value=p.read_bytes().decode('utf-8')
    require(value.count(old)==1, 'Changed source anchor: '+path+' / '+old[:80])
    p.write_bytes(value.replace(old,new,1).encode('utf-8'))

require(os.environ.get('AR_BRANCH')==BRANCH, 'Wrong implementation branch')
require(git('rev-parse','HEAD')==os.environ['AR_HEAD'], 'Wrong checkout')
require(not git('status','--porcelain'), 'Dirty tree; refuse to overwrite')
if Path('tests/H2Notes.Tests/H2AgentReliabilityContractTests.cs').exists():
    print('AR-001 patch already applied; do not overwrite subsequent repair work.')
    raise SystemExit(0)
require(git('rev-parse','origin/main')=='1283bc13e07c3cd47d04886166de3dfc595422c0', 'Main advanced; reconcile first')
changed=set(git('diff','--name-only',BASE,'HEAD').splitlines())
require(changed <= {'.github/workflows/h2-ar001-workspace.yml',
    'tools/agent-reliability/Ar001Contracts.cs.txt','tools/agent-reliability/apply_ar001.py'}, 'Unrelated branch work appeared')
for path in OUTPUTS:
    if Path(path).exists():
        require(git('rev-parse','HEAD:'+path)==git('rev-parse',BASE+':'+path), 'Source changed: '+path)

p=LAB+'Integration/H2ProductionAgentAdapter.cs'
replace(p,'using H2AgentLab.Transport;','using H2AgentLab.Transport;\nusing H2AgentLab.Tools;')
replace(p,'    IDisposable\n{','    IDisposable,\n    IAsyncDisposable\n{')
replace(p,'    private bool _disposed;','    private bool _disposed;\n    private Task? _shutdownTask;')
replace(p,'            "You are H2 Agent,','            $"You are H2 Agent,')
replace(p,'discover skills with search_skills and read the applicable guidance using read_skill','discover skills with {SkillRuntimeToolExecutor.SearchToolName} and read the applicable guidance using {SkillRuntimeToolExecutor.ReadToolName}')
replace(p,'        lock (_gate)\n            _live.Add(taskId, live);','''        lock (_gate)
        {
            // Disposal may have begun while request-local context was being prepared.
            if (_disposed)
            {
                live.Cancellation.Dispose();
                throw new ObjectDisposedException(nameof(H2ProductionAgentAdapter));
            }
            _live.Add(taskId, live);
        }''')
replace(p,'''    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LiveTask[] tasks;
        lock (_gate)
            tasks = _live.Values.ToArray();
        foreach (var task in tasks)
        {
            task.Cancellation.Cancel();
            task.Cancellation.Dispose();
        }
    }''','''    /// <summary>Request cancellation without blocking the UI thread. Resource owners that
    /// remove the state/workspace must await DisposeAsync before deleting it.</summary>
    public void Dispose() => _ = BeginShutdown();

    /// <summary>Wait for the existing execution tasks, including helper/process teardown and
    /// final archive writes. A terminal UI summary alone is not a quiescence barrier.</summary>
    public ValueTask DisposeAsync() => new(BeginShutdown());

    private Task BeginShutdown()
    {
        LiveTask[] tasks;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_shutdownTask is not null) return _shutdownTask;
            _disposed = true;
            tasks = _live.Values.ToArray();
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdownTask = completion.Task;
        }
        // Never run arbitrary cancellation callbacks while holding the admission lock.
        _ = DrainShutdownAsync(tasks, completion);
        return completion.Task;
    }

    private static async Task DrainShutdownAsync(LiveTask[] tasks, TaskCompletionSource completion)
    {
        var errors = new List<Exception>();
        foreach (var task in tasks)
        {
            try { task.Cancellation.Cancel(); }
            catch (Exception ex) { errors.Add(ex); }
        }
        try
        {
            await Task.WhenAll(tasks.Select(task => task.Finished.Task)).ConfigureAwait(false);
            foreach (var task in tasks) task.Cancellation.Dispose();
            if (errors.Count > 0) completion.TrySetException(new AggregateException(errors));
            else completion.TrySetResult();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }''')
Path('experiments/H2AgentLab.OfficeProtocol/ExcelPatchLimits.cs').write_text('''namespace H2AgentLab.OfficeProtocol;

/// <summary>One cardinality contract for callable schemas, adapters, IPC and Office backends.
/// This rejects an oversized batch; it never truncates or automatically splits a mutation.</summary>
public static class ExcelPatchLimits
{
    public const int MinCells = 1;
    public const int MaxCells = 128;
    public const string ErrorCode = "invalid_request";

    public static string? ValidationError(int count)
        => count is < MinCells or > MaxCells
            ? $"Excel patch must contain {MinCells}..{MaxCells} single-cell operations; no cells were written."
            : null;
}
''',encoding='utf-8')
p=LAB+'Integration/H2OfficeRuntimeTools.cs'
replace(p,'minItems = 1, maxItems = 200','minItems = ExcelPatchLimits.MinCells, maxItems = ExcelPatchLimits.MaxCells')
replace(p,'''    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);''','''    {
        ct.ThrowIfCancellationRequested();
        // Count preflight precedes discovery/snapshot/IPC and any native write.
        if (call.Name is "excel.write_range" or "excel.set_formula" or "excel.apply_format")
        {
            var count = call.Arguments.TryGetProperty("cells", out var batch)
                && batch.ValueKind == JsonValueKind.Array ? batch.GetArrayLength() : -1;
            if (ExcelPatchLimits.ValidationError(count) is { } problem)
                return JsonSerializer.Serialize(new { ok = false, error = ExcelPatchLimits.ErrorCode,
                    message = problem, mutationApplied = false });
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);''')
replace(p,'                    if (cells.Length is < 1 or > 200) throw new ArgumentException("Patch must contain 1–200 cells.");','''                    if (ExcelPatchLimits.ValidationError(cells.Length) is { } problem)
                        throw new ArgumentException(problem);''')
p=LAB+'Office/OfficeHostClient.cs'
replace(p,'        => CallAsync<ExcelPatchResult>("excel.patch", request, null, cancellationToken);','''    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (ExcelPatchLimits.ValidationError(request.Cells?.Count ?? -1) is { } problem)
            return Task.FromException<ExcelPatchResult>(new OfficeHostClientException(ExcelPatchLimits.ErrorCode, problem));
        return CallAsync<ExcelPatchResult>("excel.patch", request, null, cancellationToken);
    }''')
for path in ['experiments/H2AgentLab.OfficeHost/ComOfficeBackend.cs','experiments/H2AgentLab.OfficeHost/FixtureOfficeBackend.cs']:
    p=Path(path); s=p.read_text(); a=s.index('    public ExcelPatchResult PatchExcel('); b=s.index('    public ExcelLiveSnapshot RecalculateExcel',a); part=s[a:b]
    part,n=re.subn(r'            if \(request.Cells.Count is < 1 or > 128\)\n                throw new OfficeHostFaultException\("invalid_request", "Excel patch must contain 1..128 single-cell operations\."\);\n','',part)
    if not n:
        part,n=re.subn(r'        if \(request.Cells.Count is < 1 or > 128\)\n            throw new OfficeHostFaultException\("invalid_request", "Excel patch must contain 1..128 cells\."\);\n','',part)
    require(n==1,'Backend limit changed')
    part=part.replace('        OfficeHostSafety.RequirePermission(request.PermissionGranted);','''        OfficeHostSafety.RequirePermission(request.PermissionGranted);
        if (ExcelPatchLimits.ValidationError(request.Cells?.Count ?? -1) is { } problem)
            throw new OfficeHostFaultException(ExcelPatchLimits.ErrorCode, problem);''',1)
    p.write_text(s[:a]+part+s[b:],encoding='utf-8')
p='experiments/H2AgentLab.OfficeHost/FixtureOfficeBackend.cs'
replace(p,'    private readonly WordFixture _word = new();','''    private readonly WordFixture _word = new();

    public FixtureOfficeBackend(int extraExcelRows = 0)
    {
        if (extraExcelRows is < 0 or > 256) throw new ArgumentOutOfRangeException(nameof(extraExcelRows));
        for (var row = 3; row < 3 + extraExcelRows; row++)
        {
            var address = "A" + row;
            _excel.Cells.Add(address, new ExcelCellFixture(address, "UNCHANGED-" + row, "", false, false, null, "General"));
        }
    }''')
p='experiments/H2AgentLab.OfficeHost/OfficeHostServer.cs'
replace(p,'"excel.patch" => _backend.PatchExcel(Parameters<ExcelPatchRequest>(request)),','"excel.patch" => PatchExcel(Parameters<ExcelPatchRequest>(request)),')
replace(p,'    private static object FixtureDelay(','''    private ExcelPatchResult PatchExcel(ExcelPatchRequest request)
    {
        OfficeHostSafety.RequirePermission(request.PermissionGranted);
        if (ExcelPatchLimits.ValidationError(request.Cells?.Count ?? -1) is { } problem)
            throw new OfficeHostFaultException(ExcelPatchLimits.ErrorCode, problem);
        return _backend.PatchExcel(request);
    }

    private static object FixtureDelay(''')
replace(LAB+'V2ArchitectureTests.cs','                "read_skill",','                "read_skill",\n                "read_tool_output",')
replace(LAB+'Runtime/MbRetireV1ToolRegistryAdapterTests.cs','        "read_skill",','        "read_skill",\n        "read_tool_output",')
p=LAB+'Runtime/MbRetireAgentToolsSwitchTests.cs'
replace(p,'        "read_skill",','        "read_skill",\n        "read_tool_output",')
replace(p,'Check(executors.Length == 6,\n                "Expected six domain executors, got: "','Check(executors.Length == 7,\n                "Expected seven domain executors including evidence, got: "')
p=LAB+'Acceptance/MbFinalArchitectureReportTests.cs'
replace(p,'Check(registry.Tools.Count == 19,\n                "Normal built-in ToolRegistry descriptor count changed from 19.");','''Check(registry.Tools.Count == 20 && registry.TryGet("read_tool_output", out var reader)
                  && reader.Namespace.Name == "evidence" && !reader.IsMutating && reader.SupportsParallel,
                "Expected 19 historical tools plus the read-only evidence reader, with unchanged access policy.");''')
replace(p,'"Final report tool/schema metrics do not match canonical runtime."','"Historical report lost its frozen tool/schema measurements."')
p=Path('docs/H2_AGENT_FINAL_ARCHITECTURE_REPORT.md')
p.write_text(p.read_text()+'''\n\n## AR-001 current-surface correction (2026-09-22)\n\nThe 19-callable/six-executor measurements above are the historical MB-120 snapshot, not the current registry inventory. The already-shipped `read_tool_output` adds one read-only evidence tool and one evidence executor: current inventory is 20 callable descriptors / seven executors. Initial exposure remains `tool_search` plus `update_plan` (two schemas); no additional tool is eagerly exposed. AR-001 updates exact-set guards and tests chunk/foreign-handle behavior; it does not re-award any old MB acceptance.\n''',encoding='utf-8')
p='tests/H2Notes.Tests/H2WorkAssistantRepairTests.cs'
replace(p,'command = "[IO.File]::WriteAllText(\'" + marker.Replace("\'", "\'\'") + "\', \'command-result\'); Write-Output \'command-result\'"','command = "[IO.File]::WriteAllText(\'" + marker.Replace("\'", "\'\'") + "\', \'command-result\'); Write-Output \'command-result\'", timeout_seconds = 20')
replace(p,'var done = Wait(adapter, adapter.StartTaskAsync(null, "Run command", context, false).Result);','''// Budget covers the explicit 20-second command deadline plus teardown margin.
                var done = Wait(adapter, adapter.StartTaskAsync(null, "Run command", context, false).Result, 30_000);''')
replace(p,'''    private static H2ProductionAgentAdapter Adapter(string root, Script script) => new(Path.Combine(root, "state-" + Guid.NewGuid().ToString("N")),
        () => new(new AiProfile { Name = "test", Model = "script", BaseUrl = "https://example.test/v1", Protocol = AiProtocol.OpenAiChat }, ""), script);''','''    [ThreadStatic] private static List<H2ProductionAgentAdapter>? _workspaceAdapters;
    private static H2ProductionAgentAdapter Adapter(string root, Script script)
    {
        var adapter = new H2ProductionAgentAdapter(Path.Combine(root, "state-" + Guid.NewGuid().ToString("N")),
            () => new(new AiProfile { Name = "test", Model = "script", BaseUrl = "https://example.test/v1", Protocol = AiProtocol.OpenAiChat }, ""), script);
        _workspaceAdapters?.Add(adapter);
        return adapter;
    }''')
replace(p,'''    { var root = Path.Combine(Path.GetTempPath(), "h2-assistant-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { body(root); } finally { Directory.Delete(root, true); } }''','''    {
        var root = Path.Combine(Path.GetTempPath(), "h2-assistant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = _workspaceAdapters;
        var owned = _workspaceAdapters = new List<H2ProductionAgentAdapter>();
        Exception? failure = null;
        var cleanupErrors = new List<Exception>();
        try { body(root); }
        catch (Exception ex) { failure = ex; }
        finally
        {
            foreach (var adapter in owned)
            {
                try { adapter.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult(); }
                catch (Exception ex) { cleanupErrors.Add(ex); }
            }
            if (cleanupErrors.Count == 0)
            {
                try { Directory.Delete(root, true); }
                catch (Exception ex) { cleanupErrors.Add(ex); }
            }
            _workspaceAdapters = previous;
        }
        if (cleanupErrors.Count > 0)
            throw new AggregateException("Fixture teardown failed; evidence retained at " + root,
                failure is null ? cleanupErrors : new[] { failure }.Concat(cleanupErrors));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }''')
replace(p,'private static H2AgentTaskSummary Wait(IH2AgentAdapter adapter, Guid id)\n    { var until = Environment.TickCount64 + 10000;','private static H2AgentTaskSummary Wait(IH2AgentAdapter adapter, Guid id, int budgetMilliseconds = 10000)\n    { var until = Environment.TickCount64 + budgetMilliseconds;')
needle='        test("Work Assistant full access expires and never survives read-only override",'
replace(p,needle,'''        test("AR-001 forced wait timeout drains owned command and preserves the primary failure", () =>
        {
            string? workspace = null;
            int processId = 0;
            Exception? observed = null;
            try
            {
                InWorkspace(root =>
                {
                    workspace = root;
                    var pidFile = Path.Combine(root, "owned-process.txt");
                    var script = new Script([
                        new("load", "tool_search", "{\\"query\\":\\"exec_command\\"}"),
                        new("exec", "exec_command", JsonSerializer.Serialize(new { command =
                            "[IO.File]::WriteAllText('" + pidFile.Replace("'", "''") + "', [string]$PID); Start-Sleep -Seconds 60",
                            timeout_seconds = 90 }))]);
                    using var adapter = Adapter(root, script);
                    var context = new H2AgentTaskContext(root, "", PermissionScope:
                        WorkAssistantPermissionScopeMapper.ForWorkspace(H2AgentPermissionMode.FullAccess, root, DateTime.UtcNow).PermissionScope);
                    var id = adapter.StartTaskAsync(null, "Disposable command teardown fixture", context, false).Result;
                    var deadline = Environment.TickCount64 + 30_000;
                    while ((!File.Exists(pidFile) || new FileInfo(pidFile).Length == 0) && Environment.TickCount64 < deadline)
                    { Pump(); Thread.Sleep(10); }
                    Check(File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile), out processId), "Fixture process did not start.");
                    _ = Wait(adapter, id, 50);
                });
            }
            catch (Exception ex) { observed = ex; }
            Check(observed is TimeoutException, "The original timeout was lost or replaced by cleanup: " + observed);
            Check(workspace is not null && !Directory.Exists(workspace), "Workspace deleted before drain or leaked after shutdown.");
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(processId);
                Check(process.HasExited, "Owned command survived awaited adapter shutdown.");
            }
            catch (ArgumentException) { /* The owned PID no longer exists. */ }
        });

'''+needle)
replace('tests/H2Notes.Tests/H2Notes.Tests.csproj','    <ProjectReference Include="../../src/H2Notes.Coordinator/H2Notes.Coordinator.csproj" />','''    <ProjectReference Include="../../src/H2Notes.Coordinator/H2Notes.Coordinator.csproj" />
    <ProjectReference Include="../../experiments/H2AgentLab.OfficeHost/H2AgentLab.OfficeHost.csproj" />''')
replace('tests/H2Notes.Tests/Program.cs','H2AgentCapabilityRepairTests.Run(Test);','H2AgentCapabilityRepairTests.Run(Test);\nH2AgentReliabilityContractTests.Run(Test);')
Path('tests/H2Notes.Tests/H2AgentReliabilityContractTests.cs').write_bytes(Path('tools/agent-reliability/Ar001Contracts.cs.txt').read_bytes())

p='.github/workflows/avalonia-ci.yml'
replace(p,'      - name: Checkout\n        uses: actions/checkout@v4','''      - name: Checkout
        uses: actions/checkout@v4
        with:
          ref: ${{ github.event.pull_request.head.sha || github.sha }}

      - name: Record exact tested source
        shell: pwsh
        run: |
          New-Item -ItemType Directory -Force artifacts/agent-reliability | Out-Null
          @{ code_sha = (git rev-parse HEAD); run_id = '${{ github.run_id }}';
             attempt = '${{ github.run_attempt }}'; evidence = 'E1/E2 fixtures; native Office/model E3/E4 NOT_RUN; E5 DEFERRED_BY_USER' } |
            ConvertTo-Json | Set-Content artifacts/agent-reliability/source.json''')
replace(p,'      - name: Run H2 Notes tests\n        run: dotnet run --project .\\tests\\H2Notes.Tests\\H2Notes.Tests.csproj -c Release --no-build','''      - name: AR-001 RC-02 and teardown regression repeated three times
        shell: pwsh
        run: |
          foreach ($iteration in 1..3) {
            $log = "artifacts/agent-reliability/ar001-$iteration.txt"
            dotnet run --project .\\tests\\H2Notes.Tests\\H2Notes.Tests.csproj -c Release --no-build -- --filter 'AR-001' 2>&1 | Tee-Object -FilePath $log
            if ($LASTEXITCODE -ne 0) { throw "AR-001 iteration $iteration failed." }
            if (-not (Select-String -Path $log -Pattern 'RESULT: [1-9][0-9]* passed, 0 failed')) { throw 'AR-001 filter did not execute a nonempty passing corpus.' }
          }

      - name: Run H2 Notes tests
        shell: pwsh
        run: |
          dotnet run --project .\\tests\\H2Notes.Tests\\H2Notes.Tests.csproj -c Release --no-build 2>&1 | Tee-Object -FilePath artifacts/agent-reliability/h2-tests.txt
          if ($LASTEXITCODE -ne 0) { throw 'H2 Notes regression failed.' }''')
replace(p,'          if-no-files-found: warn','          if-no-files-found: error')
path=Path(p);path.write_text(path.read_text()+'''

      - name: Retain bounded Agent acceptance summaries
        if: always()
        shell: pwsh
        run: |
          New-Item -ItemType Directory -Force artifacts/agent-reliability | Out-Null
          Get-ChildItem $env:RUNNER_TEMP -Directory -Filter 'h2-agent-*' | ForEach-Object {
            $name = $_.Name
            Get-ChildItem $_.FullName -File | Where-Object { $_.Extension -in '.txt','.json' -and $_.Length -lt 2000000 } | ForEach-Object {
              Copy-Item $_.FullName (Join-Path 'artifacts/agent-reliability' ($name + '-' + $_.Name))
            }
          }
          $helper = Join-Path $env:RUNNER_TEMP 'h2-packaged-helpers.json'
          if (Test-Path $helper) { Copy-Item $helper artifacts/agent-reliability/ }

      - name: Upload Agent reliability evidence (not native acceptance)
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: H2-Agent-Reliability-Evidence
          path: artifacts/agent-reliability/
          if-no-files-found: error
''',encoding='utf-8')
p=Path(TRACKER);text=p.read_text();m=re.search(r'```yaml\n(\{\n.*?\n\})\n```',text,re.S);require(m is not None,'Missing handoff');state=json.loads(m[1]);require(state['active_task']=='AR-001' and state['implementation_status']=='ACTIVE','Wrong active task')
state['checkpoint_saved_at_utc']=datetime.datetime.now(datetime.timezone.utc).isoformat()
state['remaining_in_active_task']=['Run RC-02 and teardown corpus 3x and full required CI on committed patch; repair all failures before closing AR-001.']
state['completed_this_session']=[{'task':'AR-001','implementation_status':'ACTIVE','changes':['canonical skill prompt constants','shared Excel 128-cell preflight through schema adapter client server backends','exact-set evidence-reader guards with historical measurements preserved','awaited adapter teardown and timeout/error-preservation regression'],'acceptance_status':'NOT_RUN'}]
state['working_tree']='Offline source snapshot verified by hash; guarded source changes applied to isolated CI checkout. User-PC working tree NOT_ACCESSIBLE.'
state['next_exact_action']='Run full Avalonia CI on the committed AR-001 source, inspect exact SHA and all suites/artifacts; repair AR-001 regressions on this same branch/PR #3. Do not advance AR-010 yet.'
p.write_text(text[:m.start(1)]+json.dumps(state,ensure_ascii=False,indent=2)+text[m.end(1):],encoding='utf-8')
p=Path('docs/agent-reliability/AR-001/implementation.md');p.parent.mkdir(parents=True,exist_ok=True)
p.write_text('''# AR-001 — contract repair and CI restoration

Source baseline: `e573394963ea44398217721ed5f200f899234d31`. Existing branch `feature/h2-agent-reliability-ar-000`, PR #3. Runtime baseline is unchanged from AR-000 main before this patch.

B01: production prompt uses canonical skill name constants. B02: ExcelPatchLimits owns 1..128 for schema, adapter, IPC client/server and both backends; oversize batches are rejected before writing or native discovery, never truncated. The optional enlarged fixture tests 128 distinct cells and unrelated-cell preservation. B03: exact-set guards include the already-shipped read_tool_output; historical MB-120 measurements remain intact and current 20 descriptors/seven executors are explicitly distinguished.

B11: the old fixture had a 10-second wait but a default 60-second command timeout. Synchronous disposal cancelled/disposed tokens without waiting for the execution's Finished barrier; Directory.Delete in finally could replace the primary exception. IAsyncDisposable now drains those existing execution tasks before releasing tokens; fixture owners await it before deletion, retain the primary error and preserve evidence on cleanup failure. The command-success case has an explicit 20-second tool/30-second harness budget. A forced 50ms timeout while an owned PowerShell process runs must remain TimeoutException, stop that process and allow deletion only after drain. Slow startup is a hypothesis for the historical masked failure, not a proven root cause. No new job/queue engine or full restart-recovery claim is introduced.

## Acceptance pending

Full CI runs `dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter AR-001` three times, then every existing suite and publish/helper smoke. E1/E2 use scripted transport, fixture backend and disposable local command. Invalid native count preflight does not access COM. E3/E4 Office/model/UI are NOT_RUN. AR-083 remains DEFERRED_BY_USER. Record actual CI before claiming completion.
''',encoding='utf-8')
require(set(git('diff','--name-only').splitlines()) <= set(OUTPUTS),'Unexpected edits')
Path(os.environ['RUNNER_TEMP'],'ar001-outputs.txt').write_text('\n'.join(OUTPUTS)+'\n',encoding='utf-8')
print('AR-001 source and registered tests applied; acceptance NOT_RUN until full CI executes.')
