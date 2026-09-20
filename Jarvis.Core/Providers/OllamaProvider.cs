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
        // Системный промпт для Джарвиса
        const string systemPrompt =
            "Ты — голосовой ассистент Джарвис. Твой собеседник — твой создатель (обращайся вежливо). " +
            "Отвечай кратко, чётко и живо: максимум 1-2 коротких предложения, так как твой ответ будет озвучен вслух. " +
            "Не используй markdown (звёздочки, решётки), списки, смайлы и спецсимволы. " +
            "ВАЖНО: Все иностранные слова, бренды и термины пиши только русскими буквами (транскрипцией), " +
            "например: 'Алибаба Клауд', 'Виндовс', 'Ютуб', 'Гугл', 'Пайтон', чтобы голосовой синтезатор не пропускал их.";

        // Формируем запрос к Ollama с системным промптом и запросом пользователя
        var payload = new
        {
            model = _modelName,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = prompt }
            },
            stream = false
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