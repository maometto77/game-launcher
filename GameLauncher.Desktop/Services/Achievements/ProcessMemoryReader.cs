using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using GameLauncher.Desktop.Services.Achievements.Configuration;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Desktop.Services.Achievements;

/// <summary>
/// Default <see cref="IProcessMemoryReader"/>, built on the Win32 process APIs.
/// </summary>
/// <remarks>
/// <para>
/// Read-only by construction. The process handle is requested with
/// <c>PROCESS_QUERY_INFORMATION | PROCESS_VM_READ</c> and nothing else — not
/// <c>VM_WRITE</c>, not <c>VM_OPERATION</c>, not <c>CREATE_THREAD</c>. Even if
/// this code were changed to attempt a write, the handle it holds would not
/// permit one.
/// </para>
/// <para>
/// The only imported functions are <c>OpenProcess</c>, <c>ReadProcessMemory</c>
/// and <c>CloseHandle</c>. There is no <c>WriteProcessMemory</c>,
/// no <c>VirtualAllocEx</c> and no <c>CreateRemoteThread</c> anywhere in the
/// project.
/// </para>
/// </remarks>
public sealed class ProcessMemoryReader : IProcessMemoryReader
{
    /// <summary>Right to query basic process information.</summary>
    private const int ProcessQueryInformation = 0x0400;

    /// <summary>Right to read process memory. Deliberately paired with no write right.</summary>
    private const int ProcessVmRead = 0x0010;

    private readonly ILogger<ProcessMemoryReader> _logger;

    /// <summary>
    /// Initialises a new instance.
    /// </summary>
    /// <param name="logger">Logger for read diagnostics.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is <see langword="null"/>.</exception>
    public ProcessMemoryReader(ILogger<ProcessMemoryReader> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public MemoryReadResult ReadValue(
        int processId,
        string moduleName,
        long offset,
        MemoryValueType valueType)
    {
        if (processId <= 0)
        {
            return MemoryReadResult.Failure("The game is not running.");
        }

        if (string.IsNullOrWhiteSpace(moduleName))
        {
            return MemoryReadResult.Failure("No module name is configured.");
        }

        var moduleBase = TryGetModuleBase(processId, moduleName, out var moduleError);
        if (moduleBase == IntPtr.Zero)
        {
            return MemoryReadResult.Failure(moduleError!);
        }

        var handle = NativeMethods.OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);

        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();

            // Access denied is routine: an elevated game, or one behind anti-cheat,
            // simply cannot be inspected. That is a fact to report, not a fault.
            return MemoryReadResult.Failure(error == 5
                ? "Access to the game's memory was denied. It may be running elevated or protected."
                : $"The game process could not be opened (error {error}).");
        }

        try
        {
            var size = valueType switch
            {
                MemoryValueType.Int32 => sizeof(int),
                MemoryValueType.Float => sizeof(float),
                MemoryValueType.Byte => sizeof(byte),
                _ => sizeof(int)
            };

            var buffer = new byte[size];
            var address = IntPtr.Add(moduleBase, checked((int)offset));

            if (!NativeMethods.ReadProcessMemory(handle, address, buffer, size, out var read) || read != size)
            {
                // Usually an offset pointing at unmapped memory — a stale rule, or
                // one written against a different build of the game.
                return MemoryReadResult.Failure(
                    $"Could not read {size} bytes at {moduleName}+0x{offset:X}. The offset may be wrong.");
            }

            return MemoryReadResult.Success(Interpret(buffer, valueType));
        }
        catch (OverflowException)
        {
            return MemoryReadResult.Failure($"The offset 0x{offset:X} is too large for this process.");
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>Converts raw bytes into text according to the configured type.</summary>
    /// <param name="buffer">Bytes read from the process.</param>
    /// <param name="valueType">How to interpret them.</param>
    /// <returns>The value as invariant-culture text.</returns>
    private static string Interpret(byte[] buffer, MemoryValueType valueType) => valueType switch
    {
        MemoryValueType.Float =>
            BitConverter.ToSingle(buffer).ToString("R", CultureInfo.InvariantCulture),

        MemoryValueType.Byte =>
            buffer[0].ToString(CultureInfo.InvariantCulture),

        _ => BitConverter.ToInt32(buffer).ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>
    /// Finds a module's base address within a process.
    /// </summary>
    /// <param name="processId">The process to inspect.</param>
    /// <param name="moduleName">Module to find, matched on file name.</param>
    /// <param name="error">Receives the reason when the module is not found.</param>
    /// <returns>The module's base address, or <see cref="IntPtr.Zero"/>.</returns>
    /// <remarks>
    /// Uses the managed process API rather than <c>EnumProcessModules</c>, which
    /// keeps the P/Invoke surface to the three functions actually needed for
    /// reading. Its failure modes are handled explicitly: enumerating modules of a
    /// 32-bit process from a 64-bit one throws rather than returning empty.
    /// </remarks>
    private IntPtr TryGetModuleBase(int processId, string moduleName, out string? error)
    {
        error = null;

        try
        {
            using var process = Process.GetProcessById(processId);

            foreach (ProcessModule module in process.Modules)
            {
                if (string.Equals(module.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    return module.BaseAddress;
                }
            }

            error = $"The module '{moduleName}' is not loaded in the game process.";
            return IntPtr.Zero;
        }
        catch (ArgumentException)
        {
            error = "The game is no longer running.";
            return IntPtr.Zero;
        }
        catch (Win32Exception ex)
        {
            // Typically a bitness mismatch, or a process this one may not inspect.
            _logger.LogDebug(ex, "Could not enumerate modules of process {ProcessId}.", processId);
            error = "The game's modules could not be listed. It may be 32-bit, elevated, or protected.";
            return IntPtr.Zero;
        }
        catch (InvalidOperationException)
        {
            error = "The game exited while it was being inspected.";
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// The read-only subset of the Win32 process API this reader uses.
    /// </summary>
    /// <remarks>
    /// Deliberately minimal. No memory-writing, allocation or thread-creation
    /// function is imported anywhere in this project.
    /// </remarks>
    private static class NativeMethods
    {
        /// <summary>Opens an existing process object.</summary>
        /// <param name="desiredAccess">Access rights. Only query and read are ever passed.</param>
        /// <param name="inheritHandle">Whether child processes inherit the handle.</param>
        /// <param name="processId">The process to open.</param>
        /// <returns>A handle, or zero on failure.</returns>
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(int desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

        /// <summary>Copies memory out of another process.</summary>
        /// <param name="process">Handle opened with read access.</param>
        /// <param name="baseAddress">Address to read from.</param>
        /// <param name="buffer">Receives the bytes.</param>
        /// <param name="size">How many bytes to read.</param>
        /// <param name="bytesRead">Receives how many were actually read.</param>
        /// <returns><see langword="true"/> on success.</returns>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadProcessMemory(
            IntPtr process, IntPtr baseAddress, byte[] buffer, int size, out IntPtr bytesRead);

        /// <summary>Closes an open handle.</summary>
        /// <param name="handle">The handle to close.</param>
        /// <returns><see langword="true"/> on success.</returns>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
