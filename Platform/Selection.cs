using System.Runtime.InteropServices;

namespace pxdSpeak.Platform;

/// <summary>
/// Gets the text selected in whatever program is in front: a copy (Ctrl+C) is sent to it, the copied text is read,
/// and the clipboard is put back the way it was, so the user's own clipboard is never lost.
/// </summary>
public static class Selection
{
    public enum Outcome { Text, NothingSelected, ClipboardBusy }

    /// <summary>Runs on a worker thread; takes up to about 2 s (it first waits for the hotkey's keys to be let go).</summary>
    public static (Outcome Result, string Text) Grab()
    {
        WaitForModifiersReleased(TimeSpan.FromSeconds(2));

        var before = GetClipboardSequenceNumber();
        var saved = Snapshot.Take();
        SendCopy();

        var deadline = Environment.TickCount64 + 700;
        while (GetClipboardSequenceNumber() == before && Environment.TickCount64 < deadline)
            Thread.Sleep(15);
        if (GetClipboardSequenceNumber() == before)
            return (Outcome.NothingSelected, "");

        // Some programs fill the clipboard in steps; give them a moment.
        Thread.Sleep(40);
        var text = ReadText();
        saved?.Restore();
        return text is null ? (Outcome.ClipboardBusy, "") : string.IsNullOrWhiteSpace(text) ? (Outcome.NothingSelected, "") : (Outcome.Text, text);
    }

    /// <summary>The text on the clipboard now, or null when it can't be opened.</summary>
    public static string? ReadText()
    {
        if (!Open())
            return null;
        try
        {
            var h = GetClipboardData(CfUnicodeText);
            if (h == IntPtr.Zero)
                return "";
            var p = GlobalLock(h);
            if (p == IntPtr.Zero)
                return "";
            try
            {
                return Marshal.PtrToStringUni(p) ?? "";
            }
            finally
            {
                GlobalUnlock(h);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool Open()
    {
        for (var i = 0; i < 20; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
                return true;
            Thread.Sleep(15);
        }

        return false;
    }

    private static void WaitForModifiersReleased(TimeSpan max)
    {
        var deadline = Environment.TickCount64 + (long)max.TotalMilliseconds;
        int[] keys = [0x10, 0x11, 0x12, 0x5B, 0x5C];   // Shift, Ctrl, Alt, left/right Win
        while (Environment.TickCount64 < deadline && keys.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0))
            Thread.Sleep(10);
        Thread.Sleep(30);
    }

    private static void SendCopy()
    {
        const uint up = 0x0002;
        var inputs = new[]
        {
            Key(0x11, 0), Key(0x43, 0), Key(0x43, up), Key(0x11, up),   // Ctrl down, C down, C up, Ctrl up
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static Input Key(ushort vk, uint flags) => new() { Type = 1, Kbd = new KeybdInput { Vk = vk, Flags = flags } };

    /// <summary>
    /// A copy of every clipboard format that is plain memory (text, HTML, RTF, images as DIB, file lists…). Formats that
    /// are GDI handles (CF_BITMAP and friends) are left out; Windows rebuilds CF_BITMAP from the DIB anyway.
    /// </summary>
    private sealed class Snapshot
    {
        private const long MaxBytes = 64L * 1024 * 1024;
        private static readonly HashSet<uint> Skip = [2, 3, 9, 14, 0x80, 0x82, 0x83, 0x8E];   // BITMAP, METAFILEPICT, PALETTE, ENHMETAFILE, OWNERDISPLAY, DSP*
        private readonly List<(uint Format, byte[] Data)> _items = new();

        public static Snapshot? Take()
        {
            if (!Open())
                return null;
            var snap = new Snapshot();
            try
            {
                long total = 0;
                uint format = 0;
                while ((format = EnumClipboardFormats(format)) != 0)
                {
                    if (Skip.Contains(format))
                        continue;
                    var h = GetClipboardData(format);
                    if (h == IntPtr.Zero)
                        continue;
                    var size = (long)GlobalSize(h);
                    if (size <= 0 || total + size > MaxBytes)
                        continue;
                    var p = GlobalLock(h);
                    if (p == IntPtr.Zero)
                        continue;
                    try
                    {
                        var data = new byte[size];
                        Marshal.Copy(p, data, 0, (int)size);
                        snap._items.Add((format, data));
                        total += size;
                    }
                    finally
                    {
                        GlobalUnlock(h);
                    }
                }
            }
            finally
            {
                CloseClipboard();
            }

            return snap;
        }

        public void Restore()
        {
            if (!Open())
                return;
            try
            {
                EmptyClipboard();
                foreach (var (format, data) in _items)
                    Put(format, data);
                // Keep the put-back copy out of Windows clipboard history (Win+V) and cloud clipboard.
                Put(RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"), [0]);
            }
            finally
            {
                CloseClipboard();
            }
        }

        private static void Put(uint format, byte[] data)
        {
            var h = GlobalAlloc(0x0002, (UIntPtr)data.Length);   // GMEM_MOVEABLE
            if (h == IntPtr.Zero)
                return;
            var p = GlobalLock(h);
            Marshal.Copy(data, 0, p, data.Length);
            GlobalUnlock(h);
            if (SetClipboardData(format, h) == IntPtr.Zero)
                GlobalFree(h);
        }
    }

    private const uint CfUnicodeText = 13;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeybdInput Kbd;
    }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr mem);
}
