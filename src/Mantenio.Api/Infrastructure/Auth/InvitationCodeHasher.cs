using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Mantenio.Api.Infrastructure.Auth;

/// <summary>
/// Keyed hash (HMAC-SHA256) of invitation codes. A plain hash of a 6-digit code could be reversed by trying
/// all one million values, so the key (derived from the JWT signing key) never leaves the server.
/// </summary>
public sealed class InvitationCodeHasher(IOptions<JwtOptions> options)
{
    private readonly byte[] _key = HKDF.DeriveKey(
        HashAlgorithmName.SHA256,
        Encoding.UTF8.GetBytes(options.Value.SigningKey),
        outputLength: 32,
        info: Encoding.UTF8.GetBytes("mantenio-invitation-codes"));

    public string Hash(string code) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(code)));
}
