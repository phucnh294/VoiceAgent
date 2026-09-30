using Microsoft.Extensions.Options;
using VoiceAgent.Api.CallLogging;
using VoiceAgent.Api.Conversation;
using VoiceAgent.Api.Services;
using VoiceAgent.Api.Tools;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection(OllamaOptions.SectionName));
builder.Services.Configure<AssistantOptions>(builder.Configuration.GetSection(AssistantOptions.SectionName));
builder.Services.AddHttpClient<IChatModel, OllamaClient>((services, client) =>
{
    client.BaseAddress = new Uri(services.GetRequiredService<IOptions<OllamaOptions>>().Value.BaseUrl);
    // Covers only the wait for response headers (streaming reads are not timed), which can include
    // Ollama loading the model on the first call.
    client.Timeout = TimeSpan.FromMinutes(2);
});

// Tools the assistant may call. Add a new tool by implementing IServerTool or IClientTool.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAssistantTool, EndCallTool>();
builder.Services.AddSingleton<IAssistantTool, CurrentDateTimeTool>();

// Per-call JSON Lines log of every model request (system prompt included), response and tool call.
var callLogSection = builder.Configuration.GetSection(CallLogOptions.SectionName);
builder.Services.Configure<CallLogOptions>(callLogSection);
if (callLogSection.GetValue(nameof(CallLogOptions.Enabled), defaultValue: true))
{
    builder.Services.AddSingleton<ICallLog, JsonlCallLog>();
}
else
{
    builder.Services.AddSingleton<ICallLog, NullCallLog>();
}

builder.Services.AddSingleton<FarewellDetector>();
builder.Services.AddScoped<ConversationService>();

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
