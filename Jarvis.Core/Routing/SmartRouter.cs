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
    CloudLLMProvider cloud,        // Конкретный тип
    ILogger<SmartRouter> logger)
    {
        _executor = executor;
        _local = local;
        _cloud = cloud;
        _logger = logger;
    }

    public async Task<string> AskAsync(string query, CancellationToken ct = default)
    {
        // 1. Команды Windows / C#
        if (_executor.TryExecute(query, out var commandResult))
        {
            _logger.LogInformation("Команда выполнена: {Result}", commandResult);
            return commandResult!;
        }

        // 2. Пока сидим чисто на локальной Ollama (без лишних таймаутов и задержек)
        _logger.LogInformation("Запрос к модели: {Provider}", _local.Name);

        try
        {
            // Даём локальной модели честные 35 секунд на генерацию даже самых сложных тем
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(35));

            return await _local.GenerateAsync(query, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Ollama превысила лимит времени ожидания.");
            return "Слушай, мысль слишком глубокая, я завис на тридцать секунд.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка генерации локальной модели");
            return "Извини, что-то в мозгах замкнуло.";
        }
    }
    /*public async Task<string> AskAsync(string query, CancellationToken ct = default)
    {
        // 1. Команды C# (погода, запуск программ, время)
        if (_executor.TryExecute(query, out var commandResult))
        {
            _logger.LogInformation("Команда выполнена: {Result}", commandResult);
            return commandResult!;
        }

        // 2. Интеллектуальный выбор: сложные запросы в облако, простые в Ollama
        bool isComplex = query.Length > 80 ||
                         query.Contains("почему", StringComparison.OrdinalIgnoreCase) ||
                         query.Contains("объясни", StringComparison.OrdinalIgnoreCase) ||
                         query.Contains("напиши", StringComparison.OrdinalIgnoreCase);

        var targetProvider = (isComplex && _cloud.IsAvailable) ? _cloud : _local;

        _logger.LogInformation("Запрос к модели: {Provider}", targetProvider.Name);

        try
        {
            // Ограничиваем таймаут облака до 10 секунд
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            return await targetProvider.GenerateAsync(query, timeoutCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Провайдер {Provider} не ответил. Срабатывает Fallback на Ollama!", targetProvider.Name);

            // Если падало облако — бесшовный fallback на локальную Ollama
            if (targetProvider != _local && _local.IsAvailable)
            {
                return await _local.GenerateAsync(query, ct);
            }

            return "Извини, произошла ошибка мыслительного модуля.";
        }
    }*/
}