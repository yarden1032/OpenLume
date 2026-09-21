namespace OpenLume.Infrastructure.AI;

public sealed record OllamaOptions(
    Uri Endpoint,
    string Model,
    TimeSpan Timeout)
{
    public static OllamaOptions LocalDefault { get; } = new(
        new Uri("http://127.0.0.1:11434"),
        Environment.GetEnvironmentVariable("OPENLUME_OLLAMA_MODEL") ?? "gemma4",
        TimeSpan.FromMinutes(3));
}

