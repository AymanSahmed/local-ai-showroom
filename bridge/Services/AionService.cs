using System.Runtime.CompilerServices;
#if AION
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using AionText = AionInstructPreview.Text;
#endif

namespace LocalAIShowroom.Bridge.Services;

/// <summary>
/// Optional lane for the Aion Instruct Preview SDK (https://github.com/microsoft/Aion-Instruct-Preview-Sample).
/// Aion Instruct is the on-device model that replaces Phi Silica behind the Windows AI language model API.
/// The preview package is for developer validation only (see its EULA) - this lane is off unless you build
/// with -p:EnableAion=true after installing the framework MSIX and placing the SDK .nupkg in ./nuget-local.
/// </summary>
public sealed partial class AionService
{
#if AION
    private const string FrameworkFamily = "Microsoft.AionInstructPreview.Framework.1.0_8wekyb3d8bbwe";
    private const int ArchX64 = 0x4;    // PackageDependencyProcessorArchitectures_X64
    private const int ArchArm64 = 0x10; // PackageDependencyProcessorArchitectures_Arm64

    private readonly ILogger<AionService> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AionText.LanguageModel? _model;
    private bool _dependencyAdded;
    private string? _lastError;

    public AionService(ILogger<AionService> log) => _log = log;

    public object GetStatus() => new
    {
        compiled = true,
        loaded = _model is not null,
        error = _lastError,
        note = "Developer validation preview. Supported on Snapdragon (QNN) and Intel Lunar Lake (OpenVINO) Copilot+ PCs; no CPU fallback.",
    };

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernelbase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int TryCreatePackageDependency(IntPtr user, string packageFamilyName, ulong minVersion,
        int processorArchitectures, int lifetimeKind, string? lifetimeArtifact, int options, out IntPtr packageDependencyId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernelbase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AddPackageDependency(IntPtr packageDependencyId, int rank, int options,
        out IntPtr packageDependencyContext, out IntPtr packageFullName);

    /// <summary>Adds the installed Aion framework package to this process' package graph (dynamic dependency).</summary>
    private void EnsureFrameworkDependency()
    {
        if (_dependencyAdded) return;
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? ArchArm64 : ArchX64;
        var hr = TryCreatePackageDependency(IntPtr.Zero, FrameworkFamily, 0, arch, 0 /* process lifetime */, null, 0, out var dependencyId);
        if (hr != 0)
        {
            throw new InvalidOperationException(
                $"The Aion Instruct Preview framework package was not found (0x{hr:X8}). Install the framework MSIX from the sample's GitHub release.");
        }
        hr = AddPackageDependency(dependencyId, 0, 0, out _, out _);
        if (hr != 0) throw new InvalidOperationException($"AddPackageDependency failed (0x{hr:X8}).");
        _dependencyAdded = true;
    }

    private async Task<AionText.LanguageModel> GetModelAsync()
    {
        if (_model is not null) return _model;
        await _lock.WaitAsync();
        try
        {
            if (_model is null)
            {
                EnsureFrameworkDependency();
                _log.LogInformation("[Aion] Loading model - the first run compiles NPU caches and can take several minutes...");
                _model = await AionText.LanguageModel.CreateAsync();
            }
            _lastError = null;
            return _model;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async IAsyncEnumerable<object> StreamGenerateAsync(string prompt, string? system, [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Prompt is required.");
        var model = await GetModelAsync();

        await _lock.WaitAsync(ct);
        try
        {
            // Options must not be null in the preview; these are the sample's chat settings.
            var options = new AionText.LanguageModelOptions { Temperature = 0.5f, TopP = 0.9f, TopK = 40 };
            var fullPrompt = string.IsNullOrWhiteSpace(system) ? prompt : $"{system.Trim()}\n\n{prompt}";
            var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
            var sw = Stopwatch.StartNew();

            var operation = model.GenerateResponseAsync(fullPrompt, options);
            operation.Progress = (_, delta) => channel.Writer.TryWrite(delta);
            var completion = operation.AsTask();
            _ = completion.ContinueWith(task => channel.Writer.TryComplete(task.Exception?.GetBaseException()), TaskScheduler.Default);
            using var cancelRegistration = ct.Register(() => { try { operation.Cancel(); } catch { /* done */ } });

            yield return new { type = "start", engine = "Aion Instruct Preview", model = "Aion Instruct (preview)", device = "NPU" };

            var streamed = new StringBuilder();
            await foreach (var delta in channel.Reader.ReadAllAsync(ct))
            {
                if (string.IsNullOrEmpty(delta)) continue;
                streamed.Append(delta);
                yield return new { type = "delta", text = delta };
            }

            var result = await completion;
            var status = result.Status.ToString();
            var complete = status == "Complete";
            var finalText = result.Text ?? "";
            if (complete && finalText.Length > streamed.Length && finalText.StartsWith(streamed.ToString(), StringComparison.Ordinal))
            {
                yield return new { type = "delta", text = finalText[streamed.Length..] };
            }

            _log.LogInformation("[Aion] answered in {Ms} ms ({Status})", sw.ElapsedMilliseconds, status);
            yield return new { type = "done", status, complete, serverMs = sw.ElapsedMilliseconds };
        }
        finally
        {
            _lock.Release();
        }
    }
#else
    public object GetStatus() => new
    {
        compiled = false,
        loaded = false,
        note = "Optional. Install the Aion Instruct Preview framework, put the SDK .nupkg in bridge/nuget-local and run: dotnet run -p:EnableAion=true",
    };

    public async IAsyncEnumerable<object> StreamGenerateAsync(string prompt, string? system, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        throw new InvalidOperationException("This bridge was built without the Aion Instruct Preview lane. Rebuild with: dotnet run -p:EnableAion=true");
#pragma warning disable CS0162 // keeps this method an async iterator
        yield break;
#pragma warning restore CS0162
    }
#endif
}
