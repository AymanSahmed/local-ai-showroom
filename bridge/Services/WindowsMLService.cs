using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;
// Microsoft.AspNetCore.Builder (global using) also has a SessionOptions type; the alias wins over both imports.
using SessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace LocalAIShowroom.Bridge.Services;

/// <summary>
/// Windows ML: the ONNX Runtime that ships with the Windows App SDK, plus the execution-provider (EP) catalog that
/// downloads vendor EPs (Qualcomm QNN, Intel OpenVINO, AMD VitisAI/MIGraphX, NVIDIA TensorRT-RTX) on demand.
/// The demo model is an image classifier (ResNet-50 built with winml-cli), run on NPU, GPU or CPU.
/// </summary>
public sealed class WindowsMLService : IDisposable
{
    private sealed record SessionEntry(InferenceSession Session, string Device, string Provider, double CreateMs, string ModelPath);

    private readonly ILogger<WindowsMLService> _log;
    private readonly string _configuredPath;
    private readonly float[] _mean;
    private readonly float[] _std;
    private readonly SemaphoreSlim _epLock = new(1, 1);
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private bool _registered;
    private string? _registrationError;
    private string[]? _labels;
    private string? _labelsFor;

    public WindowsMLService(IConfiguration config, ILogger<WindowsMLService> log)
    {
        _log = log;
        var configured = config["WindowsML:ModelPath"];
        if (string.IsNullOrWhiteSpace(configured)) configured = @"%USERPROFILE%\LocalAIShowroom\models\resnet50";
        _configuredPath = Environment.ExpandEnvironmentVariables(configured);
        _mean = config.GetSection("WindowsML:Mean").Get<float[]>() is { Length: 3 } m ? m : new[] { 0.485f, 0.456f, 0.406f };
        _std = config.GetSection("WindowsML:Std").Get<float[]>() is { Length: 3 } s ? s : new[] { 0.229f, 0.224f, 0.225f };
    }

    /// <summary>The configured path may be a .onnx file or a winml-cli output folder.</summary>
    public string? ResolveModelPath()
    {
        if (File.Exists(_configuredPath)) return _configuredPath;
        if (!Directory.Exists(_configuredPath)) return null;

        var top = Path.Combine(_configuredPath, "model.onnx");
        if (File.Exists(top)) return top;

        return Directory.EnumerateFiles(_configuredPath, "*.onnx", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.Name.Equals("model.onnx", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(f => f.LastWriteTimeUtc)
            .Select(f => f.FullName)
            .FirstOrDefault();
    }

    // ---------------------------------------------------------------- execution providers

    /// <summary>Registers EPs already on the device (download=false) or downloads + registers all compatible EPs.</summary>
    public async Task EnsureRegisteredAsync(bool download, CancellationToken ct)
    {
        if (_registered && !download) return;
        await _epLock.WaitAsync(ct);
        try
        {
            if (_registered && !download) return;
            var catalog = ExecutionProviderCatalog.GetDefault();
            if (download)
            {
                _log.LogInformation("[Windows ML] Downloading and registering certified execution providers...");
                await catalog.EnsureAndRegisterCertifiedAsync();
            }
            else
            {
                await catalog.RegisterCertifiedAsync();
            }
            _registered = true;
            _registrationError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _registrationError = ex.Message;
            _log.LogWarning("[Windows ML] EP registration failed: {Message}", ex.Message);
            if (download) throw;
        }
        finally
        {
            _epLock.Release();
        }
    }

    public async Task<object> PrepareExecutionProvidersAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await EnsureRegisteredAsync(download: true, ct);
        await ResetSessionsAsync(ct); // new EPs may change which device "auto", "npu" and "gpu" use
        return new { ms = sw.ElapsedMilliseconds, status = await GetStatusAsync(ct) };
    }

    /// <summary>Which hardware Windows ML can really use. A virtual/software GPU does not count (see <see cref="IsVirtualOrSoftwareGpu"/>).</summary>
    public async Task<(bool HasNpu, bool HasRealGpu, bool VirtualGpuOnly)> GetHardwareAsync(CancellationToken ct)
    {
        await EnsureRegisteredAsync(download: false, ct);
        var devices = OrtEnv.Instance().GetEpDevices().Select(d => d.HardwareDevice).ToArray();
        var hasRealGpu = devices.Any(d => d.Type == OrtHardwareDeviceType.GPU && !IsVirtualOrSoftwareGpu(d));
        return (devices.Any(d => d.Type == OrtHardwareDeviceType.NPU), hasRealGpu, !hasRealGpu && devices.Any(d => d.Type == OrtHardwareDeviceType.GPU));
    }

    public async Task<object> GetStatusAsync(CancellationToken ct)
    {
        await EnsureRegisteredAsync(download: false, ct);

        object[] providers;
        string? catalogError = null;
        try
        {
            var availableProviders = ExecutionProviderCatalog.GetDefault().FindAllProviders();
            // Some runtime/device combinations return null when no additional certified EPs apply.
            providers = availableProviders is null ? Array.Empty<object>() : availableProviders
                .Select(p => (object)new { name = p.Name, readyState = p.ReadyState.ToString(), vendor = Vendor(p.Name) }).ToArray();
        }
        catch (Exception ex)
        {
            providers = Array.Empty<object>();
            catalogError = Infrastructure.Errors.Describe(ex);
            _log.LogWarning(ex, "[Windows ML] Could not query the execution provider catalog.");
        }

        var allDevices = OrtEnv.Instance().GetEpDevices().ToArray();
        var devices = allDevices
            .Select(d => new
            {
                ep = d.EpName,
                type = d.HardwareDevice.Type.ToString(),
                vendor = Vendor(d.EpName),
                hardwareVendor = d.HardwareDevice.Vendor,
                vendorId = d.HardwareDevice.VendorId,
                deviceId = d.HardwareDevice.DeviceId,
                virtualGpu = IsVirtualOrSoftwareGpu(d.HardwareDevice),
            })
            .ToArray();
        var hasRealGpu = devices.Any(d => d.type == "GPU" && !d.virtualGpu);

        var model = ResolveModelPath();
        return new
        {
            providers,
            catalogError,
            devices,
            hasNpu = devices.Any(d => d.type == "NPU"),
            hasGpu = hasRealGpu,
            // The only GPU is a virtual/software adapter (Hyper-V video, Basic Render Driver): ONNX Runtime/DirectML crashes the process on it.
            virtualGpuOnly = !hasRealGpu && devices.Any(d => d.type == "GPU"),
            registrationError = _registrationError,
            model = new
            {
                configuredPath = _configuredPath,
                path = model,
                found = model is not null,
                sizeMb = model is null ? (double?)null : Math.Round(new FileInfo(model).Length / 1024d / 1024d, 1),
            },
            sessions = _sessions.Values.Select(s => new { s.Device, s.Provider, createMs = Math.Round(s.CreateMs) }).ToArray(),
        };
    }

    /// <summary>
    /// Microsoft's virtual and software adapters (Hyper-V video, Microsoft Basic Render Driver / WARP) report vendor id 0x1414.
    /// Creating an ONNX Runtime session on them aborts the whole process natively (exit code 0xC0000409), so they are never used.
    /// </summary>
    internal static bool IsVirtualOrSoftwareGpu(OrtHardwareDevice device) =>
        device.Type == OrtHardwareDeviceType.GPU &&
        (device.VendorId == 0x1414 || (device.Vendor ?? "").Contains("Microsoft", StringComparison.OrdinalIgnoreCase));

    private static string Vendor(string ep) => ep switch
    {
        _ when ep.Contains("QNN", StringComparison.OrdinalIgnoreCase) => "Qualcomm",
        _ when ep.Contains("OpenVINO", StringComparison.OrdinalIgnoreCase) => "Intel",
        _ when ep.Contains("VitisAI", StringComparison.OrdinalIgnoreCase) => "AMD",
        _ when ep.Contains("MIGraphX", StringComparison.OrdinalIgnoreCase) => "AMD",
        _ when ep.Contains("TensorRt", StringComparison.OrdinalIgnoreCase) || ep.Contains("Nv", StringComparison.Ordinal) => "NVIDIA",
        _ when ep.Contains("Dml", StringComparison.OrdinalIgnoreCase) || ep.Contains("DirectML", StringComparison.OrdinalIgnoreCase) => "DirectML (any GPU)",
        _ when ep.Contains("CPU", StringComparison.OrdinalIgnoreCase) => "CPU",
        _ => "",
    };

    // ---------------------------------------------------------------- sessions

    private SessionOptions CreateSessionOptions(string device, out string providerLabel)
    {
        var options = new SessionOptions();
        switch (device)
        {
            case "cpu":
                providerLabel = "CPUExecutionProvider";
                return options;

            case "auto":
                // Same idea as winml-cli '--device auto': NPU first, then GPU, then CPU.
                options.SetEpSelectionPolicy(ExecutionProviderDevicePolicy.PREFER_NPU);
                providerLabel = "Auto (PREFER_NPU policy)";
                return options;

            default:
                var wanted = device == "npu" ? OrtHardwareDeviceType.NPU : OrtHardwareDeviceType.GPU;
                var env = OrtEnv.Instance();
                var onType = env.GetEpDevices().Where(d => d.HardwareDevice.Type == wanted).ToList();
                var candidates = onType.Where(d => !IsVirtualOrSoftwareGpu(d.HardwareDevice)).ToList();
                if (candidates.Count == 0 && onType.Count > 0)
                {
                    options.Dispose();
                    throw new NotSupportedException(
                        "The only GPU on this PC is a virtual or software adapter (for example Hyper-V video or the Microsoft Basic Render Driver). " +
                        "Running AI models on it crashes ONNX Runtime, so the GPU option is disabled. Use CPU, or a PC with a real GPU.");
                }
                if (candidates.Count == 0)
                {
                    options.Dispose();
                    throw new NotSupportedException(
                        $"No {device.ToUpperInvariant()} execution provider is registered on this PC. " +
                        "Use 'Download & register EPs' on the Readiness tab, or pick another device.");
                }

                // Prefer a vendor EP over DirectML for GPU (DirectML is the universal fallback).
                var chosen = candidates
                    .OrderBy(d => d.EpName.Contains("Dml", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .First();
                options.AppendExecutionProvider(env, new[] { chosen }, new Dictionary<string, string>());
                providerLabel = chosen.EpName;
                return options;
        }
    }

    private async Task<SessionEntry> GetSessionAsync(string device, CancellationToken ct)
    {
        var modelPath = ResolveModelPath() ?? throw new FileNotFoundException(
            $"No ONNX model found at '{_configuredPath}'. Build one with: winml build -m microsoft/resnet-50 -o \"{_configuredPath}\"  (or run scripts\\prepare-models.ps1)");

        await _sessionLock.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(device, out var existing) && existing.ModelPath == modelPath) return existing;

            var sw = Stopwatch.StartNew();
            using var options = CreateSessionOptions(device, out var provider);
            // First NPU session can take a while: the EP compiles the graph (cached afterwards).
            var session = await Task.Run(() => new InferenceSession(modelPath, options), ct);
            sw.Stop();

            existing?.Session.Dispose();
            var entry = new SessionEntry(session, device, provider, sw.Elapsed.TotalMilliseconds, modelPath);
            _sessions[device] = entry;
            _log.LogInformation("[Windows ML] session for {Device} via {Provider} created in {Ms:0} ms", device, provider, entry.CreateMs);
            return entry;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task ResetSessionsAsync(CancellationToken ct)
    {
        await _sessionLock.WaitAsync(ct);
        try
        {
            foreach (var s in _sessions.Values) s.Session.Dispose();
            _sessions.Clear();
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    // ---------------------------------------------------------------- inference

    public async Task<object> ClassifyAsync(byte[] image, string device, int iterations, CancellationToken ct)
    {
        device = device.Trim().ToLowerInvariant();
        if (device is not ("auto" or "npu" or "gpu" or "cpu")) throw new ArgumentException("device must be auto, npu, gpu or cpu.");

        await EnsureRegisteredAsync(download: false, ct);
        var isNewSession = !_sessions.ContainsKey(device);
        var entry = await GetSessionAsync(device, ct);
        var session = entry.Session;

        var inputName = session.InputMetadata.Keys.First();
        var meta = session.InputMetadata[inputName];
        if (meta.ElementType != typeof(float))
        {
            throw new NotSupportedException($"This demo feeds float32 images; the model input '{inputName}' is {meta.ElementType.Name}. Rebuild with winml-cli defaults or --no-quant.");
        }
        var (nchw, height, width) = ResolveLayout(meta.Dimensions);

        var tensor = await ImageHelpers.ToNormalizedTensorAsync(image, width, height, nchw, _mean, _std);
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, tensor) };

        var (times, logits) = await Task.Run(() =>
        {
            using (session.Run(inputs)) { } // warm-up, not timed

            var samples = new List<double>(iterations);
            float[] last = Array.Empty<float>();
            for (var i = 0; i < iterations; i++)
            {
                ct.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                using var results = session.Run(inputs);
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds);
                if (i == iterations - 1) last = results.First().AsEnumerable<float>().ToArray();
            }
            return (samples, last);
        }, ct);

        var labels = LoadLabels(entry.ModelPath, logits.Length);
        var top = TopK(logits, 5)
            .Select(t => new { label = labels[t.Index], probability = Math.Round(t.Probability, 4), index = t.Index })
            .ToArray();

        var sorted = times.OrderBy(t => t).ToArray();
        double Pct(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

        _log.LogInformation("[Windows ML] {Device} via {Provider}: top-1 '{Label}', avg {Avg:0.00} ms over {N} runs",
            device, entry.Provider, top.FirstOrDefault()?.label, times.Average(), iterations);

        return new
        {
            engine = "Windows ML",
            device,
            provider = entry.Provider,
            model = Path.GetFileName(entry.ModelPath),
            modelPath = entry.ModelPath,
            input = new { name = inputName, layout = nchw ? "NCHW" : "NHWC", width, height },
            sessionCreateMs = isNewSession ? Math.Round(entry.CreateMs, 1) : (double?)null,
            iterations,
            latencyMs = new
            {
                avg = Math.Round(times.Average(), 3),
                min = Math.Round(sorted[0], 3),
                p50 = Math.Round(Pct(0.50), 3),
                p90 = Math.Round(Pct(0.90), 3),
                max = Math.Round(sorted[^1], 3),
            },
            top,
        };
    }

    private static (bool Nchw, int Height, int Width) ResolveLayout(int[] dims)
    {
        if (dims.Length != 4) throw new NotSupportedException($"Expected a 4-D image input, got [{string.Join(",", dims)}].");
        static int Or224(int v) => v > 0 ? v : 224;
        if (dims[1] == 3 || (dims[1] <= 0 && dims[3] != 3)) return (true, Or224(dims[2]), Or224(dims[3])); // NCHW
        return (false, Or224(dims[1]), Or224(dims[2])); // NHWC
    }

    private static (int Index, double Probability)[] TopK(float[] values, int k)
    {
        if (values.Length == 0) return Array.Empty<(int, double)>();
        double[] probs;
        var sum = values.Sum(v => (double)v);
        if (values.All(v => v >= 0 && v <= 1) && Math.Abs(sum - 1) < 0.01)
        {
            probs = values.Select(v => (double)v).ToArray(); // already probabilities
        }
        else
        {
            var max = values.Max();
            var exps = values.Select(v => Math.Exp(v - max)).ToArray();
            var total = exps.Sum();
            probs = exps.Select(e => e / total).ToArray();
        }
        return probs.Select((p, i) => (i, p)).OrderByDescending(t => t.p).Take(k).ToArray();
    }

    /// <summary>Labels from labels.txt or a Hugging Face config.json (id2label) next to the model.</summary>
    private string[] LoadLabels(string modelPath, int count)
    {
        if (_labels is not null && _labelsFor == modelPath && _labels.Length >= count) return _labels;

        string[]? found = null;
        var dir = Path.GetDirectoryName(modelPath);
        for (var depth = 0; depth < 3 && dir is not null && found is null; depth++, dir = Path.GetDirectoryName(dir))
        {
            var txt = Path.Combine(dir, "labels.txt");
            if (File.Exists(txt))
            {
                found = File.ReadAllLines(txt).Where(l => l.Length > 0).ToArray();
                break;
            }

            foreach (var cfg in Directory.EnumerateFiles(dir, "config.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
                    if (!doc.RootElement.TryGetProperty("id2label", out var map)) continue;
                    var arr = new string[count];
                    foreach (var p in map.EnumerateObject())
                    {
                        if (int.TryParse(p.Name, out var i) && i >= 0 && i < count) arr[i] = p.Value.GetString() ?? $"class {i}";
                    }
                    found = arr;
                }
                catch (JsonException) { /* not a HF config */ }
            }
        }

        var labels = new string[count];
        for (var i = 0; i < count; i++)
        {
            labels[i] = found is not null && i < found.Length && !string.IsNullOrEmpty(found[i]) ? found[i] : $"class #{i}";
        }
        _labels = labels;
        _labelsFor = modelPath;
        return labels;
    }

    public void Dispose()
    {
        foreach (var s in _sessions.Values) s.Session.Dispose();
        _sessions.Clear();
    }
}
