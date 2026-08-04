using GameLauncher.Desktop.Services.Achievements.Configuration;

namespace GameLauncher.Desktop.Services.Achievements;

/// <summary>
/// The outcome of reading a value from a running process.
/// </summary>
/// <param name="Found">Whether the value was read.</param>
/// <param name="Value">The value as text, when read.</param>
/// <param name="Error">Why it was not read, when it was not.</param>
public sealed record MemoryReadResult(bool Found, string? Value, string? Error)
{
    /// <summary>Creates a successful result.</summary>
    /// <param name="value">The value read.</param>
    /// <returns>A result carrying the value.</returns>
    public static MemoryReadResult Success(string value) => new(true, value, null);

    /// <summary>Creates a failed result.</summary>
    /// <param name="error">Why the read failed.</param>
    /// <returns>A result carrying the reason.</returns>
    public static MemoryReadResult Failure(string error) => new(false, null, error);
}

/// <summary>
/// Reads values out of a running game's memory.
/// </summary>
/// <remarks>
/// <para>
/// <b>Strictly read-only.</b> The implementation opens processes with read and
/// query rights only, and there is no write, allocate, thread-creation or
/// injection capability anywhere in this interface or behind it. Nothing here can
/// modify a running game.
/// </para>
/// <para>
/// Offsets are relative to a named module's base address rather than absolute, so
/// a rule stays valid across runs despite address space layout randomisation.
/// </para>
/// </remarks>
public interface IProcessMemoryReader
{
    /// <summary>
    /// Reads a single value from a process.
    /// </summary>
    /// <param name="processId">The process to read from.</param>
    /// <param name="moduleName">Module the offset is relative to, such as <c>game.exe</c>.</param>
    /// <param name="offset">Offset from that module's base address.</param>
    /// <param name="valueType">How to interpret the bytes.</param>
    /// <returns>The value, or the reason it could not be read.</returns>
    /// <remarks>
    /// Never throws for an exited process, a missing module, a protected process
    /// or an unmapped address. All of those are ordinary while a game is starting,
    /// closing, or guarded by anti-cheat, and are reported rather than raised.
    /// </remarks>
    MemoryReadResult ReadValue(int processId, string moduleName, long offset, MemoryValueType valueType);
}
