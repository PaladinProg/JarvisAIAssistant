using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Core.Interfaces;

namespace Jarvis.Core.Providers;

public class OllamaProvider : ILLMProvider
{
    public string Name => "Ollama (Local)";
    public bool IsAvailable => true;

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _modelName;

    public OllamaProvider(string baseUrl, string modelName)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _modelName = modelName;
        _http = new HttpClient();
    }

    public async Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        // Формируем запрос точно так, как этого ждет API Ollama
        var payload = new
        {
            model = _modelName,
            messages = new[]
            {
                new { role = "user", content = prompt }
            },
            stream = false // Нам нужен полный ответ сразу
        };

        try
        {
            // Отправляем POST запрос на локальный сервер Ollama
            var response = await _http.PostAsJsonAsync($"{_baseUrl}/api/chat", payload, ct);
            response.EnsureSuccessStatusCode();

            // Читаем JSON ответ
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

            // Извлекаем текст: response.message.content
            if (result.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content))
            {
                return content.GetString() ?? "";
            }

            return "Ошибка парсинга ответа Ollama";
        }
        catch (Exception ex)
        {
            return $"Ошибка связи с Ollama: {ex.Message}";
        }
    }
}