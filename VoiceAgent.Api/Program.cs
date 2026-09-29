using Microsoft.Extensions.Options;
using VoiceAgent.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection(OllamaOptions.SectionName));
builder.Services.Configure<AssistantOptions>(builder.Configuration.GetSection(AssistantOptions.SectionName));
builder.Services.AddHttpClient<OllamaClient>((services, client) =>
{
    client.BaseAddress = new Uri(services.GetRequiredService<IOptions<OllamaOptions>>().Value.BaseUrl);
    // Covers only the wait for response headers (streaming reads are not timed), which can include
    // Ollama loading the model on the first call.
    client.Timeout = TimeSpan.FromMinutes(2);
});
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseCors();
app.MapControllers();

app.Run();
