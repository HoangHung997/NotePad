namespace H2AgentLab.DesktopHost;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var selfTest = Array.IndexOf(args, "--self-test");
            if (selfTest >= 0)
            {
                if (selfTest + 1 >= args.Length)
                    throw new ArgumentException("--self-test requires an output directory.");
                return DesktopHostSelfTests.Run(args[selfTest + 1]);
            }

            var pipeIndex = Array.IndexOf(args, "--pipe");
            if (pipeIndex < 0 || pipeIndex + 1 >= args.Length)
            {
                Console.Error.WriteLine("Usage: H2AgentLab.DesktopHost --pipe <name> [--fixture]");
                return 2;
            }

            var fixture = args.Contains("--fixture", StringComparer.Ordinal);
            IDesktopBackend backend = fixture
                ? new FixtureDesktopBackend()
                : new Win32DesktopBackend();

            using var shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                shutdown.Cancel();
            };

            new DesktopHostServer(
                args[pipeIndex + 1],
                backend,
                fixture).Run(shutdown.Token);
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
