using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace IconP1MVmixBridge;

public sealed class VMixClient : IDisposable
{
    private readonly FileLogger _logger;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly SemaphoreSlim _tcpLock = new(1, 1);
    private TcpClient? _tcpClient;
    private StreamReader? _tcpReader;
    private StreamWriter? _tcpWriter;
    private string _host = "127.0.0.1";
    private int _httpPort = 8088;
    private int _tcpPort = 8099;

    public VMixClient(FileLogger logger)
    {
        _logger = logger;
    }

    public void Configure(string host, int httpPort, int tcpPort)
    {
        if (_host == host && _httpPort == httpPort && _tcpPort == tcpPort)
            return;

        _host = host;
        _httpPort = httpPort;
        _tcpPort = tcpPort;
        DisconnectTcp();
        _logger.Info("Configured vMix target {0}, HTTP {1}, TCP {2}", _host, _httpPort, _tcpPort);
    }

    public async Task<VMixState> GetStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var uri = new UriBuilder("http", _host, _httpPort, "api/").Uri;
            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            return ParseState(doc);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or WebException)
        {
            _logger.Warn("vMix state poll failed: {0}", ex.Message);
            return new VMixState { Connected = false, Status = ex.Message };
        }
    }

    public async Task SetAssignmentVolumeAsync(ChannelAssignment assignment, double volumePercent, CancellationToken cancellationToken)
    {
        volumePercent = Math.Clamp(volumePercent, 0, 100);
        var value = volumePercent.ToString("0.##", CultureInfo.InvariantCulture);
        var command = assignment.Kind switch
        {
            AssignmentKind.Input when !string.IsNullOrWhiteSpace(assignment.InputKey) =>
                $"FUNCTION SetVolume Input={UrlEncode(assignment.InputKey!)}&Value={value}",
            AssignmentKind.Master => $"FUNCTION SetMasterVolume Value={value}",
            AssignmentKind.BusA => $"FUNCTION SetBusAVolume Value={value}",
            AssignmentKind.BusB => $"FUNCTION SetBusBVolume Value={value}",
            AssignmentKind.BusC => $"FUNCTION SetBusCVolume Value={value}",
            AssignmentKind.BusD => $"FUNCTION SetBusDVolume Value={value}",
            AssignmentKind.BusE => $"FUNCTION SetBusEVolume Value={value}",
            AssignmentKind.BusF => $"FUNCTION SetBusFVolume Value={value}",
            AssignmentKind.BusG => $"FUNCTION SetBusGVolume Value={value}",
            _ => ""
        };

        if (string.IsNullOrWhiteSpace(command))
            return;

        await SendTcpCommandAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetAssignmentVolumeFastAsync(ChannelAssignment assignment, double volumePercent, CancellationToken cancellationToken)
    {
        volumePercent = Math.Clamp(volumePercent, 0, 100);
        var value = volumePercent.ToString("0.##", CultureInfo.InvariantCulture);
        var function = assignment.Kind switch
        {
            AssignmentKind.Input when !string.IsNullOrWhiteSpace(assignment.InputKey) => "SetVolume",
            AssignmentKind.Master => "SetMasterVolume",
            AssignmentKind.BusA => "SetBusAVolume",
            AssignmentKind.BusB => "SetBusBVolume",
            AssignmentKind.BusC => "SetBusCVolume",
            AssignmentKind.BusD => "SetBusDVolume",
            AssignmentKind.BusE => "SetBusEVolume",
            AssignmentKind.BusF => "SetBusFVolume",
            AssignmentKind.BusG => "SetBusGVolume",
            _ => ""
        };

        if (string.IsNullOrWhiteSpace(function))
            return;

        var builder = new UriBuilder("http", _host, _httpPort, "api/")
        {
            Query = assignment.Kind == AssignmentKind.Input
                ? $"Function={function}&Input={UrlEncode(assignment.InputKey!)}&Value={value}"
                : $"Function={function}&Value={value}"
        };

        using var response = await _httpClient.GetAsync(builder.Uri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            _logger.Warn("vMix HTTP fader command failed: {0} {1}", (int)response.StatusCode, response.ReasonPhrase);
    }

    public async Task ToggleMuteAsync(ChannelAssignment assignment, CancellationToken cancellationToken)
    {
        var command = assignment.Kind switch
        {
            AssignmentKind.Input when !string.IsNullOrWhiteSpace(assignment.InputKey) =>
                $"FUNCTION Audio Input={UrlEncode(assignment.InputKey!)}",
            AssignmentKind.Master => "FUNCTION MasterAudio",
            AssignmentKind.BusA => "FUNCTION BusAAudio",
            AssignmentKind.BusB => "FUNCTION BusBAudio",
            AssignmentKind.BusC => "FUNCTION BusCAudio",
            AssignmentKind.BusD => "FUNCTION BusDAudio",
            AssignmentKind.BusE => "FUNCTION BusEAudio",
            AssignmentKind.BusF => "FUNCTION BusFAudio",
            AssignmentKind.BusG => "FUNCTION BusGAudio",
            _ => ""
        };

        if (!string.IsNullOrWhiteSpace(command))
            await SendTcpCommandAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendTcpCommandAsync(string command, CancellationToken cancellationToken)
    {
        await _tcpLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureTcpConnectedAsync(cancellationToken).ConfigureAwait(false);
            _logger.Debug("vMix TCP > {0}", command);
            await _tcpWriter!.WriteAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _tcpWriter.WriteAsync("\r\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            await _tcpWriter.FlushAsync(cancellationToken).ConfigureAwait(false);

            var response = await _tcpReader!.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            _logger.Debug("vMix TCP < {0}", response ?? "<closed>");
            if (response is null || response.Contains(" ER ", StringComparison.OrdinalIgnoreCase))
                throw new IOException($"vMix rejected command: {response ?? "connection closed"}");
        }
        catch (Exception ex)
        {
            DisconnectTcp();
            _logger.Error(ex, "vMix TCP command failed: {0}", command);
            throw;
        }
        finally
        {
            _tcpLock.Release();
        }
    }

    private async Task EnsureTcpConnectedAsync(CancellationToken cancellationToken)
    {
        if (_tcpClient?.Connected == true && _tcpReader is not null && _tcpWriter is not null)
            return;

        DisconnectTcp();
        _tcpClient = new TcpClient();
        await _tcpClient.ConnectAsync(_host, _tcpPort, cancellationToken).ConfigureAwait(false);
        var stream = _tcpClient.GetStream();
        _tcpReader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        _tcpWriter = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\r\n",
            AutoFlush = true
        };
        _logger.Info("Connected to vMix TCP API at {0}:{1}", _host, _tcpPort);
    }

    private VMixState ParseState(XDocument doc)
    {
        var inputs = doc.Root?
            .Element("inputs")?
            .Elements("input")
            .Select(input => new VMixInput
            {
                Number = IntAttr(input, "number"),
                Key = Attr(input, "key"),
                Title = Attr(input, "title"),
                Volume = DoubleAttr(input, "volume"),
                MeterF1 = DoubleAttr(input, "meterF1"),
                MeterF2 = DoubleAttr(input, "meterF2"),
                Muted = Attr(input, "muted").Equals("true", StringComparison.OrdinalIgnoreCase)
            })
            .OrderBy(input => input.Number)
            .ToList() ?? [];

        var outputVolumes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var audio = doc.Root?.Element("audio");
        if (audio is not null)
        {
            foreach (var attr in audio.Attributes())
            {
                if (attr.Name.LocalName.EndsWith("Volume", StringComparison.OrdinalIgnoreCase) &&
                    double.TryParse(attr.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    outputVolumes[attr.Name.LocalName] = value;
                }
            }
        }

        return new VMixState
        {
            Connected = true,
            Status = $"Connected. {inputs.Count} inputs.",
            Inputs = inputs,
            OutputVolumes = outputVolumes
        };
    }

    private static string UrlEncode(string value) => Uri.EscapeDataString(value);
    private static string Attr(XElement element, string name) => element.Attribute(name)?.Value ?? "";
    private static int IntAttr(XElement element, string name) => int.TryParse(Attr(element, name), out var value) ? value : 0;
    private static double DoubleAttr(XElement element, string name) => double.TryParse(Attr(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private void DisconnectTcp()
    {
        _tcpReader?.Dispose();
        _tcpWriter?.Dispose();
        _tcpClient?.Dispose();
        _tcpReader = null;
        _tcpWriter = null;
        _tcpClient = null;
    }

    public void Dispose()
    {
        DisconnectTcp();
        _httpClient.Dispose();
        _tcpLock.Dispose();
    }
}
