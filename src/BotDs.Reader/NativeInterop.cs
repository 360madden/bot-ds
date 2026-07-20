using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BotDs.Reader;

/// <summary>
/// Safe handle for a Windows process opened with minimal rights.
/// Passed directly to LibraryImport P/Invokes for lifetime pinning.
/// </summary>
internal sealed class SafeProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeProcessHandle()
        : base(ownsHandle: true) { }

    internal SafeProcessHandle(IntPtr existingHandle, bool ownsHandle)
        : base(ownsHandle) => SetHandle(existingHandle);

    protected override bool ReleaseHandle()
    {
        return NativeMethods.CloseHandle(handle);
    }
}

/// <summary>
/// Describes a readable committed memory region in the target process.
/// </summary>
public readonly record struct MemoryRegion(nint BaseAddress, long RegionSize);

/// <summary>
/// Result of querying an address range.
/// </summary>
internal readonly record struct MemoryQueryResult(
    nint BaseAddress,
    nint AllocationBase,
    long RegionSize,
    uint State,
    uint Protect,
    uint Type);

internal static partial class NativeMethods
{
    private const string Kernel32 = "kernel32.dll";

    // ── Process ─────────────────────────────────────────────────

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWow64Process2(
        SafeProcessHandle hProcess,
        out ushort pProcessMachine,
        out ushort pNativeMachine);

    [LibraryImport(Kernel32, SetLastError = true)]
    internal static partial void GetNativeSystemInfo(out SYSTEM_INFO lpSystemInfo);

    [LibraryImport(Kernel32, SetLastError = true)]
    internal static partial IntPtr OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        int dwProcessId);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    // ── Memory ──────────────────────────────────────────────────

    [LibraryImport(Kernel32, SetLastError = true)]
    internal static partial nuint VirtualQueryEx(
        SafeProcessHandle hProcess,
        nint lpAddress,
        out MEMORY_BASIC_INFORMATION64 lpBuffer,
        nuint dwLength);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReadProcessMemory(
        SafeProcessHandle hProcess,
        nint lpBaseAddress,
        byte[] lpBuffer,
        nuint nSize,
        out nuint lpNumberOfBytesRead);

    // ── Synchronization ─────────────────────────────────────────

    [LibraryImport(Kernel32, SetLastError = false)]
    internal static partial uint WaitForSingleObject(
        SafeProcessHandle hHandle,
        uint dwMilliseconds);

    internal const uint WAIT_OBJECT_0 = 0;
    internal const uint WAIT_TIMEOUT = 258;
    internal const uint WAIT_FAILED = 0xFFFFFFFF;

    // ── Process rights ──────────────────────────────────────────

    internal const uint PROCESS_QUERY_INFORMATION = 0x0400;
    internal const uint PROCESS_VM_READ = 0x0010;
    internal const uint SYNCHRONIZE = 0x00100000;

    internal static readonly uint MinimalProcessRights =
        PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | SYNCHRONIZE;

    // ── Machine types ───────────────────────────────────────────

    internal const ushort IMAGE_FILE_MACHINE_AMD64 = 0x8664;

    // ── Memory states and types ─────────────────────────────────

    internal const uint MEM_COMMIT = 0x1000;

    // ── Page protections ────────────────────────────────────────

    internal const uint PAGE_NOACCESS = 0x01;
    internal const uint PAGE_READONLY = 0x02;
    internal const uint PAGE_READWRITE = 0x04;
    internal const uint PAGE_WRITECOPY = 0x08;
    internal const uint PAGE_EXECUTE = 0x10;
    internal const uint PAGE_EXECUTE_READ = 0x20;
    internal const uint PAGE_EXECUTE_READWRITE = 0x40;
    internal const uint PAGE_EXECUTE_WRITECOPY = 0x80;
    internal const uint PAGE_GUARD = 0x100;

    // ── Process identity ────────────────────────────────────────

    [DllImport(Kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageNameW(
        SafeProcessHandle hProcess,
        uint dwFlags,
        IntPtr lpExeName,
        ref uint lpdwSize);

    // ── Structures ──────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_INFO
    {
        public ushort wProcessorArchitecture;
        public ushort wReserved;
        public uint dwPageSize;
        public nint lpMinimumApplicationAddress;
        public nint lpMaximumApplicationAddress;
        public nuint dwActiveProcessorMask;
        public uint dwNumberOfProcessors;
        public uint dwProcessorType;
        public uint dwAllocationGranularity;
        public ushort wProcessorLevel;
        public ushort wProcessorRevision;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORY_BASIC_INFORMATION64
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint __alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint __alignment2;
    }

    internal const ushort PROCESSOR_ARCHITECTURE_AMD64 = 9;

    /// <summary>
    /// Returns true if the protection allows reading (excludes NOACCESS, GUARD, EXECUTE-only).
    /// </summary>
    internal static bool IsProtectionReadable(uint protect)
    {
        if ((protect & PAGE_GUARD) != 0) return false;
        uint baseProtection = protect & 0xFF;
        return baseProtection is PAGE_READONLY
            or PAGE_READWRITE
            or PAGE_WRITECOPY
            or PAGE_EXECUTE_READ
            or PAGE_EXECUTE_READWRITE
            or PAGE_EXECUTE_WRITECOPY;
    }

    /// <summary>
    /// Returns true if the state is committed. Does NOT require MEM_PRIVATE.
    /// </summary>
    internal static bool IsStateReadableCommitted(uint state, uint type)
        => state == MEM_COMMIT;

    /// <summary>
    /// Map Win32 error to ReaderFailureCode.
    /// </summary>
    internal static ReaderFailureCode MapWin32Error(int errorCode) => errorCode switch
    {
        5 /* ERROR_ACCESS_DENIED */ => ReaderFailureCode.AccessDenied,
        87 /* ERROR_INVALID_PARAMETER */ => ReaderFailureCode.ProcessNotFound,
        _ => ReaderFailureCode.OpenFailure,
    };
}
