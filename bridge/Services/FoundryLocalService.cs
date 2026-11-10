using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalAIShowroom.Bridge.Infrastructure;

namespace LocalAIShowroom.Bridge.Services;

/// <summary>
/// Foundry Local: open models (Phi, Qwen, DeepSeek, Mistral...) behind an OpenAI-compatible REST API on localhost.
/// The port is dynamic, so the endpoint is discovered with 'foundry server status' unless FoundryLocal:Endpoint is set.
/// </summary>
public sealed partial class FoundryLocalService
{
    [GeneratedRegex(@"https?://(?:127\.0\.0\.1|localhost|\[::1\]):(\d{1,5})", RegexOptions.IgnoreCase)]
    private static partial Regex LocalUrl();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:\-]{0,199}$")]
    private static partial Regex SafeModelId();

    public sealed record FoundryModel(string Id, string Device, bool Loaded, bool Cached);
    public sealed record CatalogModel(string Id, string Alias, string Device, string Task, double? SizeMb, string License, bool Cached, bool Loaded);

    private readonly HttpClient _http;
    private readonly ILogger<FoundryLocalService> _log;
    private readonly string? _configuredEndpoint;
    private readonly string _cli;
    private readonly int _loadTtlSeconds;
    private readonly TimeSpan _inventoryTtl;
    private readonly TimeSpan _cliListTimeout;
    private readonly TimeSpan _firstReadWait = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _discoveryLock = new(1, 1);
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly object _refreshGate = new();
    private string? _endpoint;
    private DateTime _checkedAtUtc = DateTime.MinValue;
    private string? _lastError;
    private bool? _cliFound;

    // Loads and downloads keep the Foundry service busy: while one runs we don't poll it with more CLI commands.
    private int _activeOperations;
    private string? _activeOperation;
    private bool _legacyLoadUnavailable;

    // Last known model list, refreshed in the background so the status page never waits on a slow Foundry CLI.
    private Task? _refreshTask;
    private List<FoundryModel>? _lastModels;
    private DateTime _lastModelsAtUtc = DateTime.MinValue;
    private string? _lastRefreshError;

    public FoundryLocalService(IConfiguration config, EgressGuard guard, ILogger<FoundryLocalService> log)
    {
        _log = log;
        _configuredEndpoint = NormalizeEndpoint(config["FoundryLocal:Endpoint"]);
        _cli = string.IsNullOrWhiteSpace(config["FoundryLocal:Cli"]) ? "foundry" : config["FoundryLocal:Cli"]!;
        _loadTtlSeconds = int.TryParse(config["FoundryLocal:LoadTtlSeconds"], out var ttl) ? ttl : 3600;
        _inventoryTtl = TimeSpan.FromSeconds(int.TryParse(config["FoundryLocal:InventoryTtlSeconds"], out var inv) && inv >= 0 ? inv : 15);
        _cliListTimeout = TimeSpan.FromSeconds(int.TryParse(config["FoundryLocal:CliTimeoutSeconds"], out var cliTimeout) && cliTimeout > 0 ? cliTimeout : 90);
        _http = new HttpClient(new EgressGuardHandler(guard) { InnerHandler = new SocketsHttpHandler() })
        {
            Timeout = Timeout.InfiniteTimeSpan, // streaming answers can take a while; calls use their own timeouts
        };
    }

    internal bool IsBusy => Volatile.Read(ref _activeOperations) > 0;

    /// <summary>The background inventory refresh that is running right now, if any (used by tests).</summary>
    internal Task? PendingInventoryRefresh
    {
        get { lock (_refreshGate) return _refreshTask; }
    }

    private sealed class OperationScope(FoundryLocalService owner) : IDisposable
    {
        public void Dispose()
        {
            Interlocked.Decrement(ref owner._activeOperations);
            owner._lastModelsAtUtc = DateTime.MinValue; // the model list changed: refresh it next time
        }
    }

    private OperationScope BeginOperation(string name)
    {
        Interlocked.Increment(ref _activeOperations);
        _activeOperation = name;
        return new OperationScope(this);
    }

    public static string DeviceOf(string modelId)
    {
        var s = modelId.ToLowerInvariant();
        if (s.Contains("npu") || s.Contains("qnn") || s.Contains("vitis")) return "NPU";
        if (s.Contains("gpu") || s.Contains("cuda") || s.Contains("dml") || s.Contains("trt") || s.Contains("rtx")) return "GPU";
        if (s.Contains("cpu")) return "CPU";
        return "Auto";
    }

    // ---------------------------------------------------------------- discovery

    public async Task<string?> GetEndpointAsync(bool refresh, CancellationToken ct)
    {
        if (_configuredEndpoint is not null) return _configuredEndpoint;

        var age = DateTime.UtcNow - _checkedAtUtc;
        // While a model loads or downloads the CLI is slow; keep the endpoint we already know instead of asking again.
        if (!refresh && _endpoint is not null && (age < TimeSpan.FromMinutes(2) || IsBusy)) return _endpoint;
        if (!refresh && _endpoint is null && age < TimeSpan.FromSeconds(15)) return null; // don't hammer the CLI

        await _discoveryLock.WaitAsync(ct);
        try
        {
            _endpoint = await DiscoverAsync(ct);
            _checkedAtUtc = DateTime.UtcNow;
            return _endpoint;
        }
        finally
        {
            _discoveryLock.Release();
        }
    }

    private async Task<string?> DiscoverAsync(CancellationToken ct)
    {
        _lastError = null;
        // Current CLI: 'foundry server status'. Older 0.x CLI: 'foundry service status'.
        foreach (var args in new[] { "server status", "service status" })
        {
            try
            {
                var (_, output) = await ProcessRunner.RunAsync(_cli, args, TimeSpan.FromSeconds(25), ct);
                _cliFound = true;
                foreach (Match m in LocalUrl().Matches(output))
                {
                    if (!int.TryParse(m.Groups[1].Value, out var port) || port <= 0) continue;
                    var candidate = m.Value.TrimEnd('/');
                    if (await PingAsync(candidate, ct))
                    {
                        _log.LogInformation("Foundry Local endpoint: {Endpoint}", candidate);
                        return candidate;
                    }
                }
            }
            catch (Win32Exception)
            {
                _cliFound = false;
                _lastError = $"Foundry Local CLI ('{_cli}') was not found. Install it with: winget install Microsoft.FoundryLocal";
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _lastError = ex.Message;
            }
        }

        _lastError ??= "Foundry Local is not running. Start it with: foundry server start --port 39839 --idle-timeout 0";
        return null;
    }

    private async Task<bool> PingAsync(string baseUrl, CancellationToken ct)
    {
        foreach (var path in new[] { "/openai/status", "/v1/models" })
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(4));
                using var resp = await _http.GetAsync(baseUrl + path, cts.Token);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // try the next probe
            }
        }
        return false;
    }

    private static string? NormalizeEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().TrimEnd('/');
        if (v.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) v = v[..^3];
        return v;
    }

    // ---------------------------------------------------------------- status & models

    public async Task<object> GetStatusAsync(CancellationToken ct)
    {
        var endpoint = await GetEndpointAsync(refresh: false, ct);
        if (endpoint is null) return NotRunning();

        var inventory = await GetInventoryAsync(endpoint, ct);
        StartVersionLookup();
        return new
        {
            running = inventory.Error is null,
            endpoint,
            cliFound = _cliFound,
            version = _version,
            error = inventory.Error,
            warning = inventory.Warning,
            busy = inventory.Busy,
            operation = inventory.Operation,
            models = inventory.Models,
        };
    }

    private object NotRunning()
    {
        StartVersionLookup();
        return new { running = false, cliFound = _cliFound, version = _version, error = _lastError, models = Array.Empty<FoundryModel>() };
    }

    // ---------------------------------------------------------------- CLI version

    private string? _version;
    private Task? _versionTask;
    private DateTime _versionTriedAtUtc = DateTime.MinValue;

    /// <summary>The Foundry Local CLI version ("0.11.0"), read once in the background; null until known.</summary>
    internal string? Version => _version;

    internal Task? PendingVersionLookup => Volatile.Read(ref _versionTask);

    private void StartVersionLookup()
    {
        if (_version is not null || _cliFound == false) return;
        lock (_refreshGate)
        {
            if (_versionTask is { IsCompleted: false } || DateTime.UtcNow - _versionTriedAtUtc < TimeSpan.FromSeconds(30)) return;
            _versionTriedAtUtc = DateTime.UtcNow;
            _versionTask = Task.Run(async () =>
            {
                try
                {
                    var (_, output) = await ProcessRunner.RunAsync(_cli, "--version", TimeSpan.FromSeconds(20), CancellationToken.None);
                    _version = ParseVersion(output);
                }
                catch (Win32Exception)
                {
                    _cliFound = false;
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Could not read the Foundry Local version.");
                }
            });
        }
    }

    internal static string? ParseVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = Regex.Match(output, @"\d+\.\d+(\.\d+)?");
        return m.Success ? m.Value : null;
    }

    private sealed record Inventory(List<FoundryModel> Models, string? Error, string? Warning, bool Busy, string? Operation);

    /// <summary>
    /// The model list for the status page. It never waits on a slow Foundry CLI: while a model loads or downloads it
    /// returns the last known list, otherwise it returns the cached list at once and refreshes it in the background.
    /// Only the very first read waits (briefly) because there is nothing to show yet.
    /// </summary>
    private async Task<Inventory> GetInventoryAsync(string endpoint, CancellationToken ct)
    {
        var empty = new List<FoundryModel>();
        var models = _lastModels;

        if (IsBusy) return new Inventory(models ?? empty, null, null, true, _activeOperation);

        var fresh = models is not null && _lastRefreshError is null && DateTime.UtcNow - _lastModelsAtUtc < _inventoryTtl;
        if (fresh) return new Inventory(models!, null, null, false, null);

        var refresh = EnsureInventoryRefresh(endpoint);
        if (models is not null)
        {
            var warning = _lastRefreshError is null
                ? null
                : $"Could not refresh the model list ({Errors.Trim(_lastRefreshError, 200)}). Showing the last known list.";
            return new Inventory(models, null, warning, false, null);
        }

        await Task.WhenAny(refresh, Task.Delay(_firstReadWait, ct));
        if (_lastModels is not null) return new Inventory(_lastModels, null, null, false, null);
        if (refresh.IsCompleted) return new Inventory(empty, _lastRefreshError ?? "Could not read the Foundry Local model list.", null, false, null);
        return new Inventory(empty, null, "Foundry Local is still reading its model list; it will appear shortly.", true, "Reading the model list");
    }

    private Task EnsureInventoryRefresh(string endpoint)
    {
        lock (_refreshGate)
        {
            return _refreshTask ??= Task.Run(() => RefreshInventoryAsync(endpoint));
        }
    }

    private async Task RefreshInventoryAsync(string endpoint)
    {
        try
        {
            var (models, error) = await ListModelsAsync(endpoint, CancellationToken.None);
            if (error is not null && _configuredEndpoint is null && !IsBusy)
            {
                // The dynamic port may have changed (service restarted) - rediscover once. If it's gone, the next status call reports "not running".
                var rediscovered = await GetEndpointAsync(refresh: true, CancellationToken.None);
                if (rediscovered is not null) (models, error) = await ListModelsAsync(rediscovered, CancellationToken.None);
            }

            if (error is null)
            {
                _lastModels = models;
                _lastModelsAtUtc = DateTime.UtcNow;
                _lastRefreshError = null;
            }
            else
            {
                _lastRefreshError = error;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Foundry Local model list refresh failed.");
            _lastRefreshError = ex.Message;
        }
        finally
        {
            lock (_refreshGate) _refreshTask = null;
        }
    }

    private async Task<(List<FoundryModel> Models, string? Error)> ListModelsAsync(string endpoint, CancellationToken ct)
    {
        var loaded = await TryGetModelIdsAsync(endpoint + "/openai/loadedmodels", ct);
        var cached = await TryGetModelIdsAsync(endpoint + "/openai/models", ct);
        var v1 = await TryGetModelIdsAsync(endpoint + "/v1/models", ct);
        if (loaded is null && cached is null && v1 is null)
        {
            return (new List<FoundryModel>(), $"Foundry Local did not answer at {endpoint}.");
        }

        // Foundry Local 0.13 exposes inference over HTTP but manages model lifecycle through the CLI.
        // One command at a time: the service is slow enough already when it is busy.
        if (loaded is null || cached is null)
        {
            try
            {
                loaded ??= await GetCliModelIdsAsync("--loaded", ct);
                cached ??= await GetCliModelIdsAsync("--cached", ct);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or JsonException or TimeoutException)
            {
                _log.LogWarning(ex, "Foundry Local model inventory failed.");
                return (new List<FoundryModel>(), $"Could not read Foundry Local model state: {ex.Message}");
            }
        }

        var loadedSet = new HashSet<string>(loaded ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var cachedSet = new HashSet<string>(cached ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var all = new List<string>();
        foreach (var id in (loaded ?? Array.Empty<string>()).Concat(cached ?? Array.Empty<string>()))
        {
            if (!all.Contains(id, StringComparer.OrdinalIgnoreCase)) all.Add(id);
        }

        var models = all
            .Select(id => new FoundryModel(id, DeviceOf(id), loadedSet.Contains(id), cachedSet.Contains(id)))
            .OrderByDescending(m => m.Loaded)
            .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (models, null);
    }

    private async Task<string[]?> TryGetModelIdsAsync(string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            using var resp = await _http.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            return ParseModelIds(await resp.Content.ReadAsStringAsync(cts.Token));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<string[]> GetCliModelIdsAsync(string filter, CancellationToken ct)
    {
        var output = await RunCliAsync($"model list {filter} --variants --output json", _cliListTimeout, ct);
        return ParseModelIds(output)
            ?? throw new InvalidOperationException("Foundry Local model list returned an unrecognized JSON payload.");
    }

    /// <summary>Accepts string arrays, OpenAI data/models arrays, or CLI variants arrays.</summary>
    internal static string[]? ParseModelIds(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var arr = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("data", out var data)) arr = data;
            else if (root.TryGetProperty("models", out var models)) arr = models;
            else if (root.TryGetProperty("variants", out var variants)) arr = variants;
            else return null;
        }
        if (arr.ValueKind != JsonValueKind.Array) return null;

        var ids = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                ids.Add(item.GetString()!);
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) ids.Add(id.GetString()!);
                else if (item.TryGetProperty("variantId", out var variantId) && variantId.ValueKind == JsonValueKind.String) ids.Add(variantId.GetString()!);
                else if (item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) ids.Add(name.GetString()!);
            }
        }
        return ids.ToArray();
    }

    // ---------------------------------------------------------------- actions

    public async Task<object> GetCatalogAsync(CancellationToken ct)
    {
        var output = await RunCliAsync("model list --variants --output json", TimeSpan.FromSeconds(60), ct);
        using var doc = JsonDocument.Parse(output);
        if (!doc.RootElement.TryGetProperty("variants", out var variants) || variants.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Foundry Local catalog returned an unrecognized JSON payload. Update the Foundry Local CLI.");
        }
        var loaded = new HashSet<string>(await GetCliModelIdsAsync("--loaded", ct), StringComparer.OrdinalIgnoreCase);
        var models = new List<CatalogModel>();
        foreach (var item in variants.EnumerateArray())
        {
            var id = item.GetProperty("variantId").GetString();
            if (id is null || !SafeModelId().IsMatch(id)) throw new InvalidOperationException("Foundry Local catalog returned an invalid variant ID.");
            models.Add(new CatalogModel(
                id,
                item.TryGetProperty("alias", out var alias) ? alias.GetString() ?? id : id,
                DeviceOf(id),
                item.TryGetProperty("type", out var task) ? task.GetString() ?? "Unknown" : "Unknown",
                item.TryGetProperty("fileSizeMb", out var size) && size.TryGetDouble(out var mb) ? mb : null,
                item.TryGetProperty("license", out var license) ? license.GetString() ?? "" : "",
                item.TryGetProperty("cached", out var cached) && cached.GetBoolean(),
                loaded.Contains(id)));
        }
        return new { models };
    }

    public async Task<object> DownloadAsync(string model, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model) || !SafeModelId().IsMatch(model)) throw new ArgumentException("Invalid model id.");
        using var operation = BeginOperation($"Downloading {model}");
        await _downloadLock.WaitAsync(ct);
        try
        {
            var sw = Stopwatch.StartNew();
            _log.LogInformation("[Foundry Local] Downloading {Model} to the local cache.", model);
            await RunCliAsync($"model download {model} --output json", TimeSpan.FromHours(1), ct);
            var cached = await GetCliModelIdsAsync("--cached", ct);
            if (!cached.Contains(model, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Foundry Local did not report '{model}' as cached after the download command.");
            }
            return new { model, cached = true, ms = sw.ElapsedMilliseconds };
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    private async Task<string> RunCliAsync(string arguments, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var (exitCode, output) = await ProcessRunner.RunAsync(_cli, arguments, timeout, ct);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"Foundry Local {arguments.Split(' ')[1]} failed (CLI exit {exitCode}): {Errors.Trim(output)}{OldCliHint(output)}");
            }
            return output;
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Foundry Local CLI ('{_cli}') was not found. Install or update Foundry Local, or set FoundryLocal:Cli.", ex);
        }
    }

    /// <summary>The catalog, cache and load commands need Foundry Local 0.13+; an older CLI answers with an unknown command/option error.</summary>
    internal static string OldCliHint(string output) =>
        Regex.IsMatch(output, @"Unknown (command|option)|Unrecognized|unexpected argument|Required command|invalid (command|option)", RegexOptions.IgnoreCase)
            ? " Hint: this Foundry Local CLI looks older than the app expects (0.13 or newer is needed for the catalog, cache and load commands). Update it: winget upgrade Microsoft.FoundryLocal."
            : "";

    public async Task<object> StartAsync(int? port, CancellationToken ct)
    {
        var portArgs = port is > 0 ? $" --port {port} --idle-timeout 0" : "";
        try
        {
            var exit = await ProcessRunner.RunDetachedAsync(_cli, "server start" + portArgs, TimeSpan.FromSeconds(90), ct);
            if (exit is not null and not 0)
            {
                await ProcessRunner.RunDetachedAsync(_cli, "service start", TimeSpan.FromSeconds(90), ct); // 0.x CLI
            }
        }
        catch (Win32Exception)
        {
            return new { started = false, error = $"Foundry Local CLI ('{_cli}') was not found. Install it with: winget install Microsoft.FoundryLocal" };
        }

        var endpoint = await GetEndpointAsync(refresh: true, ct);
        return new { started = endpoint is not null, endpoint, error = endpoint is null ? _lastError : null };
    }

    public async Task<object> LoadAsync(string model, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model) || !SafeModelId().IsMatch(model)) throw new ArgumentException("Invalid model id.");

        // Foundry loads one model at a time: parallel loads (two Arena lanes, two Load clicks) queue up here instead of
        // fighting over the service and the machine's memory.
        using var operation = BeginOperation($"Loading {model}");
        await _loadLock.WaitAsync(ct);
        _activeOperation = $"Loading {model}"; // name the load that is actually running, not one still waiting its turn
        try
        {
            return await LoadCoreAsync(model, ct);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<object> LoadCoreAsync(string model, CancellationToken ct)
    {
        var endpoint = await GetEndpointAsync(refresh: false, ct)
            ?? throw new InvalidOperationException(_lastError ?? "Foundry Local is not running.");

        var sw = Stopwatch.StartNew();
        if (!_legacyLoadUnavailable)
        {
            using var resp = await _http.GetAsync($"{endpoint}/openai/load/{model}?ttl={_loadTtlSeconds}", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                _log.LogInformation("[Foundry Local] loaded {Model} in {Ms} ms", model, sw.ElapsedMilliseconds);
                return new { model, loaded = true, ms = sw.ElapsedMilliseconds };
            }
            if (resp.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed))
            {
                throw new InvalidOperationException(
                    $"Foundry Local could not load '{model}' ({(int)resp.StatusCode}): {Errors.Trim(body, 1500)}{ExecutionProviderHint(model, body)}");
            }
            _legacyLoadUnavailable = true; // Foundry Local 0.13+: loading is a CLI operation, don't ask the HTTP API again
        }

        await LoadWithCliAsync(model, ct);
        _log.LogInformation("[Foundry Local] loaded {Model} in {Ms} ms", model, sw.ElapsedMilliseconds);
        return new { model, loaded = true, ms = sw.ElapsedMilliseconds };
    }

    private async Task LoadWithCliAsync(string model, CancellationToken ct)
    {
        _log.LogInformation("[Foundry Local] Loading {Model} with the Foundry CLI.", model);
        try
        {
            var (exitCode, output) = await ProcessRunner.RunAsync(
                _cli, $"model load {model} --output json", TimeSpan.FromMinutes(10), ct);
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Foundry Local could not load '{model}' (CLI exit {exitCode}): {Errors.Trim(output, 1500)}{ExecutionProviderHint(model, output)}{OldCliHint(output)}");
            }

            // The load command already succeeded. Double-checking is best effort: a busy service can be too slow
            // to list its models, and that must not turn a successful load into an error.
            string[]? loaded = null;
            try
            {
                loaded = await GetCliModelIdsAsync("--loaded", ct);
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or JsonException)
            {
                _log.LogWarning("[Foundry Local] {Model} loaded, but confirming it with the CLI failed: {Message}", model, ex.Message);
            }
            if (loaded is not null && !loaded.Contains(model, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Foundry Local did not report '{model}' as loaded after the load command.");
            }
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"This Foundry Local version requires the CLI to load models. Ensure '{_cli}' is installed and available on PATH.", ex);
        }
    }

    /// <summary>Explains the common "model downloaded but won't load" case: a hardware variant whose execution provider (EP) is missing or incompatible.</summary>
    internal static string ExecutionProviderHint(string model, string failureOutput)
    {
        var ep = model.Contains("openvino", StringComparison.OrdinalIgnoreCase) ? "OpenVINO"
            : model.Contains("qnn", StringComparison.OrdinalIgnoreCase) ? "Qualcomm QNN"
            : model.Contains("vitis", StringComparison.OrdinalIgnoreCase) ? "AMD VitisAI"
            : model.Contains("trt", StringComparison.OrdinalIgnoreCase) || model.Contains("tensorrt", StringComparison.OrdinalIgnoreCase) ? "NVIDIA TensorRT-RTX"
            : null;
        var epError = Regex.IsMatch(failureOutput, @"PluginExecutionProviderFactory|CreateProvider|execution provider", RegexOptions.IgnoreCase);
        if (ep is null && !epError) return "";

        // The provider is installed but built against a newer ONNX Runtime than the one inside Foundry Local.
        if (Regex.IsMatch(failureOutput, @"NOT_IMPLEMENTED|not available in this build|ReleaseEpContextConfig", RegexOptions.IgnoreCase))
        {
            return $" Hint: the {ep ?? "hardware"} execution provider is installed but does not match the ONNX Runtime inside this Foundry Local " +
                   "(known upstream issue: microsoft/foundry-local #1237, #1241, #1261; it affects Foundry Local 0.12/0.13). It is not caused by this app or by offline mode. " +
                   "Fixes: install Foundry Local 0.11.0 (reported to work: winget install --id Microsoft.FoundryLocal --version 0.11.0.0 --exact), " +
                   "run the model with the app's OpenVINO sideload (Readiness > OpenVINO sideload), or load a 'generic-cpu' variant now.";
        }

        return $" Hint: this variant needs the {ep ?? "matching hardware"} execution provider, which is missing or not registered on this PC. " +
               "Execution providers are downloaded once while online (Readiness > Windows ML > Download & register EPs). " +
               "Offline, load a 'generic-cpu' variant instead; it needs no extra provider. Run 'foundry server logs' for details.";
    }

    private async Task<HttpResponseMessage> SendChatAsync(string endpoint, Dictionary<string, object?> payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/v1/chat/completions") { Content = JsonContent.Create(payload) };
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static bool ModelNotLoaded(string body) =>
        Regex.IsMatch(body, @"must be loaded|not loaded", RegexOptions.IgnoreCase);

    /// <summary>Streams an OpenAI-compatible chat completion as start / delta / done events.</summary>
    public async IAsyncEnumerable<object> StreamChatAsync(ChatRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        if (req.Messages is null || req.Messages.Count == 0) throw new ArgumentException("messages are required.");
        if (string.IsNullOrWhiteSpace(req.Model) || !SafeModelId().IsMatch(req.Model)) throw new ArgumentException("Pick a Foundry Local model first.");
        var endpoint = await GetEndpointAsync(refresh: false, ct)
            ?? throw new InvalidOperationException(_lastError ?? "Foundry Local is not running.");

        var payload = new Dictionary<string, object?>
        {
            ["model"] = req.Model,
            ["messages"] = req.Messages.Select(m => new Dictionary<string, string> { ["role"] = m.Role, ["content"] = m.Content }).ToList(),
            ["stream"] = true,
            ["temperature"] = req.Temperature ?? 0.7f,
            ["max_tokens"] = req.MaxTokens ?? 768,
        };

        var sw = Stopwatch.StartNew();
        var first = await SendChatAsync(endpoint, payload, ct);
        long loadMs = 0;
        if (first.StatusCode == HttpStatusCode.BadRequest && ModelNotLoaded(await first.Content.ReadAsStringAsync(ct)))
        {
            // Downloaded but not in memory: load it once, then retry. The load time is reported separately so it doesn't distort first-token timings.
            first.Dispose();
            var load = Stopwatch.StartNew();
            await LoadAsync(req.Model, ct);
            loadMs = load.ElapsedMilliseconds;
            sw.Restart();
            first = await SendChatAsync(endpoint, payload, ct);
        }
        using var resp = first;
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Foundry Local returned {(int)resp.StatusCode}: {Errors.Trim(err)}");
        }

        yield return new { type = "start", engine = "Foundry Local", model = req.Model, device = DeviceOf(req.Model), loadMs = loadMs > 0 ? loadMs : (long?)null };

        var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            // The server answered without streaming: send the whole answer as one chunk.
            var (text, finish, usage) = ParseChunk(await resp.Content.ReadAsStringAsync(ct));
            if (!string.IsNullOrEmpty(text)) yield return new { type = "delta", text };
            yield return new { type = "done", status = "Complete", finishReason = finish, usage, serverMs = sw.ElapsedMilliseconds };
            yield break;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        object? lastUsage = null;
        string? lastFinish = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            if (data == "[DONE]") break;

            var (delta, finish, usage) = ParseChunk(data);
            if (usage is not null) lastUsage = usage;
            if (finish is not null) lastFinish = finish;
            if (!string.IsNullOrEmpty(delta)) yield return new { type = "delta", text = delta };
        }

        _log.LogInformation("[Foundry Local] {Model} answered in {Ms} ms", req.Model, sw.ElapsedMilliseconds);
        yield return new { type = "done", status = "Complete", finishReason = lastFinish, usage = lastUsage, serverMs = sw.ElapsedMilliseconds };
    }

    private static (string? Delta, string? Finish, object? Usage) ParseChunk(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? delta = null, finish = null;
            object? usage = null;

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var c0 = choices[0];
                if (c0.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.Object &&
                    d.TryGetProperty("content", out var dc) && dc.ValueKind == JsonValueKind.String)
                {
                    delta = dc.GetString();
                }
                else if (c0.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object &&
                         m.TryGetProperty("content", out var mc) && mc.ValueKind == JsonValueKind.String)
                {
                    delta = mc.GetString();
                }
                else if (c0.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    delta = t.GetString();
                }

                if (c0.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String) finish = f.GetString();
            }

            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object) usage = u.Clone();
            return (delta, finish, usage);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }
}
