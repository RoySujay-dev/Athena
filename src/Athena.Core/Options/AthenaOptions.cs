namespace Athena.Core.Options;

/// <summary>
/// Root options bound from configuration (user-secrets only — no keys ever live in source,
/// appsettings, or comments). The secret keys are root-level sections ("OpenAI:ApiKey",
/// "AzureDocumentIntelligence:Endpoint"), so bind this type from the configuration root:
/// <c>configuration.Get&lt;AthenaOptions&gt;()</c>. Plain POCOs: Athena.Core must stay free of
/// SK and Azure types, so the Azure DI section here is strings, not an Azure credential type.
/// </summary>
public sealed class AthenaOptions
{
    public OpenAIOptions OpenAI { get; set; } = new();
    public AzureDocumentIntelligenceOptions AzureDocumentIntelligence { get; set; } = new();
}

/// <summary>Bound from the "OpenAI" section of user-secrets.</summary>
public sealed class OpenAIOptions
{
    public const string SectionName = "OpenAI";

    public string ApiKey { get; set; } = string.Empty;
    public string ChatModelId { get; set; } = string.Empty;
    public string EmbeddingModel { get; set; } = string.Empty;
    public PricingOptions Pricing { get; set; } = new();
}

/// <summary>
/// USD list price per million tokens for the chat model, bound from "OpenAI:Pricing". Prices
/// drift faster than code, so they live in configuration next to the model id they describe
/// rather than as constants; both zero means "not configured" and telemetry logs a null cost
/// instead of a misleading $0.00.
/// </summary>
public sealed class PricingOptions
{
    public decimal InputUsdPerMillionTokens { get; set; }
    public decimal OutputUsdPerMillionTokens { get; set; }
}

/// <summary>Bound from the "AzureDocumentIntelligence" section of user-secrets.</summary>
public sealed class AzureDocumentIntelligenceOptions
{
    public const string SectionName = "AzureDocumentIntelligence";

    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}
