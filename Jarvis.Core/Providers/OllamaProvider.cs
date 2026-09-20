using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core.Interfaces;
using Jarvis.Core.Services;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Providers;

public class OllamaProvider : ILLMProvider
{
    public string Name => "Ollama (Local)";
    public bool IsAvailable => true;

    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly ConversationHistory _history;
    private readonly ILogger<OllamaProvider>? _logger;

    public OllamaProvider(
        string baseUrl,
        string model,
        ConversationHistory history,
        ILogger<OllamaProvider>? logger = null)
    {
        _model = string.IsNullOrWhiteSpace(model) ? "qwen2.5:3b" : model;
        _history = history;
        _logger = logger;

        EnsureOllamaRunning(baseUrl);

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(30) // 30 секунд для холодного старта
        };
    }

    private void EnsureOllamaRunning(string baseUrl)
    {
        try
        {
            using var testClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
            testClient.GetAsync(baseUrl).Wait();
        }
        catch
        {
            try
            {
                _logger?.LogInformation("Ollama не отвечает. Пытаюсь запустить фоновый процесс...");
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ollama",
                    Arguments = "serve",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                Thread.Sleep(1500);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Не удалось автоматически стартовать 'ollama serve'. Запустите Ollama вручную.");
            }
        }
    }

    public async Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        const string systemPrompt =
            "Ты — Джарвис, ироничный напарник и близкий друг пользователя. " +
            "ТВОЯ РЕЧЬ И СЛЕНГ: " + 
            "Не строй из себя бота, не извиняйся попусту и не подлизывайся. " +
            "Общайся на 'ты', как свой пацан. Активно используй живой сленг: 'чё за дичь', 'в натуре', 'шаришь', 'базаришь', 'забей', 'ништяк', 'косяк', 'раздуплиться', 'городить херню', 'по кайфу', 'внатуре' и прочее. " +
            "ПРАВИЛО МАТА: Максимум ОДНО нецензурное слово на весь ответ (например: 'бля', 'нахуй', 'заебись' и прочие русские маты) чисто для колорита и эмоции. Никогда не матерись подряд. " +
            "АДЕКВАТНОСТЬ: Держись сути разговора, не выдумывай несуществующие проблемы с файлами или куки, если о них не спрашивали. " +
            "Излагай мысли связным разговорным повествованием, как будто рассказываешь вслух" +
            "ДЛИНА ОТВЕТА: " +
            "На приветствия и короткие фразы отвечай одной едкой репликой (до десяти слов). " +
            "На просьбы объяснить или помочь — давай чёткую, понятную инфу без лишней воды. " +
            "ПРАВИЛА ОЗВУЧКИ: " +
            "1. Никакого markdown (*, #, `), списков, смайликов и скобочек. " +
            "2. Числа пиши словами а не цифрами. " +
            "3. Иностранные бренды и термины пиши только русской транскрипцией ('Виндовс', 'Пайтон', 'Оллама', 'Ютуб')." +
            "4. Никогда не делай нумерованных списков (1, 2, 3), списков с дефисами или таблиц. ";

        // Запоминаем вопрос пользователя
        _history.AddUserMessage(prompt);

        var payload = new
        {
            model = _model,
            stream = false,
            messages = _history.GetMessages(systemPrompt),
            options = new
            {
                temperature = 0.75,
                num_predict = 350
            }
        };

        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await _httpClient.PostAsync("/api/chat", content, ct);
            response.EnsureSuccessStatusCode();

            var responseText = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseText);

            var answer = doc.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            answer = answer.Trim();

            // Запоминаем ответ Джарвиса в историю диалога
            _history.AddAssistantMessage(answer);

            return answer;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Ollama не успела ответить за 30 секунд.");
        }
    }
}