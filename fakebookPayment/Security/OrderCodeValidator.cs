namespace Fakebook.Payment.Security;

internal static class OrderCodeValidator
{
    public const long MaximumOrderCode = 9_007_199_254_740_991;

    public static bool TryParse(string? value, out long orderCode)
    {
        orderCode = 0;
        if (string.IsNullOrEmpty(value) || value.Length > 19)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out orderCode)
            && orderCode is > 0 and <= MaximumOrderCode;
    }
}
