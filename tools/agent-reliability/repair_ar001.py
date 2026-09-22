#!/usr/bin/env python3
"""AR-001 repairs from the completed 74-suite diagnostic; no runtime safety gate changes."""
from pathlib import Path
import datetime,json,os,re,subprocess
BASE='74418bee6c3b12c2be6fffa68a121a4ce36aa294'
BRANCH='feature/h2-agent-reliability-ar-000'
FILES=['experiments/H2AgentLab/Capabilities/MbRuntimeCapabilityInstallTests.cs',
 'experiments/H2AgentLab/Acceptance/MbMinimumBootableAgentAcceptanceTests.cs',
 'tools/agent-reliability/run_agent_suites.ps1',
 'docs/H2_AGENT_RELIABILITY_IMPLEMENTATION_TASKS.md']
def git(*args): return subprocess.check_output(['git',*args],text=True).strip()
def require(ok,message):
 if not ok: raise RuntimeError(message)
def replace(path,old,new):
 p=Path(path);text=p.read_text();require(text.count(old)==1,'Changed anchor: '+path+' / '+old[:80]);p.write_text(text.replace(old,new,1),encoding='utf-8')
require(os.environ['AR_BRANCH']==BRANCH and git('rev-parse','HEAD')==os.environ['AR_HEAD'],'Wrong branch/head')
require(not git('status','--porcelain'),'Dirty checkout')
if 'class InstalledPackageReadbackVerifier' in Path(FILES[0]).read_text():
 print('74-suite diagnostic repair already applied; no replay.');raise SystemExit(0)
for path in FILES:
 require(git('rev-parse','HEAD:'+path)==git('rev-parse',BASE+':'+path),'Source changed: '+path)
p=FILES[0]
replace(p,'using H2AgentLab.Transport;','using H2AgentLab.Transport;\nusing H2AgentLab.Verification;\nusing System.Security.Cryptography;')
replace(p,'            var transport = new InstallAndContinueTransport();','''            var verifier = new InstalledPackageReadbackVerifier(pluginManager, registry, skills,
                [toolOnly, skillOnly, providerTool]);
            Check(!verifier.IsInstalled(toolOnly.Manifest.Id), "Missing package was falsely verified.");
            var transport = new InstallAndContinueTransport();''')
replace(p,'''                new AgentContextManager(),
                registry);''','''                new AgentContextManager(),
                registry,
                verifier: verifier);''')
replace(p,'''                "Same-task deferred discovery did not load tools registered after install.");''','''                "Same-task deferred discovery did not load tools registered after install.");
            Check(verifier.ObservedInstallCount == 3 && result.VerificationHistory.Count == 3
                && result.VerificationHistory.All(report => report.Passed),
                "Each actual install must have a separate independent activation/payload readback.");
            var active = pluginManager.GetActive(providerTool.Manifest.Id)!.Value;
            var payload = Path.Combine(active.VersionRoot, "tools.json");
            var acceptedBytes = File.ReadAllBytes(payload);
            File.WriteAllText(payload, "tampered fixture bytes");
            Check(!verifier.IsInstalled(providerTool.Manifest.Id), "Tampered installed payload was falsely verified.");
            File.WriteAllBytes(payload, acceptedBytes);
            Check(verifier.IsInstalled(providerTool.Manifest.Id), "Restored exact payload failed readback.");''')
replace(p,'''            [],
            AgentTaskRiskClass.Medium,
            new AgentVerificationPolicy(requireVerification: false));''','''            [new AgentAcceptanceCriterion("mb74.activation-readback", "Requested package bytes and callable registrations match the selected fixture.")],
            AgentTaskRiskClass.Medium,
            new AgentVerificationPolicy(requireVerification: true,
                requiredVerifierIds: ["mb74-package-readback"]));''')
replace(p,'    private sealed class FixturePluginResolver : IPluginToolExecutorResolver','''    private sealed class InstalledPackageReadbackVerifier : IAgentRuntimeVerifier
    {
        private readonly PluginManager _plugins;
        private readonly ToolRegistry _registry;
        private readonly H2AgentLab.Skills.SkillCatalog _skills;
        private readonly Dictionary<string, (BuiltPluginPackage Package, Dictionary<string, string> Hashes)> _expected = new(StringComparer.Ordinal);
        private readonly HashSet<string> _observed = new(StringComparer.Ordinal);
        public int ObservedInstallCount => _observed.Count;
        public InstalledPackageReadbackVerifier(PluginManager plugins, ToolRegistry registry,
            H2AgentLab.Skills.SkillCatalog skills, BuiltPluginPackage[] packages)
        {
            _plugins = plugins; _registry = registry; _skills = skills;
            foreach (var package in packages)
            {
                using var zip = ZipFile.OpenRead(package.Path);
                var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var entry in zip.Entries.Where(entry => !entry.FullName.EndsWith('/')))
                {
                    using var stream = entry.Open();
                    hashes.Add(entry.FullName, Convert.ToHexString(SHA256.HashData(stream)));
                }
                _expected.Add(package.Manifest.Id, (package, hashes));
            }
        }
        public bool IsInstalled(string id)
        {
            if (!_expected.TryGetValue(id, out var expected)) return false;
            var active = _plugins.GetActive(id);
            if (active is null || active.Value.Manifest.Version != expected.Package.Manifest.Version) return false;
            foreach (var entry in expected.Hashes)
            {
                var file = Path.Combine(active.Value.VersionRoot, entry.Key.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) != entry.Value) return false;
            }
            if (expected.Package.ToolName is { } tool && (!_registry.TryGet(tool, out var descriptor)
                || descriptor.Provenance?.ProviderId != "plugin." + id || descriptor.IsMutating)) return false;
            if (expected.Package.SkillId is { } skill && !_skills.Search("", 20)
                .Any(item => item.Identity.PluginId == id && item.Identity.SkillId == skill)) return false;
            return true;
        }
        public Task<VerificationReport?> VerifyAsync(AgentRuntimeVerificationContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var calls = context.Calls.Where(call => call.Name == CatalogRuntimeToolExecutor.InstallToolName).ToArray();
            if (calls.Length == 0) return Task.FromResult<VerificationReport?>(null);
            foreach (var call in calls) _observed.Add(call.Arguments.GetProperty("plugin_id").GetString()!);
            var passed = _observed.All(IsInstalled);
            const string criterion = "mb74.activation-readback";
            string[] evidence = ["fixture:installed-package-bytes-and-registry"];
            return Task.FromResult<VerificationReport?>(new VerificationReport("mb74-package-readback",
                [new VerificationCriterionResult(criterion,
                    passed ? VerificationCriterionStatus.Passed : VerificationCriterionStatus.Failed,
                    evidence, passed ? null : new VerificationFailure(criterion,
                        "Active package payload, version or registry/skill contribution differs from selected fixture.", evidence))]));
        }
    }

    private sealed class FixturePluginResolver : IPluginToolExecutorResolver''')
p=FILES[1]
replace(p,'''                "AgentRuntime did not recover from the tool error through deferred discovery and a corrected tool call.");
        }
    }''','''                "AgentRuntime did not recover from the tool error through deferred discovery and a corrected tool call.");
        }
        // Preserve the old fully invented-name case as a negative. An unrelated successful
        // read cannot clear an unknown operation that has no host-selected recovery candidate.
        var deniedRegistry = new ToolRegistry();
        deniedRegistry.Register(ReadTool());
        var deniedTransport = new ToolErrorRecoveryTransport("invented.tool", expectCandidate: false);
        await using var deniedRuntime = new AgentRuntime(deniedTransport, new AgentContextManager(), deniedRegistry);
        var blocked = false;
        try { await deniedRuntime.RunAsync(Request("Do not clear an unrelated unknown operation."), CancellationToken.None); }
        catch (AgentVerificationRequiredException ex) { blocked = ex.Message.Contains("invented.tool", StringComparison.Ordinal); }
        if (!blocked || !deniedTransport.SawUnknownToolError || !deniedTransport.SawReadResult)
            throw new InvalidOperationException("Unrelated success cleared the original unknown operation, or the negative did not execute.");
    }''')
replace(p,'    private sealed class ToolErrorRecoveryTransport : IAgentTransport','''    private sealed class ToolErrorRecoveryTransport(string invalidName = "fixture.read_missing", bool expectCandidate = true) : IAgentTransport''')
replace(p,'                "invented.tool",\n                "{}"));','                invalidName,\n                "{}"));')
replace(p,'''                if (!SawUnknownToolError)
                    throw new InvalidOperationException(''','''                using var errorJson = JsonDocument.Parse(error.Content);
                var candidate = errorJson.RootElement.GetProperty("recoveryTools").EnumerateArray()
                    .Any(item => item.GetString() == "fixture.read");
                if (candidate != expectCandidate)
                    throw new InvalidOperationException("Recovery candidate does not match the tested host policy.");
                if (!SawUnknownToolError)
                    throw new InvalidOperationException(''')
replace(p,'''                if (request.NewlyLoadedTools?.Single().Name != "fixture.read")
                    throw new InvalidOperationException(
                        "Recovery tool_search did not load fixture.read.");''','''                // Unknown-tool recovery may already load the exact candidate. Explicit
                // discovery must select it without resending a duplicate schema.
                if (!request.ToolResults.Single().Content.Contains("fixture.read", StringComparison.Ordinal)
                    || expectCandidate && (request.NewlyLoadedTools?.Count ?? 0) != 0
                    || !expectCandidate && request.NewlyLoadedTools?.Single().Name != "fixture.read")
                    throw new InvalidOperationException("Recovery discovery did not preserve selected/cached schema identity.");''')
p=FILES[2]
replace(p,'''(?<flag>--[a-z0-9-]+) \$out')''','''(?<flag>--[a-z0-9-]+) \$out(?<helpers>[^\r\n]*)')''')
replace(p,'$results = @()','''$desktop = (Resolve-Path 'experiments/H2AgentLab.DesktopHost/bin/Release/net10.0-windows/H2AgentLab.DesktopHost.exe').Path
$office = (Resolve-Path 'experiments/H2AgentLab.OfficeHost/bin/Release/net10.0-windows/H2AgentLab.OfficeHost.exe').Path
$results = @()''')
replace(p,'''    & dotnet run --project experiments/H2AgentLab/H2AgentLab.csproj -c Release --no-build -- $flag $directory 2>&1 | Tee-Object -FilePath $log''','''    $helperText = ($matches | Where-Object { $_.Groups['flag'].Value -eq $flag } | Select-Object -First 1).Groups['helpers'].Value.Trim()
    $helperArgs = @()
    if ($helperText.Length -gt 0) {
        foreach ($token in ($helperText -split '\\s+')) {
            if ($token -eq '$desktopHostExe') { $helperArgs += $desktop }
            elseif ($token -eq '$officeHostExe') { $helperArgs += $office }
            else { throw "Unrecognized helper argument in required workflow: $token" }
        }
    }
    & dotnet run --project experiments/H2AgentLab/H2AgentLab.csproj -c Release --no-build -- $flag $directory @helperArgs 2>&1 | Tee-Object -FilePath $log''')
p=Path(FILES[3]);text=p.read_text();m=re.search(r'```yaml\n(\{\n.*?\n\})\n```',text,re.S);require(m is not None,'Missing checkpoint');state=json.loads(m[1]);require(state['active_task']=='AR-001','Wrong task')
state['last_code_commit']=BASE;state['last_validated_code_commit']=BASE;state['last_validation_result']='74_SUITE_DIAGNOSTIC_TWO_FIXTURE_FAILURES_AND_SIX_HELPER_ARGUMENT_ERRORS'
state['checkpoint_saved_at_utc']=datetime.datetime.now(datetime.timezone.utc).isoformat()
state['ci_runs'].append({'id':35693929260,'code_sha':BASE,'result':'FAILURE','AR001':'13/13 x3','Agent_suites':74,'failed_flags':['--mb-runtime-capability-install-test','--mb-minimum-bootable-agent-acceptance-test'],'harness_missing_helper_flags':6,'artifact_id':10679995582,'artifact_sha256':'9a83822aab1f870d2bafd25842787abc76ea79ce00ac6dc0022318b90859cce7'})
state['remaining_in_active_task']=['Validate independent installed-package payload verifier and candidate-bound positive/negative recovery fixtures; rerun all 74 suites with helper arguments, full CI and real publish/helper smoke.']
state['next_exact_action']='Inspect exact new SHA full CI and 74-suite diagnostic. Do not weaken completion or unrelated-error recovery guards. Native Office/model E3/E4 remain NOT_RUN; AR-083 deferred.'
p.write_text(text[:m.start(1)]+json.dumps(state,ensure_ascii=False,indent=2)+text[m.end(1):],encoding='utf-8')
require(set(git('diff','--name-only').splitlines())==set(FILES),'Unexpected edits')
Path(os.environ['RUNNER_TEMP'],'ar001-outputs.txt').write_text('\n'.join(FILES)+'\n')
print('Applied observed MB-74/MB-100 fixture and helper-runner corrections; full acceptance pending.')
