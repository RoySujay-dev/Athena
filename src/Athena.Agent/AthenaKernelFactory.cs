using Athena.Core.Options;
using Athena.Filters;
using Athena.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

namespace Athena.Agent;

/// <summary>
/// Kernel wiring (brief §10): chat + embeddings, both plugin surfaces, all three filters.
/// The caller registers the plugin and filter INSTANCES in <paramref name="services"/> (they
/// carry per-session state — profile store, retrieved-context accessor — that only the
/// composition root can scope correctly); Build attaches whatever it finds. Filters are
/// registered by concrete type and attached explicitly, so each fires exactly once — never
/// discovered implicitly AND added manually.
/// </summary>
public static class AthenaKernelFactory
{
    public static Kernel Build(IServiceCollection services, AthenaOptions options)
    {
        services.AddOpenAIChatCompletion(options.OpenAI.ChatModelId, options.OpenAI.ApiKey);
        // The brief names AddOpenAITextEmbeddingGeneration; SK 1.78 obsoleted that surface in
        // favour of the Microsoft.Extensions.AI-based generator — which is also the interface
        // the whole ingestion/retrieval stack already consumes (README "Deviations").
#pragma warning disable SKEXP0010 // embedding registration is marked experimental across all of SK 1.x
        services.AddOpenAIEmbeddingGenerator(options.OpenAI.EmbeddingModel, options.OpenAI.ApiKey);
#pragma warning restore SKEXP0010

        ServiceProvider provider = services.BuildServiceProvider();
        var kernel = new Kernel(provider);

        kernel.Plugins.AddFromObject(provider.GetRequiredService<SearchPlugin>());
        kernel.Plugins.AddFromObject(provider.GetRequiredService<RecommendPlugin>());

        kernel.FunctionInvocationFilters.Add(provider.GetRequiredService<GroundingGuardFilter>());
        kernel.AutoFunctionInvocationFilters.Add(provider.GetRequiredService<TelemetryFilter>());
        kernel.PromptRenderFilters.Add(provider.GetRequiredService<PiiRedactionFilter>());

        return kernel;
    }
}
