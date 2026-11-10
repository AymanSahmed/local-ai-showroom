using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalAIShowroom.Bridge.Infrastructure;

namespace LocalAIShowroom.Bridge.Services;

/// <summary>
/// Optional: drives the Windows ML CLI (winml-cli, https://github.com/microsoft/winml-cli) so the audience can see the
/// same ONNX model benchmarked with the official tool ('winml perf') and the device/EP inventory ('winml sys').
/// </summary>
public sealed partial class WinmlCliService
{
    [GeneratedRegex(@"\b(Avg|Mean|P50|P90|P95|P99|Min|Max|Std)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MetricName();

    [GeneratedRegex(@"-?\d+(?:\.\d+)?")]
    private static partial Regex Number();

    [GeneratedRegex(@"Throughput:\s*([\d.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Throughput();

    [GeneratedRegex(@"^\s*Device:\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DeviceLine();

    private readonly WindowsMLService _ml;
    private readonly ILogger<WinmlCliService> _log;
    private readonly string _command;
    private readonly string _prefixArgs;
    private readonly string? _workingDirectory;
    private bool? _found;

    public WinmlCliService(IConfiguration config, WindowsMLService ml, ILogger<WinmlCliService> log)
    {
        _ml = ml;
        _log = log;
        _command = Environment.ExpandEnvironmentVariables(string.IsNullOrWhiteSpace(config["WinmlCli:Command"]) ? "winml" : config["WinmlCli:Command"]!);
        _prefixArgs = config["WinmlCli:PrefixArgs"] ?? "";
        var wd = config["WinmlCli:WorkingDirectory"];
        _workingDirectory = string.IsNullOrWhiteSpace(wd) ? null : Environment.ExpandEnvironmentVariables(wd);
    }

    private string Display => string.IsNullOrWhiteSpace(_prefixArgs) ? _command : $"{_command} {_prefixArgs}";

    public object GetStatus() => new { command = Display, found = _found };

    private async Task<(int ExitCode, string Output)> RunAsync(string args, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(_command, $"{_prefixArgs} {args}".Trim(), timeout, ct, _workingDirectory);
            _found = true;
            return result;
        }
        catch (Win32Exception)
        {
            _found = false;
            throw new InvalidOperationException(
                $"winml-cli was not found ('{_command}'). Install it (see README: Silicon Lab) and set WinmlCli:Command in appsettings.local.json to the full path of winml.exe.");
        }
    }

    public async Task<object> SysAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var (exit, output) = await RunAsync("sys --list-device --list-ep", TimeSpan.FromMinutes(3), ct);
        return new { exitCode = exit, ms = sw.ElapsedMilliseconds, command = $"{Display} sys --list-device --list-ep", output = ProcessRunner.Tail(output, 8000) };
    }

    public async Task<object> PerfAsync(string device, int iterations, CancellationToken ct)
    {
        device = device.Trim().ToLowerInvariant();
        if (device is not ("auto" or "npu" or "gpu" or "cpu")) throw new ArgumentException("device must be auto, npu, gpu or cpu.");
        var model = _ml.ResolveModelPath()
            ?? throw new FileNotFoundException("No ONNX model found. Build one first (README: Silicon Lab, or scripts\\prepare-models.ps1).");

        // winml-cli picks hardware itself: on a PC whose only GPU is virtual, auto/gpu makes its Python process abort inside ONNX Runtime.
        var requestedDevice = device;
        string? note = null;
        if (device is "auto" or "gpu")
        {
            var hardware = await _ml.GetHardwareAsync(ct);
            if (hardware.VirtualGpuOnly && !hardware.HasNpu)
            {
                if (device == "gpu")
                {
                    throw new NotSupportedException("The only GPU on this PC is a virtual or software adapter (Hyper-V video / Basic Render Driver); running AI models on it crashes ONNX Runtime. Use cpu.");
                }
                device = "cpu";
                note = "This PC has no NPU and its only GPU is virtual (Hyper-V video / Basic Render Driver), so winml perf ran on the CPU instead of auto.";
            }
        }

        var report = Path.Combine(Path.GetTempPath(), $"showroom-perf-{DateTime.Now:yyyyMMdd-HHmmss}-{device}.json");
        var args = $"perf -m \"{model}\" --device {device} --iterations {iterations} -o \"{report}\"";

        _log.LogInformation("[winml-cli] {Command} {Args}", Display, args);
        var sw = Stopwatch.StartNew();
        var (exit, output) = await RunAsync(args, TimeSpan.FromMinutes(20), ct);

        JsonElement? json = null;
        if (File.Exists(report))
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(report, ct));
                json = doc.RootElement.Clone();
            }
            catch (JsonException) { /* keep console output only */ }
        }

        return new
        {
            exitCode = exit,
            device,
            requestedDevice,
            note,
            iterations,
            ms = sw.ElapsedMilliseconds,
            command = $"{Display} {args}",
            reportPath = File.Exists(report) ? report : null,
            // 0xC0000409: the Python process of winml-cli aborted inside ONNX Runtime (seen when auto/gpu picks a virtual GPU).
            hint = exit == unchecked((int)0xC0000409)
                ? "winml-cli's Python process crashed inside ONNX Runtime (exit code 0xC0000409). On a PC whose only GPU is virtual (Hyper-V video / Basic Render Driver) device auto or gpu can do this. Run it with device cpu."
                : null,
            summary = ParseSummary(output),
            report = json,
            output = ProcessRunner.Tail(output, 8000),
        };
    }

    /// <summary>Parses the console table printed by 'winml perf' (Avg P50 P90 P95 P99 Min Max Std + Throughput).</summary>
    internal static object? ParseSummary(string output)
    {
        var lines = output.Replace("\r", "").Split('\n');
        var latency = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Length && latency.Count == 0; i++)
        {
            var names = MetricName().Matches(lines[i]).Select(m => m.Value.ToLowerInvariant()).ToArray();
            if (names.Length < 3) continue;

            for (var j = i + 1; j < Math.Min(lines.Length, i + 4); j++)
            {
                var values = Number().Matches(lines[j]).Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
                if (values.Length >= names.Length)
                {
                    for (var k = 0; k < names.Length; k++) latency[names[k]] = values[k];
                    break;
                }
            }
        }

        var tp = Throughput().Match(output);
        var dev = DeviceLine().Match(output);
        if (latency.Count == 0 && !tp.Success) return null;

        return new
        {
            device = dev.Success ? dev.Groups[1].Value : null,
            latencyMs = latency,
            throughputPerSec = tp.Success && double.TryParse(tp.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null,
        };
    }
}
