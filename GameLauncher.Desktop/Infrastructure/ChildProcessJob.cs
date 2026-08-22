using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GameLauncher.Desktop.Infrastructure;

/// <summary>
/// Ties child processes to the lifetime of this one, enforced by the operating
/// system.
/// </summary>
/// <remarks>
/// <para>
/// A launcher that starts long-running helpers — <c>aria2c</c> in particular —
/// has to guarantee they do not outlive it. Killing them in <see cref="IDisposable.Dispose"/>
/// covers the polite case and only the polite case: it never runs when the
/// process is killed from Task Manager, stopped from an IDE, terminated to free
/// a locked file during a rebuild, or lost to a crash. Every one of those leaves
/// a helper running with nothing watching it, still writing to files the next
/// launch will try to use.
/// </para>
/// <para>
/// A job object closes that gap because the kernel enforces it. Processes
/// assigned to a job with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> are killed
/// when the last handle to the job closes, and that happens when this process
/// ends however it ends — including a kill it never got to react to.
/// </para>
/// <para>
/// Best effort by design. If a job cannot be created or assigned this reports
/// failure and the caller carries on: an unmanaged child is worse than a managed
/// one but far better than refusing to download at all, and the ordinary
/// disposal path still covers a graceful exit.
/// </para>
/// </remarks>
public sealed class ChildProcessJob : IDisposable
{
    private readonly SafeJobHandle? _handle;
    private bool _disposed;

    /// <summary>
    /// Creates the job, or nothing usable when the platform will not provide one.
    /// </summary>
    public ChildProcessJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = CreateJobObject(IntPtr.Zero, null);

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return;
        }

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);

            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                handle.Dispose();
                return;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        _handle = handle;
    }

    /// <summary>Gets a value indicating whether the operating system is enforcing this.</summary>
    /// <remarks>
    /// False on a platform without job objects, or when the job could not be
    /// created. Callers use it to say so in the log rather than to change what
    /// they do.
    /// </remarks>
    public bool IsEnforced => _handle is { IsInvalid: false };

    /// <summary>
    /// Puts a process under this job, so it cannot outlive this one.
    /// </summary>
    /// <param name="process">The freshly started child.</param>
    /// <returns><see langword="true"/> when the kernel accepted it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="process"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Assignment can fail legitimately: a process that has already exited cannot
    /// be assigned, and on Windows 7 a process already inside another job cannot
    /// be nested. Neither is worth failing a download over.
    /// </remarks>
    public bool Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (_disposed || _handle is null || _handle.IsInvalid)
        {
            return false;
        }

        try
        {
            return AssignProcessToJobObject(_handle, process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // The process exited between starting and being assigned. Nothing to
            // contain, and nothing to report.
            return false;
        }
    }

    /// <summary>
    /// Closes the job, which kills everything still in it.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle?.Dispose();
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    // DllImport rather than the LibraryImport source generator: that one requires
    // AllowUnsafeBlocks for the whole project, which is a large permission to
    // grant for three calls into kernel32.
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        int infoClass,
        IntPtr info,
        uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>A job handle whose closure is what kills the children.</summary>
    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
