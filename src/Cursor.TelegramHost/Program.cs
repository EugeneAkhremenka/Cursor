using Cursor.Agent;
using Cursor.Telegram;
using Microsoft.Extensions.Options;
using Telegram.Bot;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));
builder.Services.Configure<CursorAgentOptions>(builder.Configuration.GetSection(CursorAgentOptions.SectionName));
builder.Services.AddSingleton<ICursorAgentHost, CursorAcpAgentHost>();
builder.Services.AddSingleton<ChatSessionBroker>();
builder.Services.AddSingleton<TelegramUpdateHandler>();
builder.Services.AddSingleton<ITelegramBotClient>(sp =>
{
    var token = sp.GetRequiredService<IOptions<TelegramOptions>>().Value.BotToken;
    return new TelegramBotClient(token);
});
builder.Services.AddHostedService<TelegramBotWorker>();

builder.Build().Run();
