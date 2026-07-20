using System.Runtime.InteropServices;

namespace BotDs.Reader;

internal sealed class WindowsMemoryReader : IMemoryReader
{
    private readonly SafeProcessHandle _handle;
    private readonly int _processId;
    private volatile bool _disposed;

    private WindowsMemoryReader(SafeProcessHandle handle, int processId)
    {
        _handle = handle; _processId = processId;
    }

    public int ProcessId => _processId;
    public bool IsAlive => !_disposed && CheckLiveness();

    public static WindowsMemoryReader Attach(int processId, string? expectedName)
    {
        NativeMethods.GetNativeSystemInfo(out var sysInfo);
        if (sysInfo.wProcessorArchitecture != NativeMethods.PROCESSOR_ARCHITECTURE_AMD64)
            throw new ReaderException(ReaderFailureCode.UnsupportedArchitecture, "Not AMD64");
        if (nint.Size != 8)
            throw new ReaderException(ReaderFailureCode.UnsupportedArchitecture, "Not 64-bit");

        IntPtr raw = NativeMethods.OpenProcess(NativeMethods.MinimalProcessRights, false, processId);
        if (raw == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            throw new ReaderException(NativeMethods.MapWin32Error(err), $"OpenProcess failed (win32={err})");
        }

        var handle = new SafeProcessHandle(raw, true);
        try
        {
            if (!NativeMethods.IsWow64Process2(handle, out ushort pm, out ushort nm))
                throw new ReaderException(ReaderFailureCode.OpenFailure, "IsWow64Process2 failed");
            if (pm != NativeMethods.IMAGE_FILE_MACHINE_AMD64 && pm != 0)
                throw new ReaderException(ReaderFailureCode.UnsupportedArchitecture, "Target not AMD64");
            if (nm != NativeMethods.IMAGE_FILE_MACHINE_AMD64)
                throw new ReaderException(ReaderFailureCode.UnsupportedArchitecture, "Native not AMD64");

            // Verify process name if expected
            if (expectedName is not null)
                VerifyProcessName(handle, processId, expectedName);
        }
        catch { handle.Dispose(); throw; }

        return new WindowsMemoryReader(handle, processId);
    }

    private static void VerifyProcessName(SafeProcessHandle handle, int pid, string expectedName)
    {
        uint size = 260;
        IntPtr buf = Marshal.AllocHGlobal((int)(size * 2));
        try
        {
            if (!NativeMethods.QueryFullProcessImageNameW(handle, 0, buf, ref size))
            {
                int err = Marshal.GetLastWin32Error();
                throw new ReaderException(ReaderFailureCode.OpenFailure, $"QueryFullProcessImageName failed (win32={err})");
            }
            string path = Marshal.PtrToStringUni(buf, (int)size) ?? "";
            string img = Path.GetFileName(path);
            if (!new ProcessSelector { ProcessName = expectedName }.NameMatches(img))
                throw new ReaderException(ReaderFailureCode.ProcessNameMismatch, "Process image name mismatch");
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void ReadExact(nint address, byte[] buffer, int size)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowsMemoryReader));
        if (size < 0 || size > buffer.Length)
            throw new ReaderException(ReaderFailureCode.ReadFailure, "Buffer too small");
        NativeMethods.GetNativeSystemInfo(out var si);
        if ((nuint)address < (nuint)si.lpMinimumApplicationAddress || (nuint)address > (nuint)si.lpMaximumApplicationAddress)
            throw new ReaderException(ReaderFailureCode.ReadFailure, "Address out of range");

        bool ok = NativeMethods.ReadProcessMemory(_handle, address, buffer, (nuint)size, out nuint br);
        if (!ok || br != (nuint)size)
            throw new ReaderException(ReaderFailureCode.ReadFailure, $"RPM failed (win32={Marshal.GetLastWin32Error()}, read={br}, req={size})");
    }

    public RegionEnumerationResult QueryReadableRegions(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowsMemoryReader));
        var regions = new List<MemoryRegion>();
        NativeMethods.GetNativeSystemInfo(out var si);
        nint curr = si.lpMinimumApplicationAddress;
        nint max = si.lpMaximumApplicationAddress;
        RegionEnumerationFailure failCause = RegionEnumerationFailure.None;
        int failErr = 0;

        while ((nuint)curr < (nuint)max)
        {
            ct.ThrowIfCancellationRequested();
            nuint r = NativeMethods.VirtualQueryEx(_handle, curr, out var mbi,
                (nuint)Marshal.SizeOf<NativeMethods.MEMORY_BASIC_INFORMATION64>());
            if (r == 0)
            {
                failErr = Marshal.GetLastWin32Error();
                failCause = RegionEnumerationFailure.VirtualQueryError;
                break;
            }

            long curL = (long)curr, maxL = (long)max;
            if (!WindowsMemoryReader.TryCalculateRegionBounds(mbi.BaseAddress, mbi.RegionSize,
                curL, maxL, out long baseL, out long nextL, out long clampedEnd))
            {
                failCause = RegionEnumerationFailure.OverflowOrBackward;
                break;
            }

            if (NativeMethods.IsStateReadableCommitted(mbi.State, mbi.Type) &&
                NativeMethods.IsProtectionReadable(mbi.Protect))
                regions.Add(new MemoryRegion((nint)baseL, clampedEnd - baseL));

            curr = (nint)nextL;
        }

        bool complete = failCause == RegionEnumerationFailure.None && (nuint)curr >= (nuint)max;
        return new RegionEnumerationResult(regions.AsReadOnly(), complete, failCause, failErr);
    }

    internal static bool TryCalculateRegionBounds(ulong rawBase, ulong rawSize,
        long currAddr, long maxAddr, out long baseAddr, out long nextAddr, out long clampedEnd)
    {
        baseAddr = nextAddr = clampedEnd = 0;
        if (rawBase > long.MaxValue || rawSize is 0 or > long.MaxValue) return false;
        baseAddr = (long)rawBase;
        if (baseAddr < currAddr) return false;
        try { nextAddr = checked(baseAddr + (long)rawSize); }
        catch (OverflowException) { return false; }
        if (nextAddr <= currAddr) return false;
        clampedEnd = Math.Min(nextAddr, maxAddr);
        return clampedEnd > baseAddr;
    }

    public bool CheckLiveness()
    {
        if (_disposed || _handle.IsInvalid || _handle.IsClosed) return false;
        return NativeMethods.WaitForSingleObject(_handle, 0) == NativeMethods.WAIT_TIMEOUT;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }
}

internal sealed class WindowsMemoryReaderFactory : IMemoryReaderFactory
{
    public IMemoryReader Open(int processId, string? expectedName = null)
        => WindowsMemoryReader.Attach(processId, expectedName);
}
