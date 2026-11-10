using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using LocalAIShowroom.Bridge.Infrastructure;

namespace LocalAIShowroom.Bridge.Services;

/// <summary>
/// Optional "OpenVINO sideload" lane. Foundry Local 0.12/0.13 cannot load its OpenVINO NPU/GPU model variants (its
/// OpenVINO provider doesn't match its ONNX Runtime) and has no setting for a different provider. This service runs
/// the very same cached model folders outside Foundry Local, in a small Python worker that uses a newer OpenVINO
/// provider (onnxruntime-ep-openvino) with onnxruntime-genai. Set it up once with scripts\setup-openvino-sideload.ps1.
/// </summary>
public sealed partial class OpenVinoSideloadService : IDisposable
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:\-]{0,199}$")]
    private static partial Regex SafeModelId();

    [GeneratedRegex(@"-(\d+)$")]
    private static partial Regex TrailingVersion();

    [GeneratedRegex(@"^v\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionFolder();

    [GeneratedRegex(@"[A-Za-z]:\\[^\r\n]+")]
    private static partial Regex WindowsPath();

    public sealed record SideloadModel(string Id, string Device, string Path);

    public const string SetupScript = @"scripts\setup-openvino-sideload.ps1";

    private readonly ILogger<OpenVinoSideloadService> _log;
    private readonly string _root;
    private readonly string[] _configuredModelRoots;
    private readonly bool _includeGeneric;
    private readonly string _foundryCli;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _scanLock = new();

    private Worker? _worker;
    private int _activeOperations;
    private string? _activeOperation;
    private int _requestId;
    private string? _lastError;
    private Dictionary<string, string>? _versions;

    private List<SideloadModel>? _models;
    private DateTime _modelsAtUtc = DateTime.MinValue;
    private Task? _cacheDiscovery;
    private string? _foundryCacheRoot;

    public OpenVinoSideloadService(IConfiguration config, ILogger<OpenVinoSideloadService> log)
    {
        _log = log;
        _foundryCli = string.IsNullOrWhiteSpace(config["FoundryLocal:Cli"]) ? "foundry" : config["FoundryLocal:Cli"]!;
        _includeGeneric = string.Equals(config["OpenVinoSideload:IncludeGenericModels"], "true", StringComparison.OrdinalIgnoreCase);
        _configuredModelRoots = (config["OpenVinoSideload:ModelRoots"] ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Environment.ExpandEnvironmentVariables).ToArray();

        // The installation folder: an explicitly configured one is authoritative; otherwise next to the app (offline
        // bundle) or in the user's profile (the setup script's default).
        var configuredRoot = config["OpenVinoSideload:Root"];
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            _root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredRoot));
        }
        else
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "..", "openvino-sideload"),
                Path.Combine(AppContext.BaseDirectory, "openvino-sideload"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LocalAIShowroom", "openvino-sideload"),
            }.Select(Path.GetFullPath).ToArray();
            _root = candidates.FirstOrDefault(p => File.Exists(Path.Combine(p, "python", "python.exe")))
                ?? candidates.Last(); // none installed: report the default location in messages
        }
    }

    private string PythonExe => Path.Combine(_root, "python", "python.exe");
    private string SitePackages => Path.Combine(_root, "site-packages");
    private string WorkerScript => Path.Combine(_root, "worker.py");

    private bool IsInstalled =>
        File.Exists(PythonExe) && File.Exists(WorkerScript) &&
        Directory.Exists(Path.Combine(SitePackages, "onnxruntime_genai")) &&
        Directory.Exists(Path.Combine(SitePackages, "onnxruntime_ep_openvino"));

    internal bool IsBusy => Volatile.Read(ref _activeOperations) > 0;

    // ---------------------------------------------------------------- status & models

    public object GetStatus()
    {
        EnsureCacheDiscovery();
        var worker = _worker is { IsAlive: true } w ? w : null;
        var models = GetModels();
        return new
        {
            installed = IsInstalled,
            root = _root,
            pythonFound = File.Exists(PythonExe),
            packagesFound = Directory.Exists(Path.Combine(SitePackages, "onnxruntime_genai")) && Directory.Exists(Path.Combine(SitePackages, "onnxruntime_ep_openvino")),
            workerFound = File.Exists(WorkerScript),
            setup = SetupScript,
            versions = _versions,
            modelRoots = ModelRoots().ToArray(),
            loaded = worker?.ModelId,
            busy = IsBusy,
            operation = IsBusy ? _activeOperation : null,
            error = _lastError,
            models = models.Select(m => new { id = m.Id, device = m.Device, loaded = worker?.ModelId == m.Id }).ToArray(),
        };
    }

    private IEnumerable<string> ModelRoots()
    {
        var roots = new List<string>(_configuredModelRoots);
        if (_foundryCacheRoot is not null) roots.Add(_foundryCacheRoot);
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".foundry", "cache", "models"));
        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Asks the Foundry CLI where its cache is, once, in the background (it may be slow and must never block the status page).</summary>
    private void EnsureCacheDiscovery()
    {
        if (_cacheDiscovery is not null) return;
        _cacheDiscovery = Task.Run(async () =>
        {
            try
            {
                var (exitCode, output) = await ProcessRunner.RunAsync(_foundryCli, "cache location", TimeSpan.FromSeconds(30), CancellationToken.None);
                var match = WindowsPath().Match(output);
                if (exitCode == 0 && match.Success && Directory.Exists(match.Value.Trim()))
                {
                    _foundryCacheRoot = match.Value.Trim();
                    lock (_scanLock) _modelsAtUtc = DateTime.MinValue;
                }
            }
            catch (Exception ex) when (ex is Win32Exception or TimeoutException or InvalidOperationException)
            {
                _log.LogDebug("[OpenVINO sideload] Could not ask Foundry for its cache folder: {Message}", ex.Message);
            }
        });
    }

    private List<SideloadModel> GetModels()
    {
        lock (_scanLock)
        {
            if (_models is not null && DateTime.UtcNow - _modelsAtUtc < TimeSpan.FromSeconds(20)) return _models;
            _models = ScanModels();
            _modelsAtUtc = DateTime.UtcNow;
            return _models;
        }
    }

    private List<SideloadModel> ScanModels()
    {
        var found = new Dictionary<string, SideloadModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in ModelRoots().Where(Directory.Exists))
        {
            foreach (var dir in FindModelDirectories(root, 4))
            {
                var model = DescribeModel(dir);
                if (model is not null) found.TryAdd(model.Id, model);
            }
        }
        return found.Values.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> FindModelDirectories(string directory, int depth)
    {
        if (File.Exists(Path.Combine(directory, "genai_config.json")))
        {
            yield return directory;
            yield break;
        }
        if (depth == 0) yield break;
        IEnumerable<string> children;
        try { children = Directory.EnumerateDirectories(directory).ToList(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { yield break; }
        foreach (var child in children)
        {
            foreach (var hit in FindModelDirectories(child, depth - 1)) yield return hit;
        }
    }

    /// <summary>Foundry cache folders look like ...\Microsoft\qwen2.5-0.5b-instruct-openvino-npu-5\v5 and belong to model id qwen2.5-0.5b-instruct-openvino-npu:5.</summary>
    private SideloadModel? DescribeModel(string directory)
    {
        var name = new DirectoryInfo(directory).Name;
        if (VersionFolder().IsMatch(name)) name = new DirectoryInfo(directory).Parent?.Name ?? name;
        var id = TrailingVersion().Replace(name, ":$1");
        if (!SafeModelId().IsMatch(id)) return null;

        var lower = id.ToLowerInvariant();
        var isOpenVino = lower.Contains("openvino");
        if (!isOpenVino && !_includeGeneric) return null;

        var device = !isOpenVino ? "default"
            : lower.Contains("-npu") ? "NPU"
            : lower.Contains("-gpu") ? "GPU"
            : "CPU";
        return new SideloadModel(id, device, directory);
    }

    private SideloadModel Require(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId) || !SafeModelId().IsMatch(modelId)) throw new ArgumentException("Pick an OpenVINO sideload model first.");
        SideloadModel? Find() => GetModels().FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        var model = Find();
        if (model is null)
        {
            lock (_scanLock) _modelsAtUtc = DateTime.MinValue; // it may have been downloaded a moment ago
            model = Find();
        }
        return model ?? throw new ArgumentException($"'{modelId}' is not a downloaded OpenVINO model on this PC. Download it with: foundry model download <model>");
    }

    // ---------------------------------------------------------------- operations

    private sealed class OperationScope(OpenVinoSideloadService owner) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref owner._activeOperations);
    }

    private OperationScope BeginOperation(string name)
    {
        Interlocked.Increment(ref _activeOperations);
        _activeOperation = name;
        return new OperationScope(this);
    }

    public async Task<object> LoadAsync(string modelId, CancellationToken ct)
    {
        var model = Require(modelId);
        using var operation = BeginOperation($"Loading {model.Id} with the OpenVINO sideload");
        await _gate.WaitAsync(ct);
        try
        {
            var ms = await EnsureLoadedAsync(model, ct);
            return new { model = model.Id, loaded = true, ms };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<object> UnloadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var was = _worker?.ModelId;
            StopWorker();
            return new { unloaded = was };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Makes sure the worker has this model loaded and returns the load time in ms (0 when it already was). Call with the gate held.</summary>
    private async Task<long> EnsureLoadedAsync(SideloadModel model, CancellationToken ct)
    {
        if (_worker is { IsAlive: true } current && current.ModelId == model.Id) return 0;
        StopWorker();

        if (!IsInstalled)
        {
            throw new InvalidOperationException(
                $"The OpenVINO sideload is not installed. Run {SetupScript} once (needs internet), or copy its folder to {_root}.");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var worker = Worker.Start(PythonExe, WorkerScript, SitePackages, _log);
        _worker = worker;
        try
        {
            var ready = await ReadUntilAsync(worker, ["ready"], TimeSpan.FromMinutes(2), ct);
            _versions = ReadVersions(ready);

            worker.Send(new { cmd = "load", model_dir = model.Path, device = model.Device });
            var loaded = await ReadUntilAsync(worker, ["loaded"], TimeSpan.FromMinutes(20), ct);
            worker.ModelId = model.Id;
            _lastError = null;
            _log.LogInformation("[OpenVINO sideload] {Model} loaded on {Device} in {Ms} ms", model.Id, model.Device, sw.ElapsedMilliseconds);
            return Math.Max(1, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastError = ex.Message;
            StopWorker();
            throw;
        }
        catch
        {
            StopWorker(); // a cancelled load can't be resumed
            throw;
        }
    }

    public async IAsyncEnumerable<object> StreamChatAsync(ChatRequest req, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (req.Messages is null || req.Messages.Count == 0) throw new ArgumentException("messages are required.");
        var model = Require(req.Model);

        using var operation = BeginOperation($"Generating with {model.Id} (OpenVINO sideload)");
        await _gate.WaitAsync(ct);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var loadMs = await EnsureLoadedAsync(model, ct);
            var worker = _worker!;
            yield return new { type = "start", engine = "OpenVINO (sideload)", model = model.Id, device = model.Device, loadMs = loadMs > 0 ? loadMs : (long?)null };

            sw.Restart();
            var requestId = Interlocked.Increment(ref _requestId);
            worker.Send(new
            {
                cmd = "generate",
                id = requestId,
                messages = req.Messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
                max_tokens = req.MaxTokens ?? 768,
                temperature = req.Temperature ?? 0.7f,
            });

            var finished = false;
            try
            {
                while (true)
                {
                    var evt = await worker.Events.Reader.ReadAsync(ct);
                    var type = TypeOf(evt);
                    if (type == "delta")
                    {
                        yield return new { type = "delta", text = evt.GetProperty("text").GetString() ?? "" };
                    }
                    else if (type == "done")
                    {
                        finished = true;
                        yield return new
                        {
                            type = "done",
                            status = StringOf(evt, "status") ?? "Complete",
                            finishReason = StringOf(evt, "finish_reason"),
                            usage = new { completionTokens = IntOf(evt, "completion_tokens"), promptTokens = IntOf(evt, "prompt_tokens") },
                            serverMs = sw.ElapsedMilliseconds,
                        };
                        yield break;
                    }
                    else if (type == "error")
                    {
                        finished = true; // the worker answered the request with an error and is back in sync
                        throw new InvalidOperationException($"OpenVINO sideload: {StringOf(evt, "message") ?? "unknown error"}");
                    }
                    else if (type == "exit")
                    {
                        finished = true;
                        throw new InvalidOperationException(ExitMessage(worker, evt));
                    }
                }
            }
            finally
            {
                // The client went away mid-answer: stop the generation and wait for the worker to be ready again so the
                // loaded model stays usable. If it doesn't answer, it is replaced on the next request.
                if (!finished) await StopGenerationAsync(worker);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopGenerationAsync(Worker worker)
    {
        try
        {
            worker.Send(new { cmd = "cancel" });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                var type = TypeOf(await worker.Events.Reader.ReadAsync(timeout.Token));
                if (type is "done" or "error" or "exit") return;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException or IOException or ObjectDisposedException)
        {
            if (ReferenceEquals(_worker, worker)) StopWorker();
        }
    }

    // ---------------------------------------------------------------- worker plumbing

    private static string? TypeOf(JsonElement evt) => StringOf(evt, "type");

    private static string? StringOf(JsonElement evt, string name) =>
        evt.ValueKind == JsonValueKind.Object && evt.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? IntOf(JsonElement evt, string name) =>
        evt.ValueKind == JsonValueKind.Object && evt.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static Dictionary<string, string>? ReadVersions(JsonElement ready)
    {
        if (!ready.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Object) return null;
        return versions.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString());
    }

    private static string ExitMessage(Worker worker, JsonElement evt)
    {
        var code = evt.TryGetProperty("code", out var c) ? c.ToString() : "?";
        var tail = worker.StderrTail();
        return $"The OpenVINO worker stopped unexpectedly (exit code {code}).{(tail.Length > 0 ? " " + tail : "")}";
    }

    private async Task<JsonElement> ReadUntilAsync(Worker worker, string[] types, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var evt = await worker.Events.Reader.ReadAsync(cts.Token);
                var type = TypeOf(evt);
                if (type is not null && types.Contains(type)) return evt;
                if (type == "error")
                {
                    throw new InvalidOperationException(
                        $"OpenVINO sideload: {StringOf(evt, "message") ?? "unknown error"}{DeviceHint(StringOf(evt, "message"))}");
                }
                if (type == "exit") throw new InvalidOperationException(ExitMessage(worker, evt));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The OpenVINO worker did not answer within {timeout.TotalSeconds:0} s. {worker.StderrTail()}".Trim());
        }
        catch (ChannelClosedException)
        {
            throw new InvalidOperationException($"The OpenVINO worker closed unexpectedly. {worker.StderrTail()}".Trim());
        }
    }

    internal static string DeviceHint(string? message) =>
        message is not null && Regex.IsMatch(message, @"device|NPU|GPU|plugin|compile", RegexOptions.IgnoreCase)
            ? " Hint: the OpenVINO NPU/GPU devices need an Intel NPU/GPU with current drivers. Run " + SetupScript + " again to see which devices the provider can use."
            : "";

    private void StopWorker()
    {
        var worker = Interlocked.Exchange(ref _worker, null);
        worker?.Dispose();
    }

    public void Dispose() => StopWorker();

    /// <summary>One Python worker process and its line-JSON protocol.</summary>
    private sealed class Worker : IDisposable
    {
        private readonly System.Diagnostics.Process _process;
        private readonly object _sendLock = new();
        private readonly Queue<string> _stderr = new();

        public Channel<JsonElement> Events { get; } = Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions { SingleReader = true });
        public string? ModelId { get; set; }
        public bool IsAlive { get { try { return !_process.HasExited; } catch (InvalidOperationException) { return false; } } }

        private Worker(System.Diagnostics.Process process) => _process = process;

        public static Worker Start(string python, string script, string sitePackages, ILogger log)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(python)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(script)!,
            };
            foreach (var arg in new[] { "-u", "-X", "utf8", script }) psi.ArgumentList.Add(arg);
            psi.Environment["SHOWROOM_SITE_PACKAGES"] = sitePackages;
            psi.Environment["PYTHONNOUSERSITE"] = "1";
            psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";

            var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
            var worker = new Worker(process);
            process.OutputDataReceived += (_, e) => worker.OnStdout(e.Data);
            process.ErrorDataReceived += (_, e) => worker.OnStderr(e.Data);
            process.Start(); // Win32Exception when python.exe can't be started
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            log.LogInformation("[OpenVINO sideload] worker started (pid {Pid})", process.Id);

            _ = Task.Run(async () =>
            {
                try
                {
                    await process.WaitForExitAsync();
                    process.WaitForExit(); // returns after the redirected streams are drained
                    worker.Events.Writer.TryWrite(JsonDocument.Parse($"{{\"type\":\"exit\",\"code\":{process.ExitCode}}}").RootElement.Clone());
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { /* disposed */ }
                finally { worker.Events.Writer.TryComplete(); }
            });
            return worker;
        }

        private void OnStdout(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            try
            {
                using var doc = JsonDocument.Parse(line);
                Events.Writer.TryWrite(doc.RootElement.Clone());
            }
            catch (JsonException)
            {
                OnStderr(line); // not protocol output
            }
        }

        private void OnStderr(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (_stderr)
            {
                _stderr.Enqueue(line.Trim());
                while (_stderr.Count > 40) _stderr.Dequeue();
            }
        }

        public string StderrTail()
        {
            lock (_stderr) return Errors.Trim(string.Join(" | ", _stderr.TakeLast(6)), 700);
        }

        public void Send(object command)
        {
            lock (_sendLock)
            {
                _process.StandardInput.WriteLine(JsonSerializer.Serialize(command));
                _process.StandardInput.Flush();
            }
        }

        public void Dispose()
        {
            try
            {
                if (IsAlive)
                {
                    try { _process.StandardInput.Close(); } catch (IOException) { /* already closed */ }
                    if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* already gone */ }
            finally
            {
                Events.Writer.TryComplete();
                _process.Dispose();
            }
        }
    }
}
