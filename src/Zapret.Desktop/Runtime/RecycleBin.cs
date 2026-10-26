using System.Runtime.InteropServices;

namespace Zapret.Desktop.Runtime;

internal static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential)]
    private struct QueryInfo { public int Size; public long Bytes; public long Items; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBin(string? root, ref QueryInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBin(IntPtr window, string? root, uint flags);
    private const uint NoConfirmation = 1, NoProgress = 2, NoSound = 4;
    private const int Unexpected = unchecked((int)0x8000FFFF); // Returned when the bin is already empty.

    internal static (long Bytes, long Items) Query()
    {
        var info = new QueryInfo { Size = Marshal.SizeOf<QueryInfo>() };
        int result = SHQueryRecycleBin(null, ref info);
        return result == 0 ? (info.Bytes, info.Items) : (0, 0);
    }

    internal static void Empty()
    {
        int result = SHEmptyRecycleBin(IntPtr.Zero, null, NoConfirmation | NoProgress | NoSound);
        if (result != 0 && result != Unexpected) Marshal.ThrowExceptionForHR(result);
    }
}
