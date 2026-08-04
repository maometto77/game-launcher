using System.Security.Cryptography;
using System.Text;
using GameLauncher.Shared.Contracts;

namespace GameLauncher.Relay.Security;

/// <summary>
/// Mints and hashes the credentials the relay issues.
/// </summary>
public interface ITokenService
{
    /// <summary>
    /// Generates a new device token.
    /// </summary>
    /// <returns>A token that is shown to the client exactly once.</returns>
    string NewToken();

    /// <summary>
    /// Hashes a token for storage and lookup.
    /// </summary>
    /// <param name="token">The token to hash.</param>
    /// <returns>Lowercase hexadecimal SHA-256 of the token.</returns>
    string Hash(string token);

    /// <summary>
    /// Generates a friend code in the canonical <c>GL-XXXXX-XXXXX</c> form.
    /// </summary>
    /// <returns>A new friend code.</returns>
    string NewFriendCode();

    /// <summary>
    /// Generates an opaque device identifier.
    /// </summary>
    /// <returns>32 lowercase hexadecimal characters.</returns>
    string NewDeviceId();
}

/// <summary>
/// Default <see cref="ITokenService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Tokens are 32 cryptographically random bytes, base64url-encoded, prefixed
/// <c>glr_</c>. The prefix makes a leaked token recognisable in a log or a paste.
/// </para>
/// <para>
/// They are stored as an unsalted SHA-256 hash. Salting plus a slow key
/// derivation exists to make guessing expensive, and guessing is only a threat
/// against low-entropy secrets such as human-chosen passwords. A 256-bit random
/// token cannot be brute-forced however fast the hash is, so a slow KDF would
/// cost a CPU-bound operation on every single request and buy nothing.
/// </para>
/// <para>
/// Omitting the salt is also what makes the hash a usable lookup key: the relay
/// hashes the presented token and finds the device in one indexed read. With a
/// per-row salt it would have to hash against every device row in turn.
/// </para>
/// </remarks>
public sealed class TokenService : ITokenService
{
    /// <summary>Prefix identifying a GameLauncher relay token.</summary>
    public const string TokenPrefix = "glr_";

    private const int TokenBytes = 32;

    /// <inheritdoc />
    public string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);

        // Base64url so the token is safe in a query string, which is how SignalR
        // has to carry it: a WebSocket handshake cannot set request headers.
        return TokenPrefix + Base64UrlEncode(bytes);
    }

    /// <inheritdoc />
    public string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <inheritdoc />
    public string NewFriendCode()
    {
        var alphabet = FriendCodeContract.Alphabet;
        var builder = new StringBuilder(FriendCodeContract.TotalLength);

        builder.Append(FriendCodeContract.Prefix);

        for (var group = 0; group < 2; group++)
        {
            if (group > 0)
            {
                builder.Append('-');
            }

            for (var symbol = 0; symbol < FriendCodeContract.GroupLength; symbol++)
            {
                // Uniform over the range; taking a raw byte modulo the alphabet
                // length would very slightly favour the earlier symbols.
                builder.Append(alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]);
            }
        }

        return builder.ToString();
    }

    /// <inheritdoc />
    public string NewDeviceId() => Guid.NewGuid().ToString("N");

    /// <summary>Encodes bytes as unpadded base64url.</summary>
    /// <param name="bytes">Bytes to encode.</param>
    /// <returns>A URL-safe string with no padding.</returns>
    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
