using Athena.Core.Options;
using Athena.Web.Components;
using Athena.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Secrets come from the shared user-secrets store (hard constraint 3); the secret keys are
// root-level sections, so bind the whole configuration root.
var options = new AthenaOptions();
builder.Configuration.Bind(options);
builder.Services.AddSingleton(options);

// One shared retrieval stack for the app — built empty; documents are ingested on demand
// from the Corpus page. Per-circuit chat state on top of it.
builder.Services.AddSingleton<CorpusService>();
builder.Services.AddScoped<ChatSession>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
