using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using LocalAIShowroom.Bridge.Infrastructure;
using Microsoft.Graphics.Imaging;
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.ContentSafety;
using Microsoft.Windows.AI.Imaging;
using Microsoft.Windows.AI.Text;

namespace LocalAIShowroom.Bridge.Services;

/// <summary>
/// Windows AI APIs from the Windows App SDK: the built-in language model (Phi Silica, being replaced by
/// Aion Instruct from Nov 2026 / Jan 2027), text recognition (OCR) and image description.
/// Requires a Copilot+ PC (NPU) and package identity with the systemAIModels capability.
/// </summary>
public sealed class WindowsAIService : IDisposable
{
    private readonly ILogger<WindowsAIService> _log;
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private readonly SemaphoreSlim _generateLock = new(1, 1);
    private LanguageModel? _languageModel;
    private TextRecognizer? _textRecognizer;
    private ImageDescriptionGenerator? _imageDescriber;

    public WindowsAIService(ILogger<WindowsAIService> log) => _log = log;

    public static (bool HasIdentity, string? FamilyName) Identity()
    {
        try
        {
            return (true, global::Windows.ApplicationModel.Package.Current.Id.FamilyName);
        }
        catch (Exception)
        {
            return (false, null); // InvalidOperationException when the process has no package identity
        }
    }

    // ---------------------------------------------------------------- readiness

    public object GetStatus()
    {
        var (hasIdentity, family) = Identity();
        string? runtime = null;
        try { runtime = global::Microsoft.Windows.ApplicationModel.WindowsAppRuntime.RuntimeInfo.AsString; }
        catch (Exception) { /* runtime not initialized */ }

        return new
        {
            hasIdentity,
            packageFamilyName = family,
            runtimeVersion = runtime,
            features = new[]
            {
                Probe("languageModel", "Language model (Phi Silica / Aion Instruct)", () => LanguageModel.GetReadyState(), hasIdentity),
                Probe("textRecognizer", "Text recognition (OCR)", () => TextRecognizer.GetReadyState(), hasIdentity),
                Probe("imageDescription", "Image description", () => ImageDescriptionGenerator.GetReadyState(), hasIdentity),
            },
        };
    }

    private static object Probe(string id, string name, Func<AIFeatureReadyState> getState, bool hasIdentity)
    {
        try
        {
            return new { id, name, state = getState().ToString() };
        }
        catch (Exception ex)
        {
            return new
            {
                id,
                name,
                state = "Unavailable",
                error = hasIdentity
                    ? Errors.Describe(ex)
                    : "Needs package identity: start the bridge with 'dotnet run' (Developer Mode on), not by double-clicking the .exe.",
            };
        }
    }

    public async Task<object> PrepareAsync(string feature, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        _log.LogInformation("[Windows AI] EnsureReadyAsync({Feature}) - may download the model through Windows Update", feature);
        AIFeatureReadyResult result = feature switch
        {
            "languageModel" => await LanguageModel.EnsureReadyAsync(),
            "textRecognizer" => await TextRecognizer.EnsureReadyAsync(),
            "imageDescription" => await ImageDescriptionGenerator.EnsureReadyAsync(),
            _ => throw new ArgumentException($"Unknown feature '{feature}'."),
        };

        var ok = result.Status == AIFeatureReadyResultState.Success;
        return new
        {
            feature,
            result = result.Status.ToString(),
            error = ok ? null : Errors.OneLine(result.ExtendedError?.Message),
            hresult = ok || result.ExtendedError is null ? null : Errors.HResult(result.ExtendedError),
            hint = ok ? null : "Windows downloads this model through Windows Update, so the PC must be online once; afterwards it works offline. " +
                               "If the message says the product is not applicable, this PC (or VM) does not support the feature (a Copilot+ PC with an NPU is required).",
            ms = sw.ElapsedMilliseconds,
            status = GetStatus(),
        };
    }

    // ---------------------------------------------------------------- lazy model creation

    private static async Task EnsureReadyOrThrowAsync(string name, Func<AIFeatureReadyState> getState, Func<Task<AIFeatureReadyResult>> ensure)
    {
        var state = getState();
        if (state == AIFeatureReadyState.Ready) return;
        if (state == AIFeatureReadyState.NotReady)
        {
            var r = await ensure();
            if (r.Status != AIFeatureReadyResultState.Success)
            {
                throw new InvalidOperationException($"{name} could not be prepared: {Errors.OneLine(r.ExtendedError?.Message)}");
            }
            return;
        }
        throw new InvalidOperationException($"{name} is not available on this PC ({state}).");
    }

    private async Task<LanguageModel> GetLanguageModelAsync()
    {
        if (_languageModel is not null) return _languageModel;
        await _createLock.WaitAsync();
        try
        {
            if (_languageModel is null)
            {
                await EnsureReadyOrThrowAsync("The Windows AI language model", () => LanguageModel.GetReadyState(), async () => await LanguageModel.EnsureReadyAsync());
                _languageModel = await LanguageModel.CreateAsync();
            }
            return _languageModel;
        }
        finally { _createLock.Release(); }
    }

    private async Task<TextRecognizer> GetTextRecognizerAsync()
    {
        if (_textRecognizer is not null) return _textRecognizer;
        await _createLock.WaitAsync();
        try
        {
            if (_textRecognizer is null)
            {
                await EnsureReadyOrThrowAsync("Text recognition", () => TextRecognizer.GetReadyState(), async () => await TextRecognizer.EnsureReadyAsync());
                _textRecognizer = await TextRecognizer.CreateAsync();
            }
            return _textRecognizer;
        }
        finally { _createLock.Release(); }
    }

    private async Task<ImageDescriptionGenerator> GetImageDescriberAsync()
    {
        if (_imageDescriber is not null) return _imageDescriber;
        await _createLock.WaitAsync();
        try
        {
            if (_imageDescriber is null)
            {
                await EnsureReadyOrThrowAsync("Image description", () => ImageDescriptionGenerator.GetReadyState(), async () => await ImageDescriptionGenerator.EnsureReadyAsync());
                _imageDescriber = await ImageDescriptionGenerator.CreateAsync();
            }
            return _imageDescriber;
        }
        finally { _createLock.Release(); }
    }

    // ---------------------------------------------------------------- language model

    /// <summary>Streams the built-in model's answer. Progress callbacks carry text deltas.</summary>
    public async IAsyncEnumerable<object> StreamGenerateAsync(string prompt, string? system, float? temperature, [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Prompt is required.");
        var languageModel = await GetLanguageModelAsync();

        await _generateLock.WaitAsync(ct);
        try
        {
            var options = new LanguageModelOptions();
            if (temperature is float t) options.Temperature = t;

            var fullPrompt = string.IsNullOrWhiteSpace(system) ? prompt : $"{system.Trim()}\n\n{prompt}";
            var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
            var sw = Stopwatch.StartNew();

            var operation = languageModel.GenerateResponseAsync(fullPrompt, options);
            operation.Progress = (_, delta) => channel.Writer.TryWrite(delta);
            var completion = operation.AsTask();
            _ = completion.ContinueWith(task => channel.Writer.TryComplete(task.Exception?.GetBaseException()), TaskScheduler.Default);
            using var cancelRegistration = ct.Register(() => { try { operation.Cancel(); } catch { /* already completed */ } });

            yield return new { type = "start", engine = "Windows AI APIs", model = "Built-in language model", device = "NPU" };

            var streamed = new StringBuilder();
            await foreach (var delta in channel.Reader.ReadAllAsync(ct))
            {
                if (string.IsNullOrEmpty(delta)) continue;
                streamed.Append(delta);
                yield return new { type = "delta", text = delta };
            }

            var result = await completion;
            var status = result.Status.ToString();
            var complete = result.Status == LanguageModelResponseStatus.Complete;

            // Progress events can race with completion: send whatever the final text adds.
            var finalText = result.Text ?? "";
            if (complete && finalText.Length > streamed.Length && finalText.StartsWith(streamed.ToString(), StringComparison.Ordinal))
            {
                yield return new { type = "delta", text = finalText[streamed.Length..] };
            }

            _log.LogInformation("[Windows AI] language model answered in {Ms} ms ({Status})", sw.ElapsedMilliseconds, status);
            yield return new { type = "done", status, complete, serverMs = sw.ElapsedMilliseconds };
        }
        finally
        {
            _generateLock.Release();
        }
    }

    /// <summary>Text intelligence skill: summarize.</summary>
    public async Task<object> SummarizeAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Text is required.");
        var languageModel = await GetLanguageModelAsync();
        await _generateLock.WaitAsync(ct);
        try
        {
            var sw = Stopwatch.StartNew();
            var summarizer = new TextSummarizer(languageModel);
            var result = await summarizer.SummarizeAsync(text);
            var status = result.Status.ToString();
            _log.LogInformation("[Windows AI] TextSummarizer {Ms} ms ({Status})", sw.ElapsedMilliseconds, status);
            return new { engine = "Windows AI · TextSummarizer", device = "NPU", status, text = status == "Complete" ? result.Text : null, ms = sw.ElapsedMilliseconds };
        }
        finally { _generateLock.Release(); }
    }

    /// <summary>Text intelligence skill: rewrite for clarity.</summary>
    public async Task<object> RewriteAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Text is required.");
        var languageModel = await GetLanguageModelAsync();
        await _generateLock.WaitAsync(ct);
        try
        {
            var sw = Stopwatch.StartNew();
            var rewriter = new TextRewriter(languageModel);
            var result = await rewriter.RewriteAsync(text);
            var status = result.Status.ToString();
            _log.LogInformation("[Windows AI] TextRewriter {Ms} ms ({Status})", sw.ElapsedMilliseconds, status);
            return new { engine = "Windows AI · TextRewriter", device = "NPU", status, text = status == "Complete" ? result.Text : null, ms = sw.ElapsedMilliseconds };
        }
        finally { _generateLock.Release(); }
    }

    // ---------------------------------------------------------------- imaging

    public async Task<object> RecognizeTextAsync(byte[] image, CancellationToken ct)
    {
        TextRecognizer recognizer;
        try
        {
            recognizer = await GetTextRecognizerAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No NPU, model not provisioned, or no package identity: use the CPU OCR engine that ships with Windows.
            _log.LogInformation("[Windows AI] TextRecognizer unavailable ({Reason}); using the built-in Windows OCR engine.", ex.Message);
            return await RecognizeWithBuiltInOcrAsync(image, Errors.Trim(Errors.OneLine(ex.Message), 160), ct);
        }

        using var bitmap = await ImageHelpers.DecodeAsync(image);
        var buffer = ImageBuffer.CreateForSoftwareBitmap(bitmap);

        var sw = Stopwatch.StartNew();
        var recognized = await Task.Run(() => recognizer.RecognizeTextFromImage(buffer), ct);
        var ms = sw.ElapsedMilliseconds;

        var lines = recognized.Lines.Select(line => new
        {
            text = line.Text,
            box = new[]
            {
                line.BoundingBox.TopLeft.X, line.BoundingBox.TopLeft.Y,
                line.BoundingBox.TopRight.X, line.BoundingBox.TopRight.Y,
                line.BoundingBox.BottomRight.X, line.BoundingBox.BottomRight.Y,
                line.BoundingBox.BottomLeft.X, line.BoundingBox.BottomLeft.Y,
            },
            words = line.Words.Select(word => new
            {
                text = word.Text,
                confidence = Math.Round(word.MatchConfidence, 3),
                box = new[]
                {
                    word.BoundingBox.TopLeft.X, word.BoundingBox.TopLeft.Y,
                    word.BoundingBox.TopRight.X, word.BoundingBox.TopRight.Y,
                    word.BoundingBox.BottomRight.X, word.BoundingBox.BottomRight.Y,
                    word.BoundingBox.BottomLeft.X, word.BoundingBox.BottomLeft.Y,
                },
            }).ToArray(),
        }).ToArray();

        _log.LogInformation("[Windows AI] TextRecognizer: {Lines} lines in {Ms} ms", lines.Length, ms);
        return new
        {
            engine = "Windows AI · TextRecognizer",
            device = "NPU",
            width = bitmap.PixelWidth,
            height = bitmap.PixelHeight,
            ms,
            lineCount = lines.Length,
            text = string.Join("\n", lines.Select(l => l.text)),
            lines,
        };
    }

    /// <summary>
    /// Fallback OCR: Windows.Media.Ocr.OcrEngine runs on the CPU on any Windows 10/11 PC with an OCR language pack, fully offline.
    /// It returns the same shape as the NPU path (rectangles instead of polygons, no per-word confidence).
    /// </summary>
    private async Task<object> RecognizeWithBuiltInOcrAsync(byte[] image, string reason, CancellationToken ct)
    {
        var engine = global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException(
                $"Windows AI text recognition is unavailable ({reason}) and no Windows OCR language pack is installed. " +
                "Add one in Settings > Time & language > Language & region (Optical character recognition).");

        using var bitmap = await ImageHelpers.DecodeAsync(image);
        var max = (int)global::Windows.Media.Ocr.OcrEngine.MaxImageDimension;
        if (bitmap.PixelWidth > max || bitmap.PixelHeight > max)
        {
            throw new ArgumentException($"The image is larger than the OCR engine limit of {max} pixels per side.");
        }

        var sw = Stopwatch.StartNew();
        var result = await engine.RecognizeAsync(bitmap).AsTask(ct);
        var ms = sw.ElapsedMilliseconds;

        static double[] Box(double x1, double y1, double x2, double y2) => new[] { x1, y1, x2, y1, x2, y2, x1, y2 };
        var lines = result.Lines.Select(line =>
        {
            var words = line.Words.Select(w => new
            {
                text = w.Text,
                box = Box(w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.X + w.BoundingRect.Width, w.BoundingRect.Y + w.BoundingRect.Height),
            }).ToArray();
            var box = words.Length == 0
                ? Array.Empty<double>()
                : Box(words.Min(w => w.box[0]), words.Min(w => w.box[1]), words.Max(w => w.box[4]), words.Max(w => w.box[5]));
            return new { text = line.Text, box, words };
        }).ToArray();

        _log.LogInformation("[Windows OCR] built-in engine ({Language}): {Lines} lines in {Ms} ms", engine.RecognizerLanguage.DisplayName, lines.Length, ms);
        return new
        {
            engine = "Windows OCR (built-in engine)",
            device = "CPU",
            fallback = true,
            note = $"Windows AI text recognition isn't available on this PC ({reason}), so the built-in Windows OCR engine ({engine.RecognizerLanguage.DisplayName}) ran on the CPU instead.",
            width = bitmap.PixelWidth,
            height = bitmap.PixelHeight,
            ms,
            lineCount = lines.Length,
            text = string.Join("\n", lines.Select(l => l.text)),
            lines,
        };
    }

    public async Task<object> DescribeImageAsync(byte[] image, string? kind, CancellationToken ct)
    {
        var describer = await GetImageDescriberAsync();
        using var bitmap = await ImageHelpers.DecodeAsync(image);
        var buffer = ImageBuffer.CreateForSoftwareBitmap(bitmap);
        var descriptionKind = (kind ?? "").ToLowerInvariant() switch
        {
            "detailed" => ImageDescriptionKind.DetailedDescription,
            "diagram" => ImageDescriptionKind.DiagramDescription,
            "accessible" => ImageDescriptionKind.AccessibleDescription,
            _ => ImageDescriptionKind.BriefDescription,
        };

        var sw = Stopwatch.StartNew();
        var result = await describer.DescribeAsync(buffer, descriptionKind, new ContentFilterOptions());
        var status = result.Status.ToString();
        _log.LogInformation("[Windows AI] ImageDescription ({Kind}) {Ms} ms ({Status})", descriptionKind, sw.ElapsedMilliseconds, status);
        return new
        {
            engine = "Windows AI · ImageDescription",
            device = "NPU",
            kind = descriptionKind.ToString(),
            status,
            description = status == "Complete" ? result.Description : null,
            ms = sw.ElapsedMilliseconds,
        };
    }

    public void Dispose()
    {
        // The WinRT model objects are IClosable (projected as IDisposable).
        (_languageModel as IDisposable)?.Dispose();
        (_textRecognizer as IDisposable)?.Dispose();
        (_imageDescriber as IDisposable)?.Dispose();
    }
}
