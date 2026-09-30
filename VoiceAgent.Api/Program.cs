using VoiceAgent.Api.CallLogging;
using VoiceAgent.Api.Conversation;
using VoiceAgent.Api.Services;
using VoiceAgent.Api.Settings;
using VoiceAgent.Api.Tools;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

// Runtime settings (model, voice, prompts, tools) live in data/settings.json and are edited from
// the Settings panel. The Ollama/Assistant sections of appsettings.json only seed the first run.
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection(OllamaOptions.SectionName));
builder.Services.Configure<AssistantOptions>(builder.Configuration.GetSection(AssistantOptions.SectionName));
builder.Services.Configure<SettingsFileOptions>(builder.Configuration.GetSection(SettingsFileOptions.SectionName));
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<ISettingsProvider>(services => services.GetRequiredService<SettingsStore>());

// Chat models are created per turn from settings. The timeout covers only the wait for response
// headers (streaming reads are not timed), which can include Ollama loading the model.
builder.Services.AddHttpClient(ChatModelResolver.OllamaClientName, client => client.Timeout = TimeSpan.FromMinutes(2));
builder.Services.AddHttpClient(ChatModelResolver.GeminiClientName, client => client.Timeout = TimeSpan.FromMinutes(2));
builder.Services.AddSingleton<IChatModelResolver, ChatModelResolver>();

// Text-to-speech (Kokoro). Synthesis of one sentence normally takes well under a few seconds.
builder.Services.AddHttpClient(KokoroClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<KokoroClient>();

// Tools: built-ins plus webhook tools from settings. Each webhook applies its own timeout.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient(WebhookTool.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<IToolRegistry, ToolRegistry>();

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

// Load (or create) the settings file at startup, so a broken file is reported immediately.
_ = app.Services.GetRequiredService<SettingsStore>();

// Configure the HTTP request pipeline.
app.UseCors();
app.MapControllers();

app.Run();
