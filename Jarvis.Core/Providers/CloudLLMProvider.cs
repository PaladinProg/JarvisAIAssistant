using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jarvis.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Providers;

public class CloudLLMProvider : ILLMProvider
{
    public string Name => "OpenRouter Free (Cloud, No VPN)";
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_apiKey);

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger<CloudLLMProvider>? _logger;

    public CloudLLMProvider(string apiKey, string model = "deepseek/deepseek-chat:free", ILogger<CloudLLMProvider>? logger = null)
    {
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? "deepseek/deepseek-chat:free" : model;
        _logger = logger;

        _http = new HttpClient
        {
            BaseAddress = new Uri("https://openrouter.ai/api/v1/"),
            Timeout = TimeSpan.FromSeconds(8) // Таймаут 8 сек для голосового отклика
        };

        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            // OpenRouter рекомендует указывать заголовок реферера (можно любой)
            _http.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/PaladinProg/JarvisAIAssistant");
            _http.DefaultRequestHeaders.Add("X-Title", "Jarvis Assistant");
        }
    }

    public async Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("API ключ OpenRouter не указан.");

        const string systemPrompt =
            "Ты — голосовой ассистент Джарвис. Твой собеседник — твой создатель. " +
            "Отвечай кратко, чётко, живо и по существу: максимум 2-3 коротких предложения (до 40 слов), так как твой ответ будет озвучен голосом. " +
            "Не используй markdown (звёздочки, решётки), списки, смайлы и спецсимволы. " +
            "Все иностранные слова и бренды пиши только русской транскрипцией (например: 'Виндовс', 'Пайтон', 'Оллама').";

        var body = new
        {
            model = _model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = prompt }
            },
            temperature = 0.6,
            max_tokens = 250
        };

        var json = JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync("chat/completions", content, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseJson);

        var answer = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";

        return answer.Trim();
    }
}