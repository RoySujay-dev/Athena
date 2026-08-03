using Athena.Core.Options;
using Athena.Filters;

namespace Athena.Tests.Filters;

public sealed class TelemetryFilterTests
{
    private static PricingOptions Pricing(decimal input, decimal output)
        => new() { InputUsdPerMillionTokens = input, OutputUsdPerMillionTokens = output };

    [Fact]
    public void EstimateCostUsd_ComputesFromPerMillionRates()
    {
        // 1M input at $0.15 + 0.5M output at $0.60 = 0.15 + 0.30.
        decimal? cost = TelemetryFilter.EstimateCostUsd(1_000_000, 500_000, Pricing(0.15m, 0.60m));

        Assert.Equal(0.45m, cost);
    }

    [Fact]
    public void EstimateCostUsd_NullWhenUsageMissing()
        => Assert.Null(TelemetryFilter.EstimateCostUsd(null, null, Pricing(0.15m, 0.60m)));

    [Fact]
    public void EstimateCostUsd_NullWhenPricesNotConfigured()
        // Unconfigured prices must log null, never a misleading $0.00.
        => Assert.Null(TelemetryFilter.EstimateCostUsd(1_000, 200, Pricing(0m, 0m)));

    [Fact]
    public void EstimateCostUsd_TreatsMissingSideAsZeroTokens()
        => Assert.Equal(0.0003m, TelemetryFilter.EstimateCostUsd(2_000, null, Pricing(0.15m, 0.60m)));
}
