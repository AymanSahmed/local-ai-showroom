namespace LocalAIShowroom.Bridge;

// Request bodies (JSON, camelCase from the browser).

public sealed record PrepareRequest(string Feature);

public sealed record GenerateRequest(string Prompt, string? System = null, float? Temperature = null);

public sealed record TextRequest(string Text);

/// <summary>Image as a data URL (data:image/png;base64,...) or plain base64.</summary>
public sealed record ImageRequest(string Image, string? Kind = null);

public sealed record ClassifyRequest(string Image, string? Device = null, int? Iterations = null);

public sealed record ModelRequest(string Model);

public sealed record ChatMessage(string Role, string Content);

public sealed record ChatRequest(string Model, List<ChatMessage> Messages, float? Temperature = null, int? MaxTokens = null);

public sealed record PerfRequest(string? Device = null, int? Iterations = null);
