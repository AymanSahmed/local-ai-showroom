using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LocalAIShowroom.Bridge.Infrastructure;

/// <summary>Server-sent events: one JSON object per "data:" line.</summary>
public static class Sse
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task StreamAsync(HttpContext ctx, IAsyncEnumerable<object> events, ILogger log)
    {
        ctx.Response.ContentType = "text/event-stream; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

        try
        {
            await foreach (var evt in events.WithCancellation(ctx.RequestAborted))
            {
                await WriteAsync(ctx, evt);
            }
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // Browser stopped listening (Stop button or tab closed).
        }
        catch (Exception ex)
        {
            log.LogWarning("{Path} stream failed: {Message}", ctx.Request.Path, ex.Message);
            if (!ctx.RequestAborted.IsCancellationRequested)
            {
                await WriteAsync(ctx, new { type = "error", message = ex.Message, hresult = Errors.HResult(ex) });
            }
        }
    }

    private static async Task WriteAsync(HttpContext ctx, object evt)
    {
        var json = JsonSerializer.Serialize(evt, evt.GetType(), Json);
        await ctx.Response.WriteAsync($"data: {json}\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }
}

public static class Errors
{
    public static string? HResult(Exception ex) => ex.HResult is 0 or unchecked((int)0x80131500) ? null : $"0x{ex.HResult:X8}";

    public static string Describe(Exception ex)
    {
        var hr = HResult(ex);
        return hr is null ? ex.Message : $"{ex.Message} ({hr})";
    }

    /// <summary>Windows often returns the same message twice, separated by blank lines. Collapse that into one readable line.</summary>
    public static string OneLine(string? text) =>
        string.Join(" ", (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct());

    public static string Trim(string? text, int max = 400)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        text = text.Trim();
        return text.Length <= max ? text : text[..max] + "...";
    }
}

/// <summary>Version of this build, taken from the VERSION file via the csproj (informational version without any +commit suffix).</summary>
public static class BuildInfo
{
    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0]
        ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "unknown";
}

public static class LocalOnly
{
    public static bool IsLocalHost(string? host) =>
        host is not null &&
        (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "[::1]" or "::1");
}

/// <summary>Decodes images sent by the browser as data URLs.</summary>
public static class ImageData
{
    public const int MaxBytes = 20 * 1024 * 1024;

    public static byte[] Decode(string? dataUrlOrBase64)
    {
        if (string.IsNullOrWhiteSpace(dataUrlOrBase64)) throw new ArgumentException("An image is required.");

        var s = dataUrlOrBase64.Trim();
        var comma = s.IndexOf(',');
        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0) s = s[(comma + 1)..];

        byte[] bytes;
        try { bytes = Convert.FromBase64String(s); }
        catch (FormatException) { throw new ArgumentException("The image must be a base64 data URL."); }

        if (bytes.Length == 0) throw new ArgumentException("The image is empty.");
        if (bytes.Length > MaxBytes) throw new ArgumentException("The image is larger than 20 MB.");
        return bytes;
    }
}

/// <summary>
/// Counts outbound HTTP calls. Every HttpClient in the bridge goes through <see cref="EgressGuardHandler"/>,
/// which only allows loopback destinations - the UI shows these counters as "cloud calls: 0".
/// </summary>
public sealed class EgressGuard
{
    private long _allowed;
    private long _blocked;
    private string? _lastBlockedHost;

    public long Allowed => Interlocked.Read(ref _allowed);
    public long Blocked => Interlocked.Read(ref _blocked);
    public string? LastBlockedHost => _lastBlockedHost;

    public void RecordAllowed() => Interlocked.Increment(ref _allowed);

    public void RecordBlocked(string? host)
    {
        Interlocked.Increment(ref _blocked);
        _lastBlockedHost = host;
    }
}

public sealed class EgressGuardHandler(EgressGuard guard) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null || !uri.IsLoopback)
        {
            guard.RecordBlocked(uri?.Host);
            throw new InvalidOperationException($"Egress guard blocked a call to '{uri?.Host}'. This demo only talks to services on this PC.");
        }

        guard.RecordAllowed();
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Runs CLI tools (foundry, winml) without ever waiting on interactive prompts.</summary>
public static partial class ProcessRunner
{
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscape();

    public static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName, string arguments, TimeSpan timeout, CancellationToken ct, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory)) psi.WorkingDirectory = workingDirectory;
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        using var proc = new Process { StartInfo = psi };
        var output = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        proc.Start(); // throws Win32Exception when the tool is not installed
        proc.StandardInput.Close(); // never block on interactive prompts
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"'{fileName} {arguments}' did not finish within {timeout.TotalSeconds:0} s.");
        }

        lock (output)
        {
            return (proc.ExitCode, AnsiEscape().Replace(output.ToString(), ""));
        }
    }

    /// <summary>
    /// Starts a command that may spawn a background daemon. Output is not redirected (a daemon that
    /// inherits the pipe would keep it open forever); the process is left running after the timeout.
    /// </summary>
    public static async Task<int?> RunDetachedAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName, arguments) { UseShellExecute = false, CreateNoWindow = true };
        using var proc = Process.Start(psi) ?? throw new Win32Exception($"Could not start {fileName}.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
            return proc.ExitCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // still running (fine for a foreground server); discovery decides what happened
        }
    }

    public static string Tail(string text, int max) =>
        text.Length <= max ? text : "...\n" + text[^max..];
}

public static class MachineInfo
{
    public static object Get() => new
    {
        os = RuntimeInformation.OSDescription,
        build = Environment.OSVersion.Version.Build,
        osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
        processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        cpu = ReadRegistry(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.ToString()?.Trim(),
        logicalCores = Environment.ProcessorCount,
        memoryGb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024d / 1024d, 1),
        dotnet = RuntimeInformation.FrameworkDescription,
        developerMode = ReadRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock", "AllowDevelopmentWithoutDevLicense") is int v && v == 1,
    };

    private static object? ReadRegistry(string key, string value)
    {
        try { return Registry.GetValue(key, value, null); }
        catch { return null; }
    }
}
