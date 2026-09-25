using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using H2AgentLab.DesktopProtocol;

namespace H2AgentLab.DesktopHost;

public sealed class DesktopHostServer
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _pipeName;
    private readonly IDesktopBackend _backend;
    private readonly bool _fixtureMode;

    public DesktopHostServer(
        string pipeName,
        IDesktopBackend backend,
        bool fixtureMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (pipeName.Length > 180 || pipeName.Any(char.IsControl))
            throw new ArgumentException("DesktopHost pipe name is invalid.", nameof(pipeName));
        _pipeName = pipeName;
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _fixtureMode = fixtureMode;
    }

    /// <summary>
    /// Synchronous on Program.Main's STA thread so UI Automation/Win32 interactions do not hop
    /// onto arbitrary thread-pool apartments.
    /// </summary>
    public void Run(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.CurrentUserOnly);

            server.WaitForConnection();
            cancellationToken.ThrowIfCancellationRequested();
            HandleConnection(server, cancellationToken);
        }
    }

    private void HandleConnection(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 16 * 1024,
            leaveOpen: true);
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(false),
            16 * 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        var line = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(line))
        {
            Write(writer, Error("", "invalid_request", "DesktopHost request is empty."));
            return;
        }
        if (Encoding.UTF8.GetByteCount(line) > DesktopProtocolConstants.MaxMessageBytes)
        {
            Write(writer, Error("", "request_too_large", "DesktopHost request exceeds protocol size limit."));
            return;
        }

        DesktopRpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<DesktopRpcRequest>(line, Json);
        }
        catch (JsonException)
        {
            Write(writer, Error("", "invalid_json", "DesktopHost request JSON is invalid."));
            return;
        }

        if (request is null
            || string.IsNullOrWhiteSpace(request.Id)
            || string.IsNullOrWhiteSpace(request.Method))
        {
            Write(writer, Error(request?.Id ?? "", "invalid_request", "DesktopHost request requires id and method."));
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        DesktopRpcResponse response;
        try
        {
            var result = Dispatch(request);
            response = new DesktopRpcResponse(
                request.Id,
                true,
                JsonSerializer.SerializeToElement(result, result.GetType(), Json),
                null);
        }
        catch (DesktopHostFaultException ex)
        {
            response = Error(request.Id, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            response = Error(request.Id, "host_error", Sanitize(ex.Message));
        }

        Write(writer, response);
    }

    private object Dispatch(DesktopRpcRequest request)
        => request.Method switch
        {
            "ping" => new DesktopPingResult(
                Environment.ProcessId,
                DesktopProtocolConstants.Version,
                _fixtureMode,
                Thread.CurrentThread.GetApartmentState() == ApartmentState.STA),

            "desktop.list" => _backend.ListWindows(),
            "desktop.launch_app" => _backend.LaunchApplication(
                Parameters<DesktopApplicationLaunchRequest>(request)),
            "desktop.wait_app" => _backend.WaitForApplicationWindow(
                Parameters<DesktopApplicationWaitRequest>(request)),
            "desktop.activate_window" => _backend.ActivateWindow(
                Parameters<DesktopApplicationActivateRequest>(request)),
            "desktop.observe" => _backend.Observe(
                Parameters<DesktopObserveRequest>(request).SessionId),
            "desktop.act" => _backend.Act(
                Parameters<DesktopActionRequest>(request)),

            _ => throw new DesktopHostFaultException(
                "unknown_method",
                $"DesktopHost method '{request.Method}' is not supported.")
        };

    private static T Parameters<T>(DesktopRpcRequest request)
        where T : class
    {
        try
        {
            return request.Parameters.Deserialize<T>(Json)
                ?? throw new DesktopHostFaultException(
                    "invalid_request",
                    $"Missing parameters for '{request.Method}'.");
        }
        catch (JsonException ex)
        {
            throw new DesktopHostFaultException(
                "invalid_request",
                "DesktopHost parameters are invalid: " + Sanitize(ex.Message));
        }
    }

    private static DesktopRpcResponse Error(
        string id,
        string code,
        string message)
        => new(id, false, null, new DesktopRpcError(code, Sanitize(message)));

    private static void Write(
        StreamWriter writer,
        DesktopRpcResponse response)
    {
        var json = JsonSerializer.Serialize(response, Json);
        if (Encoding.UTF8.GetByteCount(json) > DesktopProtocolConstants.MaxMessageBytes)
            json = JsonSerializer.Serialize(
                Error(response.Id, "response_too_large", "DesktopHost response exceeds protocol size limit."),
                Json);
        writer.WriteLine(json);
    }

    private static string Sanitize(string value)
    {
        value ??= "";
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 1_000 ? value : value[..1_000];
    }
}
