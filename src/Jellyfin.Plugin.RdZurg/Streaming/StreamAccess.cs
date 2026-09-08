using System;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.RdZurg.Configuration;

namespace Jellyfin.Plugin.RdZurg.Streaming;

/// <summary>Scopes playback URLs to one file and this installation's current account.</summary>
public static class StreamAccess
{
    /// <summary>Reports whether a value is a canonical Real-Debrid content key.</summary>
    /// <param name="key">The candidate key.</param>
    /// <returns>Whether it has exactly 13 uppercase ASCII letters or digits.</returns>
    public static bool IsValidKey(string key)
        => key.Length == 13 && System.Linq.Enumerable.All(key, c => c is >= 'A' and <= 'Z' or >= '0' and <= '9');

    /// <summary>Signs a single content key; changing the account or signing key revokes old URLs.</summary>
    /// <param name="config">The current server configuration.</param>
    /// <param name="key">The content key.</param>
    /// <returns>A hexadecimal signature.</returns>
    public static string Sign(PluginConfiguration config, string key)
        => Convert.ToHexString(HMACSHA256.HashData(
            Convert.FromHexString(config.StreamSecret), Encoding.UTF8.GetBytes(config.ApiKey + "\n" + key)));

    /// <summary>Checks a capability before any provider request or cache lookup.</summary>
    /// <param name="config">The current server configuration.</param>
    /// <param name="key">The requested content key.</param>
    /// <param name="signature">The supplied signature.</param>
    /// <returns>Whether the signature authorizes this content key.</returns>
    public static bool Verify(PluginConfiguration config, string key, string signature)
    {
        if (!IsValidKey(key) || signature.Length != 64 || string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return false;
        }

        Span<byte> supplied = stackalloc byte[32];
        return Convert.FromHexString(signature, supplied, out _, out var written) == System.Buffers.OperationStatus.Done && written == 32
            && CryptographicOperations.FixedTimeEquals(supplied, Convert.FromHexString(Sign(config, key)));
    }
}
