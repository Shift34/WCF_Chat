using ChatServer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ChatSessionService>();
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 64 * 1024;
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.StreamBufferCapacity = 4;
}).AddMessagePackProtocol();

var app = builder.Build();

app.MapGet("/", () => "Chat server is running. Hub: /chat");
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapHub<ChatHub>("/chat");

app.Run();
