#!/usr/bin/env python3
"""Repair the Phase-11 contract drift actually observed after the AR-001 core tests pass.
Only exact reviewed source files are modified; no live Web/Office/model request is made.
"""
from pathlib import Path
import os
import subprocess
BASE = 'c26b3521ce4b7d60e69f3942dda123c23fd53cf3'
BRANCH = 'feature/h2-agent-reliability-ar-000'
FILES = ['experiments/H2AgentLab/Phase11/V2Phase11Tests.cs',
         'experiments/H2AgentLab.OfficeHost/ComOfficeBackend.cs',
         'experiments/H2AgentLab.OfficeHost/FixtureOfficeBackend.cs',
         'docs/agent-reliability/AR-001/implementation.md']
def git(*args): return subprocess.check_output(['git', *args], text=True).strip()
def require(ok, message):
    if not ok: raise RuntimeError(message)
def replace(path, old, new):
    p=Path(path); text=p.read_text(encoding='utf-8')
    require(text.count(old)==1, 'Source anchor changed: '+path)
    p.write_text(text.replace(old,new,1),encoding='utf-8')
require(os.environ['AR_BRANCH']==BRANCH and git('rev-parse','HEAD')==os.environ['AR_HEAD'],'Wrong branch/head')
require(not git('status','--porcelain'),'Dirty tree; do not overwrite other work')
if 'AR-001 feed execution contract' in Path(FILES[0]).read_text():
    print('Phase-11 AR-001 correction already applied; no replay.')
    raise SystemExit(0)
for path in FILES:
    require(git('rev-parse','HEAD:'+path)==git('rev-parse',BASE+':'+path),'Changed source requires reconciliation: '+path)
replace(FILES[0], '                "web.extract", "web.get_metadata", "web.open_browser"',
        '                "web.extract", "web.read_feed", "web.get_metadata", "web.open_browser"')
replace(FILES[0], '''                "WebResearchHost did not lazily load selected schema only.");

            var search''', '''                "WebResearchHost did not lazily load selected schema only.");

            // AR-001 feed execution contract: keep exact inventory/order and prove the added
            // callable uses the existing backend/parser rather than weakening the guard.
            var feedDefinitions = await host.LoadToolDefinitionsAsync(["web.read_feed"], CancellationToken.None);
            Check(feedDefinitions.Count == 1 && feedDefinitions[0].Summary.Name == "web.read_feed"
                && feedDefinitions[0].Summary.Access == AgentToolAccess.ReadOnly
                && feedDefinitions[0].Summary.SupportsParallel
                && feedDefinitions[0].Summary.ResourceScope == "web:public",
                "Feed schema was eagerly mixed with other tools or changed permission metadata.");
            WebFeedPage? observedFeed = null;
            host.FeedObserved = page => observedFeed = page;
            using var feed = JsonDocument.Parse(await host.ExecuteToolAsync("web.read_feed",
                JsonSerializer.SerializeToElement(new { url = "https://feed.example.test/ar001.xml", max_items = 1 }),
                CancellationToken.None));
            Check(feed.RootElement.GetProperty("Items").GetArrayLength() == 1
                && feed.RootElement.GetProperty("Items")[0].GetProperty("Title").GetString() == "Tin thử AR-001"
                && feed.RootElement.GetProperty("Items")[0].GetProperty("Url").GetString() == "https://feed.example.test/item-1"
                && observedFeed is { TotalItems: 1 } && observedFeed.Items.Count == 1,
                "Feed execution lost source content, paging bound or host observation.");

            var search''')
replace(FILES[0], '''            var official = url.Contains("gov.vn", StringComparison.OrdinalIgnoreCase);''', '''            if (url == "https://feed.example.test/ar001.xml")
                return Task.FromResult(new WebFetchedDocument(url, "application/rss+xml",
                    Encoding.UTF8.GetBytes("<rss version=\\"2.0\\"><channel><title>Fixture</title><item><title>Tin thử AR-001</title><link>https://feed.example.test/item-1</link><description>Observed fixture only</description></item></channel></rss>"),
                    "Fixture feed", "Fixture", null, null));
            var official = url.Contains("gov.vn", StringComparison.OrdinalIgnoreCase);''')
for path in FILES[1:3]:
    replace(path, '''        if (ExcelPatchLimits.ValidationError(request.Cells?.Count ?? -1) is { } problem)
            throw new OfficeHostFaultException(ExcelPatchLimits.ErrorCode, problem);''', '''        var countError = ExcelPatchLimits.ValidationError(request.Cells?.Count ?? -1);
        if (request.Cells is null || countError is not null)
            throw new OfficeHostFaultException(ExcelPatchLimits.ErrorCode, countError ?? "Excel patch cells are required.");''')
report=Path(FILES[3])
report.write_text(report.read_text()+'''

## Full CI uncovered another pre-existing exact-inventory mismatch

Source c26b3521ce4b7d60e69f3942dda123c23fd53cf3 passed the AR-001 corpus 13/13 in all three focused repetitions (run 35692162013; artifact digest b26040954154ed315355978edcad3e09e8267c85be2855a7cb863f8735cf79f8). Full run 35692347728 passed H2 603/603, architecture 35/35 and all preceding suites, then Phase-11 case 1108 failed: its exact WebResearchHost list omitted the already-implemented web.read_feed. Phase-11 was 14 passed / 1 failed; downstream MB suites and publish were NOT_RUN. The corrected test retains exact ordering and selected-only schema checks, adds read-only/scope assertions, and executes the existing feed backend/parser/observation path using a synthetic feed. No actual search/browser connection is installed or claimed. Native/fixture Excel null preflight is made explicit to remove the two new nullable warnings without changing its rejection contract. This repair remains AR-001 CI restoration; AR-060 live Web acceptance stays open.
''',encoding='utf-8')
require(set(git('diff','--name-only').splitlines())==set(FILES),'Unexpected changed files')
Path(os.environ['RUNNER_TEMP'],'ar001-outputs.txt').write_text('\n'.join(FILES)+'\n')
print('Applied four-file AR-001 full-CI repair; acceptance still pending.')
