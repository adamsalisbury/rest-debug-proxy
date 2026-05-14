using RestDebugProxy;

if (args.Length < 1 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return 0;
}

var targetEndpoint = args[0].TrimEnd('/');
if (!Uri.TryCreate(targetEndpoint, UriKind.Absolute, out _))
{
    Console.Error.WriteLine($"Invalid target URL: {targetEndpoint}");
    PrintUsage();
    return 1;
}

var listenEndpoint = args.Length > 1 ? args[1] : "http://localhost:5000";
if (!Uri.TryCreate(listenEndpoint, UriKind.Absolute, out _))
{
    Console.Error.WriteLine($"Invalid listen URL: {listenEndpoint}");
    PrintUsage();
    return 1;
}

var baseDirectory = AppContext.BaseDirectory;
var cannedDirectory = Path.Combine(baseDirectory, "candresp");
var trafficDirectory = Path.Combine(baseDirectory, "traffic");

Directory.CreateDirectory(cannedDirectory);
Directory.CreateDirectory(trafficDirectory);

var state = new AppState
{
    ListenEndpoint = listenEndpoint,
    TargetEndpoint = targetEndpoint
};

var cannedStore = new CannedResponseStore(cannedDirectory);
cannedStore.LoadAll();

var trafficLogger = new TrafficLogger(trafficDirectory);

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls(listenEndpoint);
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
});

builder.Services.AddSingleton(state);
builder.Services.AddSingleton(cannedStore);
builder.Services.AddSingleton(trafficLogger);
builder.Services.AddHttpClient("proxy")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None
    })
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.ExpectContinue = false;
    });

var app = builder.Build();

app.Run(async context =>
{
    var handler = new ProxyHandler(
        state,
        app.Services.GetRequiredService<IHttpClientFactory>(),
        cannedStore,
        trafficLogger);
    await handler.HandleAsync(context);
});

var uiCts = new CancellationTokenSource();
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

var ui = new ConsoleUi(state, cannedStore, async () =>
{
    uiCts.Cancel();
    lifetime.StopApplication();
    await Task.CompletedTask;
});

lifetime.ApplicationStopping.Register(() => uiCts.Cancel());

var uiTask = ui.RunAsync(uiCts.Token);

try
{
    await app.RunAsync();
}
finally
{
    uiCts.Cancel();
    try { await uiTask; }
    catch (OperationCanceledException) { /* expected */ }
}

return 0;

static void PrintUsage()
{
    Console.WriteLine("REST Debug Proxy");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  RestDebugProxy <target-url> [listen-url]");
    Console.WriteLine();
    Console.WriteLine("Example:");
    Console.WriteLine("  RestDebugProxy http://appserver/target/api/ http://localhost:5000");
    Console.WriteLine();
    Console.WriteLine("Default listen URL: http://localhost:5000");
}
