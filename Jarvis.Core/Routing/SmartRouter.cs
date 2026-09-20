using Jarvis.Core.Interfaces;
using Jarvis.Core.Providers;
using Jarvis.Core.Services;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Routing;

public class SmartRouter
{
    private readonly CommandExecutor _executor;
    private readonly ILLMProvider _local;
    private readonly ILLMProvider _cloud;
    private readonly ILogger<SmartRouter> _logger;

    public SmartRouter(
    CommandExecutor executor,
    OllamaProvider local,      // Конкретный тип, а не интерфейс
    GroqProvider cloud,        // Конкретный тип
    ILogger<SmartRouter> logger)
    {
        _executor = executor;
        _local = local;
        _cloud = cloud;
        _logger = logger;
    }
    public async Task<string> AskAsync(string query, CancellationToken ct = default)
    {
        // 1. Сначала пробуем выполнить как команду (C# код)
        if (_executor.TryExecute(query, out var commandResult))
        {
            _logger.LogInformation("Команда выполнена локально: {Result}", commandResult);
            return commandResult!;
        }

        // 2. Если не команда — решаем, какая модель нужна
        // Для простоты пока отправляем всё сложное в облако, а простое локально
        // (можно усложнить логику позже)
        var provider = query.Length > 100 ? _cloud : _local;

        if (!provider.IsAvailable) provider = _local; // Fallback

        _logger.LogInformation("Запрос к LLM: {Provider}", provider.Name);

        try
        {
            return await provider.GenerateAsync(query, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка LLM {Provider}", provider.Name);
            return "Извини, мой мозг сейчас недоступен.";
        }
    }
}