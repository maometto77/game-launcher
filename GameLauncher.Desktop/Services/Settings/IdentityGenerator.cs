using System.Security.Cryptography;
using System.Text;
using GameLauncher.Shared.Contracts;

namespace GameLauncher.Desktop.Services.Settings;

/// <summary>
/// Default <see cref="IIdentityGenerator"/>.
/// </summary>
public sealed class IdentityGenerator : IIdentityGenerator
{
    /// <inheritdoc />
    /// <remarks>
    /// Symbols are drawn with <see cref="RandomNumberGenerator"/> rather than
    /// <see cref="Random"/>. A friend code is a public identifier, but one that is
    /// predictable from a seed would let somebody enumerate other people's codes,
    /// and the cost of using the cryptographic generator is nil.
    /// </remarks>
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
                // GetInt32 is uniform over the range; taking a raw byte modulo the
                // alphabet length would very slightly favour the earlier symbols.
                builder.Append(alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]);
            }
        }

        return builder.ToString();
    }

    /// <inheritdoc />
    public string SuggestDisplayName()
    {
        var name = Environment.UserName;

        if (string.IsNullOrWhiteSpace(name))
        {
            return "Player";
        }

        // Trimmed to something a friends list can render without eliding it.
        name = name.Trim();
        return name.Length > 32 ? name[..32] : name;
    }
}
