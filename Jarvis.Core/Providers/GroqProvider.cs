using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jarvis.Core.Interfaces;

namespace Jarvis.Core.Providers;

public class GroqProvider : ILLMProvider
{
    public string Name => "Groq (Cloud)";
    public bool IsAvailable => !string.IsNullOrEmpty(_apiKey);

    private readonly HttpClient _http;
    private readonly string _apiKey;

    public GroqProvider(string apiKey)
    {
        _apiKey = apiKey;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        var body = new
        {
            model = "llama-3.3-70b-versatile", // Или qwen-2.5-72b
            messages = new[] { new { role = "user", content = prompt } },
            max_tokens = 1024
        };

        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync("https://api.groq.com/openai/v1/chat/completions", content, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseJson);
        return doc.RootElement.GetProperty("choices")[0]
                  .GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}