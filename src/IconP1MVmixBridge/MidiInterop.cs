using System.Runtime.InteropServices;
using System.Text;

namespace IconP1MVmixBridge;

internal static class MidiInterop
{
    public const int MMSYSERR_NOERROR = 0;
    public const int CALLBACK_FUNCTION = 0x00030000;
    public const int MIM_OPEN = 0x3C1;
    public const int MIM_CLOSE = 0x3C2;
    public const int MIM_DATA = 0x3C3;

    public delegate void MidiInProc(IntPtr hMidiIn, int wMsg, IntPtr dwInstance, int dwParam1, int dwParam2);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MIDIINCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwSupport;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MIDIOUTCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public ushort wTechnology;
        public ushort wVoices;
        public ushort wNotes;
        public ushort wChannelMask;
        public uint dwSupport;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIDIHDR
    {
        public IntPtr lpData;
        public int dwBufferLength;
        public int dwBytesRecorded;
        public IntPtr dwUser;
        public int dwFlags;
        public IntPtr lpNext;
        public IntPtr reserved;
        public int dwOffset;
        public IntPtr dwReserved1;
        public IntPtr dwReserved2;
        public IntPtr dwReserved3;
        public IntPtr dwReserved4;
        public IntPtr dwReserved5;
        public IntPtr dwReserved6;
        public IntPtr dwReserved7;
        public IntPtr dwReserved8;
    }

    [DllImport("winmm.dll")]
    public static extern int midiInGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    public static extern int midiInGetDevCaps(IntPtr uDeviceID, out MIDIINCAPS lpMidiInCaps, int cbMidiInCaps);

    [DllImport("winmm.dll")]
    public static extern int midiInOpen(out IntPtr lphMidiIn, int uDeviceID, MidiInProc dwCallback, IntPtr dwInstance, int dwFlags);

    [DllImport("winmm.dll")]
    public static extern int midiInStart(IntPtr hMidiIn);

    [DllImport("winmm.dll")]
    public static extern int midiInStop(IntPtr hMidiIn);

    [DllImport("winmm.dll")]
    public static extern int midiInClose(IntPtr hMidiIn);

    [DllImport("winmm.dll")]
    public static extern int midiOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    public static extern int midiOutGetDevCaps(IntPtr uDeviceID, out MIDIOUTCAPS lpMidiOutCaps, int cbMidiOutCaps);

    [DllImport("winmm.dll")]
    public static extern int midiOutOpen(out IntPtr lphMidiOut, int uDeviceID, IntPtr dwCallback, IntPtr dwInstance, int dwFlags);

    [DllImport("winmm.dll")]
    public static extern int midiOutShortMsg(IntPtr hMidiOut, int dwMsg);

    [DllImport("winmm.dll")]
    public static extern int midiOutPrepareHeader(IntPtr hMidiOut, ref MIDIHDR lpMidiOutHdr, int cbMidiOutHdr);

    [DllImport("winmm.dll")]
    public static extern int midiOutLongMsg(IntPtr hMidiOut, ref MIDIHDR lpMidiOutHdr, int cbMidiOutHdr);

    [DllImport("winmm.dll")]
    public static extern int midiOutUnprepareHeader(IntPtr hMidiOut, ref MIDIHDR lpMidiOutHdr, int cbMidiOutHdr);

    [DllImport("winmm.dll")]
    public static extern int midiOutClose(IntPtr hMidiOut);

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    public static extern int midiOutGetErrorText(int mmrError, StringBuilder pszText, int cchText);

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    public static extern int midiInGetErrorText(int mmrError, StringBuilder pszText, int cchText);

    public static string MidiInError(int code)
    {
        if (code == MMSYSERR_NOERROR)
            return "OK";
        var builder = new StringBuilder(256);
        return midiInGetErrorText(code, builder, builder.Capacity) == MMSYSERR_NOERROR ? builder.ToString() : $"MIDI input error {code}";
    }

    public static string MidiOutError(int code)
    {
        if (code == MMSYSERR_NOERROR)
            return "OK";
        var builder = new StringBuilder(256);
        return midiOutGetErrorText(code, builder, builder.Capacity) == MMSYSERR_NOERROR ? builder.ToString() : $"MIDI output error {code}";
    }
}
