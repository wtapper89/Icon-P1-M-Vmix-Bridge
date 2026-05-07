using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IconP1MVmixBridge;

public sealed class BridgeApiServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly FileLogger _logger;
    private readonly Func<ApiSnapshot> _getSnapshot;
    private readonly Func<int, ApiAssignmentRequest, Task<ApiAssignmentResponse>> _setAssignmentAsync;
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private int _port;

    public BridgeApiServer(
        FileLogger logger,
        Func<ApiSnapshot> getSnapshot,
        Func<int, ApiAssignmentRequest, Task<ApiAssignmentResponse>> setAssignmentAsync)
    {
        _logger = logger;
        _getSnapshot = getSnapshot;
        _setAssignmentAsync = setAssignmentAsync;
    }

    public void Start(int port)
    {
        Stop();
        _port = port;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
        _logger.Info("Bridge assignment API listening at http://127.0.0.1:{0}/api", port);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _cts?.Dispose();
        _cts = null;
        _listener = null;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Assignment API accept failed");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(requestLine))
                return;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
            {
                var separator = line.IndexOf(':');
                if (separator > 0)
                    headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }

            var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                await WriteJsonAsync(stream, 400, new ApiError("Invalid request line"), cancellationToken).ConfigureAwait(false);
                return;
            }

            var method = parts[0].ToUpperInvariant();
            var path = parts[1].Split('?', 2)[0].TrimEnd('/');
            var contentLength = headers.TryGetValue("Content-Length", out var contentLengthText) &&
                int.TryParse(contentLengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLength)
                    ? Math.Max(0, parsedLength)
                    : 0;
            var body = contentLength > 0
                ? new string(await ReadBodyAsync(reader, contentLength, cancellationToken).ConfigureAwait(false))
                : "";

            _logger.Debug("Assignment API {0} {1}", method, path);
            await RouteAsync(stream, method, path, body, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Assignment API request failed");
        }
    }

    private async Task RouteAsync(Stream stream, string method, string path, string body, CancellationToken cancellationToken)
    {
        if (method == "GET" && path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(stream, 200, new { ok = true, port = _port }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (method == "OPTIONS")
        {
            await WriteJsonAsync(stream, 200, new { ok = true }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path.Equals("/api/assignments", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(stream, 200, _getSnapshot(), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path.Equals("/api/inputs", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(stream, 200, _getSnapshot().Inputs, cancellationToken).ConfigureAwait(false);
            return;
        }

        if ((method == "PUT" || method == "POST") &&
            path.StartsWith("/api/channels/", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(path["/api/channels/".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel))
        {
            var request = string.IsNullOrWhiteSpace(body)
                ? new ApiAssignmentRequest()
                : JsonSerializer.Deserialize<ApiAssignmentRequest>(body, JsonOptions) ?? new ApiAssignmentRequest();
            var response = await _setAssignmentAsync(channel, request).ConfigureAwait(false);
            await WriteJsonAsync(stream, response.Success ? 200 : 400, response, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(stream, 404, new ApiError("Not found"), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<char[]> ReadBodyAsync(TextReader reader, int contentLength, CancellationToken cancellationToken)
    {
        var buffer = new char[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(read, contentLength - read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;
            read += count;
        }

        return read == contentLength ? buffer : buffer[..read];
    }

    private static async Task WriteJsonAsync(Stream stream, int statusCode, object payload, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(payload, JsonOptions);
        var reason = statusCode switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            _ => "Error"
        };
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: GET, POST, PUT, OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => Stop();
}

public sealed record ApiSnapshot(
    IReadOnlyList<ApiChannelAssignment> Channels,
    IReadOnlyList<ApiInput> Inputs,
    IReadOnlyList<string> AssignmentKinds,
    IReadOnlyList<string> StripColors);

public sealed record ApiInput(int Number, string Key, string Title);

public sealed record ApiChannelAssignment(
    int Channel,
    string Kind,
    string? InputKey,
    string LabelOverride,
    string StripColor);

public sealed class ApiAssignmentRequest
{
    public string? Kind { get; set; }
    public string? InputKey { get; set; }
    public int? InputNumber { get; set; }
    public string? InputTitle { get; set; }
    public string? LabelOverride { get; set; }
    public string? StripColor { get; set; }
}

public sealed record ApiAssignmentResponse(bool Success, string Message, ApiChannelAssignment? Channel = null);

public sealed record ApiError(string Error);
