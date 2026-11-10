using System.Net;
using System.Text.Json;
using LocalAIShowroom.Bridge.Infrastructure;
using LocalAIShowroom.Bridge.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging.Abstractions;

const string model = "qwen2.5-0.5b-instruct-generic-cpu:4";
const string variants = """{"variants":[{"variantId":"qwen2.5-0.5b-instruct-generic-cpu:4"}]}""";

// The test executable doubles as the CLI subprocess to exercise the real process runner.
if (args.FirstOrDefault() == "--version")
{
    Console.WriteLine("0.11.0+test");
    return 0;
}
if (args.FirstOrDefault() == "model")
{
    var mode = Environment.GetEnvironmentVariable("SHOWROOM_TEST_CLI_MODE");
    if (mode == "must-not-run") return 99;
    if (args.ElementAtOrDefault(1) == "load")
    {
        if (mode == "ep-failure")
        {
            Console.WriteLine("{\"error\":{\"code\":\"INTERNAL_ERROR\",\"message\":\"failed to load model: PluginExecutionProviderFactory::CreateProvider Error creating provider " + new string('x', 700) + " TAIL-MARKER\"}}");
            return 1;
        }
        if (mode == "slow-load")
        {
            // Records when each load starts and ends so the test can prove loads never overlap.
            var log = Environment.GetEnvironmentVariable("SHOWROOM_TEST_LOAD_LOG");
            if (log is not null) File.AppendAllText(log, "S\n");
            Thread.Sleep(1200);
            if (log is not null) File.AppendAllText(log, "E\n");
        }
        Console.WriteLine(mode == "load-failure" ? "Insufficient memory" : """{"success":true}""");
        return mode == "load-failure" ? 7 : 0;
    }
    if (args.ElementAtOrDefault(1) == "download")
    {
        Console.WriteLine(mode == "download-failure" ? "Disk full" : """{"success":true}""");
        return mode == "download-failure" ? 9 : 0;
    }
    if (mode == "list-failure")
    {
        Console.WriteLine("Inventory unavailable");
        return 8;
    }
    if (mode == "slow-list") Thread.Sleep(4000); // a Foundry service that is too busy to answer quickly
    Console.WriteLine(mode switch
    {
        "malformed-json" => "not JSON",
        "unknown-shape" => """{"unexpected":[]}""",
        "not-loaded" when args.Contains("--loaded") => """{"variants":[]}""",
        "not-cached" when args.Contains("--cached") => """{"variants":[]}""",
        _ when !args.Contains("--loaded") && !args.Contains("--cached") =>
            """{"variants":[{"alias":"qwen2.5-0.5b","variantId":"qwen2.5-0.5b-instruct-generic-cpu:4","type":"Chat","fileSizeMb":822,"license":"apache-2.0","cached":true}]}""",
        _ => variants,
    });
    return 0;
}

void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

foreach (var json in new[]
{
    $$"""["{{model}}"]""",
    $$"""{"data":[{"id":"{{model}}"}]}""",
    $$"""{"models":[{"name":"{{model}}"}]}""",
    variants,
})
{
    Assert(FoundryLocalService.ParseModelIds(json)?.Single() == model, $"Model parsing failed: {json}");
}
Console.WriteLine("PASS legacy, OpenAI and CLI model payloads");

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
await using var app = builder.Build();
var responses = new Dictionary<string, (int Status, string Body)>();
var chatResponses = new Queue<(int Status, string ContentType, string Body)>();
var requests = new List<string>();
app.Run(async context =>
{
    var path = context.Request.Path.ToString();
    requests.Add(path);
    if (path == "/v1/chat/completions" && chatResponses.TryDequeue(out var chat))
    {
        context.Response.StatusCode = chat.Status;
        context.Response.ContentType = chat.ContentType;
        await context.Response.WriteAsync(chat.Body);
        return;
    }
    var response = responses.GetValueOrDefault(path, (404, "No mapping"));
    context.Response.StatusCode = response.Item1;
    await context.Response.WriteAsync(response.Item2);
});
await app.StartAsync();
var endpoint = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
var cli = Path.ChangeExtension(typeof(FoundryLocalService).Assembly.Location, ".exe");
Assert(File.Exists(cli), "Run the tests through their executable apphost.");

FoundryLocalService Service(string? cliOverride = null, int? ttlSeconds = null, int? cliTimeoutSeconds = null) => new(
    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["FoundryLocal:Endpoint"] = endpoint,
        ["FoundryLocal:Cli"] = cliOverride ?? cli,
        ["FoundryLocal:InventoryTtlSeconds"] = ttlSeconds?.ToString(),
        ["FoundryLocal:CliTimeoutSeconds"] = cliTimeoutSeconds?.ToString(),
    }).Build(),
    new EgressGuard(), NullLogger<FoundryLocalService>.Instance);

async Task ExpectFailure(Func<Task<object>> action, string message)
{
    try { await action(); }
    catch (InvalidOperationException ex) when (ex.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) { return; }
    throw new InvalidOperationException($"Expected an error containing '{message}'.");
}

try
{
    Environment.SetEnvironmentVariable("SHOWROOM_TEST_CLI_MODE", "must-not-run");
    responses[$"/openai/load/{model}"] = (200, "{}");
    var result = JsonSerializer.SerializeToElement(await Service().LoadAsync(model, default));
    Assert(result.GetProperty("loaded").GetBoolean(), "Legacy load did not succeed.");
    Console.WriteLine("PASS legacy load does not invoke CLI");

    responses[$"/openai/load/{model}"] = (500, "Backend failed");
    await ExpectFailure(() => Service().LoadAsync(model, default), "500");
    Console.WriteLine("PASS non-routing HTTP errors are preserved");

    foreach (var code in new[] { 404, 405 })
    {
        responses[$"/openai/load/{model}"] = (code, "No mapping");
        Environment.SetEnvironmentVariable("SHOWROOM_TEST_CLI_MODE", null);
        result = JsonSerializer.SerializeToElement(await Service().LoadAsync(model, default));
        Assert(result.GetProperty("loaded").GetBoolean(), $"CLI load fallback failed for {code}.");
    }
    Console.WriteLine("PASS 404/405 load fallback with loaded-state verification");

    Environment.SetEnvironmentVariable("SHOWROOM_TEST_CLI_MODE", "load-failure");
    await ExpectFailure(() => Service().LoadAsync(model, default), "Insufficient memory");
    Environment.SetEnvironmentVariable("SHOWROOM_TEST_CLI_MODE", "not-loaded");
    await ExpectFailure(() => Service().LoadAsync(model, default), "did not report");
    await ExpectFailure(() => Service(Path.Combine(Path.GetTempPath(), "missing-showroom-cli.exe")).LoadAsync(model, default), "requires the CLI");
    Console.WriteLine("PASS CLI errors, unconfirmed loads and missing CLI are surfaced");

    responses["/v1/models"] = (200, """{"data":[{"id":"not-downloaded-catalog-model"}]}""");
    Environment.SetEnvironmentVariable("SHOWROOM_TEST_CLI_MODE", null);
    result = JsonSerializer.SerializeToElement(await Service().GetStatusAsync(default));
    var models = result.GetProperty("models");
    Assert(result.GetProperty("running").GetBoolean(), "Modern inventory unavailable.");
    Assert(models.GetArrayLength() == 1, "Uncached catalog models were included.");
    Assert(models[0].GetProperty("Id").GetString() == model, "Variant ID lost.");
    Assert(models[0].GetProperty("Loaded").GetBoolean() && models[0].GetProperty("Cached").GetBoolean(), "Loaded/cached flags lost.");
    Console.WriteLine("PASS modern inventory uses cached/loaded variants, not catalog availability");

    foreach (var mode in new[] { "list-failure", "malformed-json", "unknown-shape" })
    {
        Environment.SetEnvironmentVariable("SHOWROOM_TEST_CLI_MODE", mode);
        result = JsonSerializer.SerializeToElement(await Service().GetStatusAsync(default));
        Assert(!result.GetProperty("running").GetBoolean() && result.GetProperty("error").GetString()!.Length > 0,
            $"Inventory error was hidden for {mode}.");
    }
    Console.WriteLine("PASS inventory failures and invalid JSON are surfaced");

    Environment.SetEnvironmentVariable("SHOWROOM_TEST_CLI_MODE", "must-not-run");
    responses["/openai/loadedmodels"] = (200, $$"""["{{model}}"]""");
    responses["/openai/models"] = (200, $$"""["{{model}}"]""");
    result = JsonSerializer.SerializeToElement(await Service().GetStatusAsync(default));
    Assert(result.GetProperty("running").GetBoolean(), "Legacy inventory invoked CLI.");
    Console.WriteLine("PASS legacy inventory remains HTTP-only");

    // The version comes from `foundry --version` in the background and shows on the next status call.
    var versionService = Service();
    await versionService.GetStatusAsync(default);
    if (versionService.PendingVersionLookup is { } versionLookup) await versionLookup.WaitAsync(TimeSpan.FromSeconds(30));
    result = JsonSerializer.SerializeToElement(await versionService.GetStatusAsync(default));
    Assert(result.GetProperty("version").GetString() == "0.11.0", "Foundry Local version not reported in the status.");
    Console.WriteLine("PASS status reports the Foundry Local version");

    requests.Clear();
    try
    {
        await Service().LoadAsync("bad id --option", default);
        throw new InvalidOperationException("Invalid model ID accepted.");
    }
    catch (ArgumentException) { }
    Assert(requests.Count == 0, "Invalid model ID reached the server.");
    Console.WriteLine("PASS invalid model IDs rejected before HTTP or CLI");

    Environment.SetEnvironmentVariable("SHOWROOM_TEST_CLI_MODE", null);
    result = JsonSerializer.SerializeToElement(await Service().GetCatalogAsync(default));
    var entry = result.GetProperty("models")[0];
    Assert(entry.GetProperty("Id").GetString() == model, "Catalog variant ID lost.");
    Assert(entry.GetProperty("SizeMb").GetDouble() == 822 && entry.GetProperty("Task").GetString() == "Chat", "Catalog metadata lost.");
    Assert(entry.GetProperty("Cached").GetBoolean() && entry.GetProperty("Loaded").GetBoolean(), "Catalog state lost.");
    result = JsonSerializer.SerializeToElement(await Service().DownloadAsync(model, default));
    Assert(result.GetProperty("cached").GetBoolean(), "Download did not verify cache state.");
    Console.WriteLine("PASS catalog metadata and verified download");

    // Hardware variant whose execution provider is missing: full error text plus an actionable hint.
    const string openvinoModel = "DeepSeek-R1-Distill-Qwen-1.5B-openvino-gpu:2";
    responses[$"/openai/load/{openvinoModel}"] = (404, "No mapping");
    Environment.SetEnvironmentVariable("SHOWROOM_TEST_CLI_MODE", "ep-failure");
    try
    {
        await Service().LoadAsync(openvinoModel, default);
        throw new InvalidOperationException("EP failure was not reported.");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("CLI exit 1"))
    {
        Assert(ex.Message.Contains("TAIL-MARKER"), "Foundry error text was truncated before the root cause.");
        Assert(ex.Message.Contains("OpenVINO execution provider"), "Missing execution provider hint.");
        Assert(ex.Message.Contains("generic-cpu"), "Missing offline fallback advice.");
    }
    Assert(FoundryLocalService.ExecutionProviderHint(model, "Insufficient memory") == "", "Hint shown for an unrelated generic-cpu failure.");
    Assert(FoundryLocalService.ExecutionProviderHint("any-model", "CreateProvider failed").Contains("matching hardware"), "Hint not shown for an unrecognised provider error.");
    Assert(FoundryLocalService.OldCliHint("● error: Unknown option '--variants'").Contains("0.13"), "No hint for an old Foundry CLI.");
    var incompatible = FoundryLocalService.ExecutionProviderHint("phi-4-mini-instruct-openvino-npu:4",
        "PluginExecutionProviderFactory::CreateProvider Error creating execution provider: [ONNXRuntimeError] : 9 : NOT_IMPLEMENTED : Experimental function OrtEpApi_ReleaseEpContextConfig_SinceV28 is not available in this build");
    Assert(incompatible.Contains("does not match") && incompatible.Contains("1261") && incompatible.Contains("generic-cpu") && incompatible.Contains("0.11.0"), "Missing hint for the OpenVINO / ONNX Runtime incompatibility.");
    Assert(incompatible.Contains("sideload") && incompatible.Contains("0.11.0 (reported to work"), "Hint does not offer Foundry 0.11.0 or the sideload.");
    Assert(FoundryLocalService.ParseVersion("0.11.0+abc123\r\n") == "0.11.0" && FoundryLocalService.ParseVersion("Foundry Local 0.13.0.5") == "0.13.0" && FoundryLocalService.ParseVersion("error") is null, "Foundry version parsing.");
    Assert(!incompatible.Contains("Download & register EPs"), "Wrongly told the user to download more providers for an incompatibility.");
    Assert(FoundryLocalService.OldCliHint("Insufficient memory") == "", "Old-CLI hint shown for an unrelated error.");
    Console.WriteLine("PASS missing execution provider: full error, hint, and no false positives");

    // Chat against a downloaded-but-unloaded model: load once, retry, report the load time separately.
    const string notLoadedError = """{"error":{"message":"Model not loaded: Model 'x' must be loaded before inference"}}""";
    const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\ndata: [DONE]\n\n";
    async Task<List<JsonElement>> Chat()
    {
        var events = new List<JsonElement>();
        var request = new LocalAIShowroom.Bridge.ChatRequest(model, new() { new("user", "hello") });
        await foreach (var evt in Service().StreamChatAsync(request, default))
        {
            events.Add(JsonSerializer.SerializeToElement(evt));
        }
        return events;
    }

    responses[$"/openai/load/{model}"] = (404, "No mapping");
    Environment.SetEnvironmentVariable("SHOWROOM_TEST_CLI_MODE", null);
    requests.Clear();
    chatResponses.Enqueue((400, "application/json", notLoadedError));
    chatResponses.Enqueue((200, "text/event-stream", sse));
    var chatEvents = await Chat();
    Assert(requests.Count(p => p == "/v1/chat/completions") == 2, "Chat was not retried after loading the model.");
    Assert(chatEvents[0].GetProperty("type").GetString() == "start" && chatEvents[0].TryGetProperty("loadMs", out var loadMsValue) && loadMsValue.ValueKind == JsonValueKind.Number, "Start event is missing loadMs.");
    Assert(chatEvents.Any(e => e.GetProperty("type").GetString() == "delta" && e.GetProperty("text").GetString() == "Hi"), "Answer was not streamed after the retry.");
    Assert(chatEvents[^1].GetProperty("type").GetString() == "done", "Stream did not complete.");

    requests.Clear();
    chatResponses.Enqueue((200, "text/event-stream", sse));
    chatEvents = await Chat();
    Assert(requests.Count(p => p == "/v1/chat/completions") == 1, "A loaded model was reloaded.");
    Assert(!chatEvents[0].TryGetProperty("loadMs", out var noLoad) || noLoad.ValueKind == JsonValueKind.Null, "A loaded model reported a load time.");

    chatResponses.Enqueue((400, "application/json", """{"error":{"message":"Bad temperature"}}"""));
    try
    {
        await Chat();
        throw new InvalidOperationException("Other 400 errors must not be swallowed.");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Bad temperature")) { }
    Console.WriteLine("PASS chat auto-loads an unloaded model once, reports loadMs, and keeps other errors");

    Environment.SetEnvironmentVariable("SHOWROOM_TEST_CLI_MODE", "download-failure");
    await ExpectFailure(() => Service().DownloadAsync(model, default), "Disk full");
    Environment.SetEnvironmentVariable("SHOWROOM_TEST_CLI_MODE", "not-cached");
    await ExpectFailure(() => Service().DownloadAsync(model, default), "did not report");
    await ExpectFailure(() => Service(Path.Combine(Path.GetTempPath(), "missing-showroom-cli.exe")).GetCatalogAsync(default), "was not found");
    Environment.SetEnvironmentVariable("SHOWROOM_TEST_CLI_MODE", "unknown-shape");
    await ExpectFailure(() => Service().GetCatalogAsync(default), "unrecognized JSON");
    foreach (var invalid in new[] { "--help", "-f", "bad id", "", "../model" })
    {
        try
        {
            await Service().DownloadAsync(invalid, default);
            throw new InvalidOperationException("Invalid download model ID accepted.");
        }
        catch (ArgumentException) { }
    }
    Console.WriteLine("PASS download errors, cache verification, missing CLI, malformed catalog and invalid IDs");

    // ---- A slow or busy Foundry service (the "foundry model list did not finish within 30 s" report) ----
    responses.Remove("/openai/loadedmodels");
    responses.Remove("/openai/models");
    responses[$"/openai/load/{model}"] = (404, "No mapping");
    Environment.SetEnvironmentVariable("SHOWROOM_TEST_CLI_MODE", null);

    // 1) A refresh that times out must not wipe the model list: the last known list stays, with a warning.
    var slowSvc = Service(ttlSeconds: 0, cliTimeoutSeconds: 1);
    var first = JsonSerializer.SerializeToElement(await slowSvc.GetStatusAsync(default));
    Assert(first.GetProperty("running").GetBoolean() && first.GetProperty("models").GetArrayLength() == 1, "First read did not return the model list.");
    Environment.SetEnvironmentVariable("SHOWROOM_TEST_CLI_MODE", "slow-list");
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var duringRefresh = JsonSerializer.SerializeToElement(await slowSvc.GetStatusAsync(default));
    Assert(clock.ElapsedMilliseconds < 1500, $"Status waited {clock.ElapsedMilliseconds} ms for a slow Foundry CLI instead of answering from its cache.");
    Assert(duringRefresh.GetProperty("models").GetArrayLength() == 1 && duringRefresh.GetProperty("running").GetBoolean(), "Cached list not served while refreshing.");
    await (slowSvc.PendingInventoryRefresh ?? Task.CompletedTask);
    var afterTimeout = JsonSerializer.SerializeToElement(await slowSvc.GetStatusAsync(default));
    Assert(afterTimeout.GetProperty("running").GetBoolean(), "A refresh timeout was reported as Foundry not running.");
    Assert(afterTimeout.GetProperty("models").GetArrayLength() == 1, "A refresh timeout emptied the model list.");
    Assert(afterTimeout.GetProperty("warning").GetString()!.Contains("last known"), "No warning that the list is stale.");
    Assert(afterTimeout.GetProperty("error").ValueKind == JsonValueKind.Null, "A stale list was reported as an error.");
    await (slowSvc.PendingInventoryRefresh ?? Task.CompletedTask);
    Console.WriteLine("PASS slow Foundry CLI: status answers from its cache and keeps the last known model list");

    // 2) While a model loads, status must not run more CLI commands, and reports that Foundry is busy.
    Environment.SetEnvironmentVariable("SHOWROOM_TEST_CLI_MODE", "slow-load");
    var busySvc = Service(ttlSeconds: 0);
    await busySvc.GetStatusAsync(default); // fill the cache
    var loading = busySvc.LoadAsync(model, default);
    await Task.Delay(400);
    Assert(busySvc.IsBusy, "Service did not report a load in progress.");
    clock.Restart();
    var busyStatus = JsonSerializer.SerializeToElement(await busySvc.GetStatusAsync(default));
    Assert(clock.ElapsedMilliseconds < 500 && busyStatus.GetProperty("busy").GetBoolean(), "Status was not immediate and busy during a load.");
    Assert(busyStatus.GetProperty("operation").GetString()!.Contains("Loading") && busyStatus.GetProperty("models").GetArrayLength() == 1, "Busy status lost the model list or operation name.");
    Assert(busyStatus.GetProperty("error").ValueKind == JsonValueKind.Null && busyStatus.GetProperty("running").GetBoolean(), "A load in progress was reported as an error.");
    await loading;
    Assert(!busySvc.IsBusy, "Service stayed busy after the load finished.");
    Console.WriteLine("PASS status during a load: immediate, busy, keeps the model list");

    // 3) Parallel loads (two Arena lanes, two Load clicks) run one after the other.
    var loadLog = Path.Combine(Path.GetTempPath(), $"showroom-load-log-{Guid.NewGuid():N}.txt");
    Environment.SetEnvironmentVariable("SHOWROOM_TEST_LOAD_LOG", loadLog);
    var serialSvc = Service();
    await Task.WhenAll(serialSvc.LoadAsync(model, default), serialSvc.LoadAsync(model, default));
    var order = File.ReadAllLines(loadLog);
    File.Delete(loadLog);
    Environment.SetEnvironmentVariable("SHOWROOM_TEST_LOAD_LOG", null);
    Assert(string.Join("", order) == "SESE", $"Loads overlapped: {string.Join("", order)} (expected SESE).");
    Console.WriteLine("PASS parallel loads are serialized");

    // 4) The CLI is only asked once whether the legacy HTTP load API exists.
    Environment.SetEnvironmentVariable("SHOWROOM_TEST_CLI_MODE", null);
    requests.Clear();
    var legacySvc = Service();
    await legacySvc.LoadAsync(model, default);
    await legacySvc.LoadAsync(model, default);
    Assert(requests.Count(p => p.StartsWith("/openai/load/")) == 1, "The unavailable legacy load API was called again.");
    Console.WriteLine("PASS legacy load API is skipped after the first 404");

    // 5) A load that succeeded must not turn into an error because the follow-up check is slow or failing.
    Environment.SetEnvironmentVariable("SHOWROOM_TEST_CLI_MODE", "list-failure");
    var lenient = JsonSerializer.SerializeToElement(await Service().LoadAsync(model, default));
    Assert(lenient.GetProperty("loaded").GetBoolean(), "A successful load failed because the confirmation list failed.");
    Environment.SetEnvironmentVariable("SHOWROOM_TEST_CLI_MODE", "slow-list");
    var lenientSlow = JsonSerializer.SerializeToElement(await Service(cliTimeoutSeconds: 1).LoadAsync(model, default));
    Assert(lenientSlow.GetProperty("loaded").GetBoolean(), "A successful load failed because the confirmation list timed out.");
    Console.WriteLine("PASS a successful load is not failed by a slow confirmation check");

    // ======================================================================================== OpenVINO sideload
    OpenVinoSideloadService Side(string? root, string? modelRoots, bool generic = false) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenVinoSideload:Root"] = root,
            ["OpenVinoSideload:ModelRoots"] = modelRoots,
            ["OpenVinoSideload:IncludeGenericModels"] = generic ? "true" : "false",
            ["FoundryLocal:Cli"] = Path.Combine(Path.GetTempPath(), "no-such-foundry.exe"),
        }).Build(),
        NullLogger<OpenVinoSideloadService>.Instance);

    JsonElement[] ModelsOf(OpenVinoSideloadService s) =>
        JsonSerializer.SerializeToElement(s.GetStatus()).GetProperty("models").EnumerateArray().ToArray();

    // 1) Model discovery from a Foundry-style cache folder (no Python needed).
    var fakeCache = Path.Combine(Path.GetTempPath(), "showroom-ov-fake-" + Guid.NewGuid().ToString("N"));
    foreach (var folder in new[] { @"Microsoft\foo-bar-openvino-npu-4\v4", @"Microsoft\foo-openvino-gpu-2\v2", @"Microsoft\generic-cpu-3\v3", @"Microsoft\bar-openvino-cpu-7\v7" })
    {
        Directory.CreateDirectory(Path.Combine(fakeCache, folder));
        File.WriteAllText(Path.Combine(fakeCache, folder, "genai_config.json"), "{}");
    }
    Directory.CreateDirectory(Path.Combine(fakeCache, @"Microsoft\broken-openvino-npu-1\v1")); // no genai_config.json: not a model
    var notInstalledRoot = Path.Combine(Path.GetTempPath(), "showroom-no-sideload-" + Guid.NewGuid().ToString("N"));
    using (var discovery = Side(notInstalledRoot, fakeCache))
    {
        var found = ModelsOf(discovery).ToDictionary(m => m.GetProperty("id").GetString()!, m => m.GetProperty("device").GetString()!);
        Assert(found.ContainsKey("foo-bar-openvino-npu:4") && found.ContainsKey("foo-openvino-gpu:2") && found.ContainsKey("bar-openvino-cpu:7"), $"Expected the 3 fake OpenVINO models, found: {string.Join(", ", found.Keys)}");
        Assert(found["foo-bar-openvino-npu:4"] == "NPU" && found["foo-openvino-gpu:2"] == "GPU" && found["bar-openvino-cpu:7"] == "CPU", "Model ids or devices were not derived from the folder names.");
        Assert(!found.ContainsKey("generic-cpu:3") && !found.ContainsKey("broken-openvino-npu:1"), "A non-OpenVINO or incomplete folder was listed.");
        Assert(found.Keys.All(k => k.Contains("openvino", StringComparison.OrdinalIgnoreCase)), "A model that is not an OpenVINO variant was listed.");
    }
    using (var withGeneric = Side(notInstalledRoot, fakeCache, generic: true))
    {
        var all = ModelsOf(withGeneric).ToDictionary(m => m.GetProperty("id").GetString()!, m => m.GetProperty("device").GetString()!);
        Assert(all.TryGetValue("generic-cpu:3", out var genericDevice) && genericDevice == "default" && all.Count >= 4, "IncludeGenericModels did not list the generic model with the default device.");
    }
    Console.WriteLine("PASS sideload discovers OpenVINO models from a Foundry cache and skips other folders");

    // 2) Not installed: a clear message, no process started, nothing crashes.
    using (var absent = Side(notInstalledRoot, fakeCache))
    {
        var status = JsonSerializer.SerializeToElement(absent.GetStatus());
        Assert(!status.GetProperty("installed").GetBoolean() && status.GetProperty("setup").GetString()!.Contains("setup-openvino-sideload"), "Missing install not reported.");
        try
        {
            await absent.LoadAsync("foo-bar-openvino-npu:4", default);
            throw new InvalidOperationException("Load worked without an installation.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("setup-openvino-sideload")) { }
        Assert(!absent.IsBusy, "Service stayed busy after a failed load.");
        foreach (var bad in new[] { "--help", "", "../x", "not-downloaded:1" })
        {
            try
            {
                await absent.LoadAsync(bad, default);
                throw new InvalidOperationException($"Invalid or unknown model '{bad}' was accepted.");
            }
            catch (ArgumentException) { }
        }
    }
    Directory.Delete(fakeCache, recursive: true);
    Console.WriteLine("PASS sideload not installed: clear setup message, unknown or unsafe model ids rejected");

    // 3) With a real installation: load, stream, cancel, concurrency, crash recovery. Needs SHOWROOM_TEST_SIDELOAD_ROOT and
    //    SHOWROOM_TEST_MODEL_DIR (a Foundry model folder that contains genai_config.json).
    var sideRoot = Environment.GetEnvironmentVariable("SHOWROOM_TEST_SIDELOAD_ROOT");
    var sideModelDir = Environment.GetEnvironmentVariable("SHOWROOM_TEST_MODEL_DIR");
    if (string.IsNullOrEmpty(sideRoot) || string.IsNullOrEmpty(sideModelDir) || !File.Exists(Path.Combine(sideModelDir, "genai_config.json")))
    {
        Console.WriteLine("SKIP real OpenVINO sideload tests (set SHOWROOM_TEST_SIDELOAD_ROOT and SHOWROOM_TEST_MODEL_DIR to run them)");
    }
    else
    {
        // A cache folder whose name makes the (generic CPU) test model look like an OpenVINO CPU variant: the worker then
        // really runs it through the sideloaded OpenVINO provider on the CPU device.
        var cache = Path.Combine(Path.GetTempPath(), "showroom-ov-cache-" + Guid.NewGuid().ToString("N"));
        var variantDir = Path.Combine(cache, "Microsoft", "qwen-test-openvino-cpu-1");
        var link = Path.Combine(variantDir, "v1");
        Directory.CreateDirectory(variantDir);
        void Cmd(string command)
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c " + command) { CreateNoWindow = true, UseShellExecute = false })!;
            p.WaitForExit();
        }
        Cmd($"mklink /J \"{link}\" \"{sideModelDir}\" >nul");
        Assert(Directory.Exists(link), "Could not create the test model junction.");
        const string testModel = "qwen-test-openvino-cpu:1";

        async Task<List<JsonElement>> SideChat(OpenVinoSideloadService s, string prompt, int maxTokens = 40, CancellationToken token = default)
        {
            var events = new List<JsonElement>();
            var request = new LocalAIShowroom.Bridge.ChatRequest(testModel, new() { new("system", "Be brief."), new("user", prompt) }, 0.2f, maxTokens);
            await foreach (var evt in s.StreamChatAsync(request, token)) events.Add(JsonSerializer.SerializeToElement(evt));
            return events;
        }
        string Text(List<JsonElement> events) => string.Concat(events.Where(e => e.GetProperty("type").GetString() == "delta").Select(e => e.GetProperty("text").GetString()));
        bool HasLoadMs(JsonElement start) => start.TryGetProperty("loadMs", out var v) && v.ValueKind == JsonValueKind.Number;

        try
        {
            using var side = Side(sideRoot, cache);
            var status = JsonSerializer.SerializeToElement(side.GetStatus());
            Assert(status.GetProperty("installed").GetBoolean(), "The real installation was not recognised.");
            var mine = ModelsOf(side).Where(m => m.GetProperty("id").GetString() == testModel).ToArray();
            Assert(mine.Length == 1 && mine[0].GetProperty("device").GetString() == "CPU", "Test model not discovered with the CPU device.");

            // streaming + load time reported once
            var clock2 = System.Diagnostics.Stopwatch.StartNew();
            var first2 = await SideChat(side, "Name one benefit of running AI locally.");
            Assert(first2[0].GetProperty("type").GetString() == "start" && HasLoadMs(first2[0]), "First chat did not report the load time.");
            Assert(first2[0].GetProperty("engine").GetString()!.Contains("OpenVINO"), "Wrong engine name.");
            var done = first2[^1];
            Assert(done.GetProperty("type").GetString() == "done" && done.GetProperty("status").GetString() == "Complete", "Chat did not complete.");
            Assert(done.GetProperty("usage").GetProperty("completionTokens").GetInt32() > 0 && Text(first2).Trim().Length > 0, "No tokens or text streamed.");
            Assert(first2.Count(e => e.GetProperty("type").GetString() == "delta") > 1, "The answer was not streamed in pieces.");
            Assert(JsonSerializer.SerializeToElement(side.GetStatus()).GetProperty("loaded").GetString() == testModel, "Status does not show the loaded model.");
            var second = await SideChat(side, "Say hello.");
            Assert(!HasLoadMs(second[0]) && second[^1].GetProperty("status").GetString() == "Complete", "The loaded model was reloaded for the next chat.");
            Console.WriteLine($"PASS sideload loads a model, streams an answer and keeps it loaded ({clock2.ElapsedMilliseconds} ms incl. load)");

            // cancel in the middle: the worker must stay usable and keep the model
            var cts = new CancellationTokenSource();
            var seen = 0;
            try
            {
                var longRequest = new LocalAIShowroom.Bridge.ChatRequest(testModel, new() { new("user", "Write a very long story about a dragon.") }, 0.8f, 400);
                await foreach (var evt in side.StreamChatAsync(longRequest, cts.Token))
                {
                    if (JsonSerializer.SerializeToElement(evt).GetProperty("type").GetString() == "delta" && ++seen == 3) cts.Cancel();
                }
                throw new InvalidOperationException("The cancelled chat was not interrupted.");
            }
            catch (OperationCanceledException) { }
            Assert(seen >= 3 && !side.IsBusy, "Cancel did not stop the generation or left the service busy.");
            var afterCancel = await SideChat(side, "Say hello again.");
            Assert(!HasLoadMs(afterCancel[0]) && afterCancel[^1].GetProperty("status").GetString() == "Complete", "The worker lost its model or went out of sync after a cancel.");
            Console.WriteLine("PASS sideload cancel stops the answer and the loaded model stays usable");

            // two chats at once are served one after the other
            var both = await Task.WhenAll(SideChat(side, "Name a color."), SideChat(side, "Name an animal."));
            Assert(both.All(c => c[^1].GetProperty("status").GetString() == "Complete" && Text(c).Trim().Length > 0), "Concurrent chats did not both complete.");
            Console.WriteLine("PASS sideload serializes concurrent chats");

            // the worker dies (killed): the next chat starts a new one by itself
            foreach (var proc in System.Diagnostics.Process.GetProcessesByName("python"))
            {
                try
                {
                    if (proc.MainModule?.FileName?.StartsWith(sideRoot, StringComparison.OrdinalIgnoreCase) == true) { proc.Kill(entireProcessTree: true); proc.WaitForExit(5000); }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
            var recovered = await SideChat(side, "Say hi.");
            Assert(HasLoadMs(recovered[0]) && recovered[^1].GetProperty("status").GetString() == "Complete", "A killed worker was not replaced.");
            Console.WriteLine("PASS sideload replaces a worker that was killed");

            // unload frees the worker
            await side.UnloadAsync(default);
            Assert(JsonSerializer.SerializeToElement(side.GetStatus()).GetProperty("loaded").ValueKind == JsonValueKind.Null, "Unload did not free the model.");
            var explicitLoad = JsonSerializer.SerializeToElement(await side.LoadAsync(testModel, default));
            Assert(explicitLoad.GetProperty("loaded").GetBoolean() && explicitLoad.GetProperty("ms").GetInt64() > 0, "Explicit load failed.");
            Console.WriteLine("PASS sideload unload and explicit load");
        }
        finally
        {
            Cmd($"rmdir \"{link}\""); // removes only the junction, never the real model
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
            Assert(File.Exists(Path.Combine(sideModelDir, "genai_config.json")), "The real model folder was damaged by the test cleanup.");
        }
    }
}
finally
{
    Environment.SetEnvironmentVariable("SHOWROOM_TEST_CLI_MODE", null);
    await app.StopAsync();
}

Console.WriteLine("All Foundry Local regression checks passed.");
return 0;
