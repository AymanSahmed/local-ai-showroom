// Local AI Showroom - local bridge between the browser and Windows on-device AI.
//
// The bridge listens on loopback only and talks to nothing but local runtimes:
//   - Windows AI APIs (Windows App SDK 2.x): built-in language model, OCR, image description
//   - Windows ML (ONNX Runtime + EP catalog): your own ONNX model on NPU / GPU / CPU
//   - Foundry Local: open models behind an OpenAI-compatible endpoint on localhost
//   - winml-cli: optional benchmarking of the same ONNX model
//   - Aion Instruct Preview: optional lane (build with -p:EnableAion=true)

using System.Text.Json.Serialization;
using LocalAIShowroom.Bridge;
using LocalAIShowroom.Bridge.Infrastructure;
using LocalAIShowroom.Bridge.Services;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});

// Optional machine-specific overrides written by scripts/setup.ps1 (for example the winml-cli path).
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false);
builder.Configuration.AddCommandLine(args);

var url = builder.Configuration["Showroom:Url"] ?? "http://127.0.0.1:5080";
if (!Uri.TryCreate(url, UriKind.Absolute, out var bindUri) || !bindUri.IsLoopback)
{
    throw new InvalidOperationException($"Showroom:Url must be a loopback address (got '{url}'). The bridge never listens on the network.");
}
builder.WebHost.UseUrls(url);

// A second copy on the same port would die with a raw stack trace. Say what is wrong and how to fix it instead.
if (!PortIsFree(bindUri.Port))
{
    var owner = "another program";
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var ping = await http.GetStringAsync($"http://127.0.0.1:{bindUri.Port}/api/ping");
        if (ping.Contains("Local AI Showroom")) owner = "another copy of Local AI Showroom";
    }
    catch { /* not ours, or not answering */ }
    Console.Error.WriteLine($"Port {bindUri.Port} is already in use by {owner}.");
    Console.Error.WriteLine($"Open {url} if that copy is yours, stop it first, or choose another port: --Showroom:Url=http://127.0.0.1:{bindUri.Port + 10}");
    Environment.Exit(2);
}

static bool PortIsFree(int port)
{
    try
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        probe.Start();
        probe.Stop();
        return true;
    }
    catch (System.Net.Sockets.SocketException)
    {
        return false;
    }
}

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddSingleton<EgressGuard>();
builder.Services.AddSingleton<FoundryLocalService>();
builder.Services.AddSingleton<WindowsAIService>();
builder.Services.AddSingleton<WindowsMLService>();
builder.Services.AddSingleton<WinmlCliService>();
builder.Services.AddSingleton<AionService>();
builder.Services.AddSingleton<OpenVinoSideloadService>();
builder.Services.AddSingleton<StatusService>();

var app = builder.Build();

// 1) JSON errors for every API call (streams report errors in-band instead).
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex) when (!ctx.Response.HasStarted && !ctx.RequestAborted.IsCancellationRequested)
    {
        ctx.Response.StatusCode = ex is ArgumentException or InvalidDataException or NotSupportedException ? 400
            : ex is FileNotFoundException ? 404
            : 500;
        app.Logger.LogWarning("{Path} failed: {Message}", ctx.Request.Path, ex.Message);
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message, kind = ex.GetType().Name, hresult = Errors.HResult(ex) });
    }
});

// 2) Local-only guard: rejects DNS-rebinding hosts and cross-origin browser requests.
app.Use(async (ctx, next) =>
{
    if (!LocalOnly.IsLocalHost(ctx.Request.Host.Host))
    {
        ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
        await ctx.Response.WriteAsync("Local requests only.");
        return;
    }

    var origin = ctx.Request.Headers.Origin.ToString();
    if (!string.IsNullOrEmpty(origin) && (!Uri.TryCreate(origin, UriKind.Absolute, out var o) || !LocalOnly.IsLocalHost(o.Host)))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsync("Cross-origin requests are not allowed.");
        return;
    }

    await next();
});

app.UseDefaultFiles();
// The UI is edited in place during development; make browsers revalidate instead of reusing stale JS modules.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

var api = app.MapGroup("/api");

api.MapGet("/ping", () => Results.Ok(new { ok = true, app = "Local AI Showroom", version = BuildInfo.Version, simulated = false }));
api.MapGet("/status", (StatusService s, CancellationToken ct) => s.GetAsync(ct));

// ---- Windows AI APIs (Windows App SDK) ------------------------------------------------------
api.MapPost("/windows-ai/prepare", async (PrepareRequest req, WindowsAIService ai, CancellationToken ct) =>
    Results.Ok(await ai.PrepareAsync(req.Feature, ct)));

api.MapPost("/windows-ai/generate", (HttpContext ctx, GenerateRequest req, WindowsAIService ai) =>
    Sse.StreamAsync(ctx, ai.StreamGenerateAsync(req.Prompt, req.System, req.Temperature, ctx.RequestAborted), app.Logger));

api.MapPost("/windows-ai/summarize", async (TextRequest req, WindowsAIService ai, CancellationToken ct) =>
    Results.Ok(await ai.SummarizeAsync(req.Text, ct)));

api.MapPost("/windows-ai/rewrite", async (TextRequest req, WindowsAIService ai, CancellationToken ct) =>
    Results.Ok(await ai.RewriteAsync(req.Text, ct)));

api.MapPost("/windows-ai/ocr", async (ImageRequest req, WindowsAIService ai, CancellationToken ct) =>
    Results.Ok(await ai.RecognizeTextAsync(ImageData.Decode(req.Image), ct)));

api.MapPost("/windows-ai/describe", async (ImageRequest req, WindowsAIService ai, CancellationToken ct) =>
    Results.Ok(await ai.DescribeImageAsync(ImageData.Decode(req.Image), req.Kind, ct)));

// ---- Foundry Local ---------------------------------------------------------------------------
api.MapGet("/foundry/models", async (FoundryLocalService f, CancellationToken ct) =>
    Results.Ok(await f.GetStatusAsync(ct)));

api.MapGet("/foundry/catalog", async (FoundryLocalService f, CancellationToken ct) =>
    Results.Ok(await f.GetCatalogAsync(ct)));

api.MapPost("/foundry/download", async (ModelRequest req, FoundryLocalService f, CancellationToken ct) =>
    Results.Ok(await f.DownloadAsync(req.Model, ct)));

api.MapPost("/foundry/start", async (FoundryLocalService f, IConfiguration config, CancellationToken ct) =>
    Results.Ok(await f.StartAsync(int.TryParse(config["FoundryLocal:Port"], out var p) ? p : null, ct)));

api.MapPost("/foundry/load", async (ModelRequest req, FoundryLocalService f, CancellationToken ct) =>
    Results.Ok(await f.LoadAsync(req.Model, ct)));

api.MapPost("/foundry/chat", (HttpContext ctx, ChatRequest req, FoundryLocalService f) =>
    Sse.StreamAsync(ctx, f.StreamChatAsync(req, ctx.RequestAborted), app.Logger));

// ---- Windows ML ------------------------------------------------------------------------------
api.MapPost("/windows-ml/prepare-eps", async (WindowsMLService ml, CancellationToken ct) =>
    Results.Ok(await ml.PrepareExecutionProvidersAsync(ct)));

api.MapPost("/windows-ml/classify", async (ClassifyRequest req, WindowsMLService ml, CancellationToken ct) =>
    Results.Ok(await ml.ClassifyAsync(ImageData.Decode(req.Image), req.Device ?? "auto", Math.Clamp(req.Iterations ?? 10, 1, 200), ct)));

// ---- winml-cli (optional) --------------------------------------------------------------------
api.MapPost("/winml-cli/sys", async (WinmlCliService cli, CancellationToken ct) =>
    Results.Ok(await cli.SysAsync(ct)));

api.MapPost("/winml-cli/perf", async (PerfRequest req, WinmlCliService cli, CancellationToken ct) =>
    Results.Ok(await cli.PerfAsync(req.Device ?? "auto", Math.Clamp(req.Iterations ?? 50, 5, 1000), ct)));

// ---- OpenVINO sideload (optional): runs the cached OpenVINO model variants outside Foundry Local --------
api.MapGet("/openvino/models", (OpenVinoSideloadService o) => Results.Ok(o.GetStatus()));

api.MapPost("/openvino/load", async (ModelRequest req, OpenVinoSideloadService o, CancellationToken ct) =>
    Results.Ok(await o.LoadAsync(req.Model, ct)));

api.MapPost("/openvino/unload", async (OpenVinoSideloadService o, CancellationToken ct) =>
    Results.Ok(await o.UnloadAsync(ct)));

api.MapPost("/openvino/chat", (HttpContext ctx, ChatRequest req, OpenVinoSideloadService o) =>
    Sse.StreamAsync(ctx, o.StreamChatAsync(req, ctx.RequestAborted), app.Logger));

// ---- Aion Instruct Preview (optional) --------------------------------------------------------
api.MapPost("/aion/generate", (HttpContext ctx, GenerateRequest req, AionService aion) =>
    Sse.StreamAsync(ctx, aion.StreamGenerateAsync(req.Prompt, req.System, ctx.RequestAborted), app.Logger));

app.Lifetime.ApplicationStarted.Register(() =>
{
    var (hasIdentity, family) = WindowsAIService.Identity();
    Console.WriteLine();
    Console.WriteLine("  Local AI Showroom bridge is running");
    Console.WriteLine($"  UI ............ {url}");
    Console.WriteLine($"  Identity ...... {(hasIdentity ? family : "none (Windows AI APIs disabled; run with 'dotnet run' + Developer Mode)")}");
    Console.WriteLine("  Network ....... loopback only; outbound calls limited to localhost");
    Console.WriteLine("  Press Ctrl+C to stop.");
    Console.WriteLine();

    if (!string.Equals(app.Configuration["Showroom:OpenBrowser"], "false", StringComparison.OrdinalIgnoreCase))
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { app.Logger.LogInformation("Open {Url} in your browser ({Message}).", url, ex.Message); }
    }
});

app.Run();
