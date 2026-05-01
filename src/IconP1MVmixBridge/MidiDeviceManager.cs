using System.Runtime.InteropServices;
using System.Text;

namespace IconP1MVmixBridge;

public sealed class MidiDeviceManager : IDisposable
{
    private readonly FileLogger _logger;
    private readonly MidiInterop.MidiInProc _midiInProc;
    private IntPtr _midiIn = IntPtr.Zero;
    private IntPtr _midiOut = IntPtr.Zero;

    public event EventHandler<MidiMessageEventArgs>? MessageReceived;
    public bool InputOpen => _midiIn != IntPtr.Zero;
    public bool OutputOpen => _midiOut != IntPtr.Zero;
    public string OpenInputName { get; private set; } = "";
    public string OpenOutputName { get; private set; } = "";

    public MidiDeviceManager(FileLogger logger)
    {
        _logger = logger;
        _midiInProc = OnMidiInMessage;
    }

    public static IReadOnlyList<MidiDeviceInfo> GetInputs()
    {
        var devices = new List<MidiDeviceInfo>();
        var size = Marshal.SizeOf<MidiInterop.MIDIINCAPS>();
        for (var i = 0; i < MidiInterop.midiInGetNumDevs(); i++)
        {
            if (MidiInterop.midiInGetDevCaps(new IntPtr(i), out var caps, size) == MidiInterop.MMSYSERR_NOERROR)
                devices.Add(new MidiDeviceInfo(i, caps.szPname));
        }
        return devices;
    }

    public static IReadOnlyList<MidiDeviceInfo> GetOutputs()
    {
        var devices = new List<MidiDeviceInfo>();
        var size = Marshal.SizeOf<MidiInterop.MIDIOUTCAPS>();
        for (var i = 0; i < MidiInterop.midiOutGetNumDevs(); i++)
        {
            if (MidiInterop.midiOutGetDevCaps(new IntPtr(i), out var caps, size) == MidiInterop.MMSYSERR_NOERROR)
                devices.Add(new MidiDeviceInfo(i, caps.szPname));
        }
        return devices;
    }

    public void Open(string inputName, string outputName)
    {
        Close();
        var input = FindDevice(GetInputs(), inputName);
        var output = FindDevice(GetOutputs(), outputName);

        if (input is not null)
        {
            var result = MidiInterop.midiInOpen(out _midiIn, input.Id, _midiInProc, IntPtr.Zero, MidiInterop.CALLBACK_FUNCTION);
            if (result != MidiInterop.MMSYSERR_NOERROR)
                throw new InvalidOperationException(MidiInterop.MidiInError(result));

            result = MidiInterop.midiInStart(_midiIn);
            if (result != MidiInterop.MMSYSERR_NOERROR)
                throw new InvalidOperationException(MidiInterop.MidiInError(result));

            _logger.Info("Opened MIDI input {0}", input.Name);
            OpenInputName = input.Name;
        }

        if (output is not null)
        {
            var result = MidiInterop.midiOutOpen(out _midiOut, output.Id, IntPtr.Zero, IntPtr.Zero, 0);
            if (result != MidiInterop.MMSYSERR_NOERROR)
                throw new InvalidOperationException(MidiInterop.MidiOutError(result));

            _logger.Info("Opened MIDI output {0}", output.Name);
            OpenOutputName = output.Name;
        }

        if (input is null && output is null)
            throw new InvalidOperationException("No matching MIDI input or output devices were selected.");

        if (input is null)
            _logger.Warn("No matching MIDI input device found for '{0}'", inputName);
        if (output is null)
            _logger.Warn("No matching MIDI output device found for '{0}'", outputName);
    }

    public void SendShort(int status, int data1, int data2)
    {
        if (_midiOut == IntPtr.Zero)
            return;

        status &= 0xFF;
        data1 &= 0x7F;
        data2 &= 0x7F;
        var message = status | (data1 << 8) | (data2 << 16);
        var result = MidiInterop.midiOutShortMsg(_midiOut, message);
        if (result != MidiInterop.MMSYSERR_NOERROR)
            _logger.Warn("MIDI output send failed: {0}", MidiInterop.MidiOutError(result));
    }

    public void SendPitchBend(int zeroBasedChannel, int fourteenBitValue)
    {
        fourteenBitValue = Math.Clamp(fourteenBitValue, 0, 16383);
        SendShort(0xE0 | (zeroBasedChannel & 0x0F), fourteenBitValue & 0x7F, (fourteenBitValue >> 7) & 0x7F);
    }

    public void SendMackieMeter(int zeroBasedChannel, int level)
    {
        level = Math.Clamp(level, 0, 12);
        SendShort(0xD0, ((zeroBasedChannel & 0x0F) << 4) | level, 0);
    }

    public void SendMackieScribbleText(int zeroBasedChannel, string text)
    {
        if (_midiOut == IntPtr.Zero)
            return;

        zeroBasedChannel = Math.Clamp(zeroBasedChannel, 0, 7);
        var offset = zeroBasedChannel * 7;
        var label = ToAsciiFixed(text, 7);
        var payload = new byte[7 + label.Length];
        payload[0] = 0xF0;
        payload[1] = 0x00;
        payload[2] = 0x00;
        payload[3] = 0x66;
        payload[4] = 0x14;
        payload[5] = 0x12;
        payload[6] = (byte)offset;
        Array.Copy(label, 0, payload, 7, label.Length);
        Array.Resize(ref payload, payload.Length + 1);
        payload[^1] = 0xF7;
        SendLong(payload);
        _logger.Debug("Display ch {0}: {1}", zeroBasedChannel + 1, text);
    }

    private void SendLong(byte[] data)
    {
        var dataPtr = IntPtr.Zero;
        try
        {
            dataPtr = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, dataPtr, data.Length);
            var header = new MidiInterop.MIDIHDR
            {
                lpData = dataPtr,
                dwBufferLength = data.Length,
                dwBytesRecorded = data.Length
            };
            var headerSize = Marshal.SizeOf<MidiInterop.MIDIHDR>();
            var result = MidiInterop.midiOutPrepareHeader(_midiOut, ref header, headerSize);
            if (result != MidiInterop.MMSYSERR_NOERROR)
            {
                _logger.Warn("MIDI long prepare failed: {0}", MidiInterop.MidiOutError(result));
                return;
            }

            result = MidiInterop.midiOutLongMsg(_midiOut, ref header, headerSize);
            if (result != MidiInterop.MMSYSERR_NOERROR)
                _logger.Warn("MIDI long send failed: {0}", MidiInterop.MidiOutError(result));

            Thread.Sleep(5);
            MidiInterop.midiOutUnprepareHeader(_midiOut, ref header, headerSize);
        }
        finally
        {
            if (dataPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(dataPtr);
        }
    }

    private static byte[] ToAsciiFixed(string text, int length)
    {
        text = (text ?? "").Trim();
        if (text.Length > length)
            text = text[..length];
        text = text.PadRight(length);
        return Encoding.ASCII.GetBytes(text.Select(ch => ch is >= ' ' and <= '~' ? ch : '?').ToArray());
    }

    public void Close()
    {
        if (_midiIn != IntPtr.Zero)
        {
            MidiInterop.midiInStop(_midiIn);
            MidiInterop.midiInClose(_midiIn);
            _midiIn = IntPtr.Zero;
            OpenInputName = "";
            _logger.Info("Closed MIDI input");
        }

        if (_midiOut != IntPtr.Zero)
        {
            MidiInterop.midiOutClose(_midiOut);
            _midiOut = IntPtr.Zero;
            OpenOutputName = "";
            _logger.Info("Closed MIDI output");
        }
    }

    private static MidiDeviceInfo? FindDevice(IReadOnlyList<MidiDeviceInfo> devices, string name)
    {
        if (devices.Count == 0)
            return null;
        if (string.IsNullOrWhiteSpace(name))
            return devices.FirstOrDefault(device => device.Name.Contains("P1", StringComparison.OrdinalIgnoreCase)) ?? devices.First();

        return devices.FirstOrDefault(device => device.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(device => device.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private void OnMidiInMessage(IntPtr hMidiIn, int message, IntPtr instance, int param1, int param2)
    {
        if (message != MidiInterop.MIM_DATA)
            return;

        try
        {
            MessageReceived?.Invoke(this, new MidiMessageEventArgs(param1, param2));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "MIDI message handler failed");
        }
    }

    public void Dispose() => Close();
}
