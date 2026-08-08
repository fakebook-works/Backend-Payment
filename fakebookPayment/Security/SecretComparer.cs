using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace Fakebook.Payment.Security;

public static class SecretComparer
{
    public static bool FixedTimeEqualsHeader(StringValues supplied, string expected) =>
        supplied.Count == 1 && FixedTimeEquals(supplied[0], expected);

    public static bool FixedTimeEquals(string? supplied, string expected)
    {
        if (string.IsNullOrEmpty(supplied)) return false;
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return suppliedBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }
}
