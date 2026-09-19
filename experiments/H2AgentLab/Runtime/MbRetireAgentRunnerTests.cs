namespace H2AgentLab.Runtime;

public static class MbRetireAgentRunnerTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-90 test directory.");
        Directory.CreateDirectory(root);

        var lines = new List<string>();
        var failed = 0;

        async Task Test(string name, Func<Task> action)
        {
            try
            {
                await action();
                lines.Add("PASS " + name);
            }
            catch (Exception ex)
            {
                failed++;
                lines.Add("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

        await Test("MB-90 repository guard confines AgentRunner to baseline tests and live-evaluation harnesses", () =>
        {
            var repo = FindRepoRoot();
            var sourceRoot = Path.Combine(repo, "experiments", "H2AgentLab");
            var offenders = Directory
                .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !IsGeneratedPath(path))
                .Where(path => ContainsLegacyRunnerCodeToken(File.ReadAllText(path)))
                .Where(path => !IsAllowedLegacyHarness(path))
                .Select(path => Path.GetRelativePath(repo, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            Check(
                offenders.Length == 0,
                "Production C# source references retired AgentRunner: " + string.Join(", ", offenders));

            var legacy = Path.Combine(sourceRoot, "AgentRunner.cs");
            Check(File.Exists(legacy),
                "Frozen v1 AgentRunner baseline implementation disappeared before legacy cleanup is complete.");
            return Task.CompletedTask;
        });

        await Test("MB-90 LabWindow normal execution is wired only to AgentRuntime", () =>
        {
            var repo = FindRepoRoot();
            var window = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "LabWindow.cs"));
            var facade = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Tasking", "AgentOrchestratedRun.cs"));
            var orchestrator = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Tasking", "AgentOrchestrator.cs"));

            Check(window.Contains("new AgentOrchestratedRun", StringComparison.Ordinal),
                "LabWindow no longer enters the v2 runtime facade.");
            Check(!window.Contains("AgentRunner", StringComparison.Ordinal),
                "LabWindow directly references retired AgentRunner.");
            Check(facade.Contains("CreateRuntime", StringComparison.Ordinal)
                    && facade.Contains("RunRuntimeAsync", StringComparison.Ordinal),
                "AgentOrchestratedRun is not wired through AgentRuntime.");
            Check(!facade.Contains("AgentRunner", StringComparison.Ordinal),
                "AgentOrchestratedRun references retired AgentRunner.");
            Check(orchestrator.Contains("RunRuntimeAsync", StringComparison.Ordinal)
                    && !orchestrator.Contains("AgentRunner", StringComparison.Ordinal)
                    && !orchestrator.Contains("CreateCompatibilityRunner", StringComparison.Ordinal),
                "Production AgentOrchestrator still exposes a legacy runner path.");
            return Task.CompletedTask;
        });

        await Test("MB-90 AgentOrchestrator API cannot construct or return AgentRunner", () =>
        {
            var legacyType = typeof(global::H2AgentLab.AgentRunner);
            var orchestratorType = typeof(Tasking.AgentOrchestrator);

            var constructorLeaks = orchestratorType
                .GetConstructors()
                .SelectMany(x => x.GetParameters())
                .Where(x => x.ParameterType == legacyType
                    || x.ParameterType.GenericTypeArguments.Contains(legacyType))
                .Select(x => x.Name ?? x.ParameterType.Name)
                .ToArray();
            var methodLeaks = orchestratorType
                .GetMethods()
                .Where(x => x.ReturnType == legacyType
                    || x.ReturnType.GenericTypeArguments.Contains(legacyType))
                .Select(x => x.Name)
                .ToArray();

            Check(constructorLeaks.Length == 0,
                "AgentOrchestrator constructor still accepts AgentRunner: " + string.Join(", ", constructorLeaks));
            Check(methodLeaks.Length == 0,
                "AgentOrchestrator API still returns AgentRunner: " + string.Join(", ", methodLeaks));
            Check(orchestratorType.GetMethod("CreateCompatibilityRunner") is null,
                "CreateCompatibilityRunner remains reachable from production orchestration.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-retire-agent-runner-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static bool ContainsLegacyRunnerCodeToken(string source)
    {
        foreach (var rawLine in source.Split('\n'))
        {
            var line = rawLine;
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
                line = line[..comment];

            var searchFrom = 0;
            while (searchFrom < line.Length)
            {
                var index = line.IndexOf("AgentRunner", searchFrom, StringComparison.Ordinal);
                if (index < 0)
                    break;

                var beforeIsWord = index > 0
                    && (char.IsLetterOrDigit(line[index - 1]) || line[index - 1] == '_');
                var afterIndex = index + "AgentRunner".Length;
                var afterIsWord = afterIndex < line.Length
                    && (char.IsLetterOrDigit(line[afterIndex]) || line[afterIndex] == '_');

                if (!beforeIsWord
                    && !afterIsWord
                    && !IsInsideQuotedString(line, index))
                    return true;

                searchFrom = afterIndex;
            }
        }

        return false;
    }

    private static bool IsInsideQuotedString(string line, int index)
    {
        var quoted = false;
        for (var i = 0; i < index; i++)
        {
            if (line[i] != '"')
                continue;

            var backslashes = 0;
            for (var j = i - 1; j >= 0 && line[j] == '\\'; j--)
                backslashes++;

            if (backslashes % 2 == 0)
                quoted = !quoted;
        }

        return quoted;
    }

    private static bool IsAllowedLegacyHarness(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("AgentRunner.cs", StringComparison.Ordinal)
            || name.EndsWith("Tests.cs", StringComparison.Ordinal)
            || name.EndsWith("LiveEvaluation.cs", StringComparison.Ordinal);
    }

    private static bool IsGeneratedPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(current.FullName, "experiments")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
