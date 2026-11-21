using PawPals.Server;

var builder = WebApplication.CreateBuilder(args);
// Render supplies PORT; local launchSettings still uses localhost:5057.
if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), out int port) && port is > 0 and <= 65535)
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
string? publicOrigin = builder.Configuration["PawPals:PublicOrigin"] ?? builder.Configuration["RENDER_EXTERNAL_URL"];
if (!string.IsNullOrWhiteSpace(publicOrigin))
{
    if (!Uri.TryCreate(publicOrigin, UriKind.Absolute, out var origin)
        || origin.Scheme != "https" || origin.AbsolutePath != "/" || origin.Query.Length != 0
        || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
        throw new InvalidOperationException("PawPals__PublicOrigin harus berupa alamat dasar HTTPS layanan.");
    publicOrigin = origin.GetLeftPart(UriPartial.Authority);
}
string? database = builder.Configuration["DATABASE_URL"];
if (builder.Configuration.GetValue<bool>("PawPals:RequirePostgres") && string.IsNullOrWhiteSpace(database))
    throw new InvalidOperationException("DATABASE_URL belum diisi. Hosting tidak boleh memakai JSON sementara.");
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 8 * 1024;
    options.MaximumParallelInvocationsPerClient = 1;
    options.EnableDetailedErrors = false;
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(15);
    options.KeepAliveInterval = TimeSpan.FromSeconds(5);
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IRoomEvents, SignalRRoomEvents>();
builder.Services.AddSingleton<IRoomPersistence>(services => !string.IsNullOrWhiteSpace(database)
    ? new PostgresRoomPersistence(database, () => services.GetRequiredService<IHostApplicationLifetime>().StopApplication())
    : new JsonRoomPersistence(
        builder.Configuration["PawPals:DataFile"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PawPals", "Server", "rooms.json"),
        services.GetRequiredService<ILogger<JsonRoomPersistence>>()));
builder.Services.AddSingleton<RoomStore>();
builder.Services.AddHostedService<RoomMaintenanceWorker>();
builder.Services.AddHostedService<WorldWorker>();

var app = builder.Build();

// Optional one-time import. Reads room codes only and never changes the JSON source.
if (args.Length == 2 && args[0] == "--import-room-codes")
{
    if (app.Services.GetRequiredService<IRoomPersistence>() is not PostgresRoomPersistence postgres)
        throw new InvalidOperationException("Isi DATABASE_URL terlebih dahulu untuk impor.");
    int count = postgres.ImportCodes(args[1]);
    Console.WriteLine($"Impor selesai: {count} kode room. Anggota/token tidak diimpor; file sumber tetap utuh.");
    return;
}

// Browser memakai origin server yang sama; aplikasi .NET tidak mengirim Origin.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/pawpals")
        && context.Request.Headers.TryGetValue("Origin", out var origins))
    {
        // Explicit public origin supports TLS termination without trusting arbitrary forwarded headers.
        string expected = publicOrigin ?? $"{context.Request.Scheme}://{context.Request.Host}";
        if (!string.Equals(origins.ToString(), expected, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }
    await next();
});

app.MapGet("/", () => "Server PawPals aktif. Pemeriksaan: /health");
app.MapGet("/health", () => Results.Ok(new { status = "ok", server = "PawPals.Server", version = "0.10", protocolVersion = 10 }));
app.MapHub<PawPalsHub>("/pawpals");

if (app.Environment.IsDevelopment())
{
    app.MapGet("/test", () => Results.File(Path.Combine(app.Environment.ContentRootPath, "DevTools", "index.html"), "text/html"));
    app.MapGet("/test/app.js", () => Results.File(Path.Combine(app.Environment.ContentRootPath, "DevTools", "app.js"), "application/javascript"));
    app.MapGet("/test/signalr.min.js", () => Results.File(Path.Combine(app.Environment.ContentRootPath, "DevTools", "signalr.min.js"), "application/javascript"));
}
app.Run();
