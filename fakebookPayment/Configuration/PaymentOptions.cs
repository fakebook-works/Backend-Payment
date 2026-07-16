using System.ComponentModel.DataAnnotations;

namespace Fakebook.Payment.Configuration;

public sealed class PaymentOptions
{
    public const string SectionName = "Payment";
    [Required, Url] public string PublicBaseUrl { get; init; } = string.Empty;
    [Required, Url] public string FrontendPublicUrl { get; init; } = string.Empty;
    public bool PaymentsEnabled { get; init; }
    [Range(1, 120)] public int CheckoutTtlMinutes { get; init; } = 30;
    [Range(0, 1023)] public int WorkerId { get; init; } = 1;
}

public sealed class PayOSOptions
{
    public const string SectionName = "PayOS";
    [Required] public string ClientId { get; init; } = string.Empty;
    [Required] public string ApiKey { get; init; } = string.Empty;
    [Required] public string ChecksumKey { get; init; } = string.Empty;
}

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";
    [Required, MinLength(32)] public string SharedSecret { get; init; } = string.Empty;
}

public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";
    [Required, Url] public string Endpoint { get; init; } = string.Empty;
    [Required, MinLength(32)] public string PaymentSecret { get; init; } = string.Empty;
}

public sealed class SocialGraphOptions
{
    public const string SectionName = "SocialGraph";
    [Required, Url] public string BaseUrl { get; init; } = string.Empty;
    [Required, MinLength(32)] public string InternalSecret { get; init; } = string.Empty;
}
