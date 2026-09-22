namespace H2AgentLab.OfficeHost;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var pipeIndex = Array.IndexOf(args, "--pipe");
            if (pipeIndex < 0 || pipeIndex + 1 >= args.Length || string.IsNullOrWhiteSpace(args[pipeIndex + 1]))
            {
                Console.Error.WriteLine("Usage: H2AgentLab.OfficeHost --pipe <name> [--fixture]");
                return 2;
            }

            var pipeName = args[pipeIndex + 1];
            var fixture = args.Contains("--fixture", StringComparer.Ordinal);
            IOfficeBackend backend = fixture
                ? new FixtureOfficeBackend()
                : new ComOfficeBackend();

            using var shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                shutdown.Cancel();
            };

            try { new OfficeHostServer(pipeName, backend, fixture).Run(shutdown.Token); }
            finally { (backend as IDisposable)?.Dispose(); }
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }
}
