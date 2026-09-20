namespace Jarvis.Core.Interfaces;

public interface ILLMProvider
{
    string Name { get; }
    Task<string> GenerateAsync(string prompt, CancellationToken ct = default);
    bool IsAvailable { get; }
}

public enum Complexity
{
    Simple,
    Medium,
    Hard
}