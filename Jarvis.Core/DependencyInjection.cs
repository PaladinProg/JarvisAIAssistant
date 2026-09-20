using Jarvis.Core.Interfaces;
using Jarvis.Core.Providers;
using Jarvis.Core.Routing;
using Jarvis.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddJarvisCore(this IServiceCollection services, IConfiguration config)
    {
        // 1. Регистрируем исполнителя команд
        services.AddSingleton<CommandExecutor>();

        // 2. Создаем экземпляры провайдеров ВРУЧНУЮ, чтобы точно знать, кто есть кто
        var ollamaProvider = new OllamaProvider(
            config["Ollama:BaseUrl"] ?? "http://localhost:11434",
            config["Ollama:Model"] ?? "qwen2.5:3b"
        );

        var groqProvider = new GroqProvider(
            config["Groq:ApiKey"] ?? ""
        );

        // 3. Регистрируем их и как конкретные типы, и как интерфейсы (на всякий случай)
        services.AddSingleton(ollamaProvider);
        services.AddSingleton(groqProvider);

        // Также регистрируем как ILLMProvider, если где-то в будущем понадобится полиморфизм
        services.AddSingleton<ILLMProvider>(ollamaProvider);
        services.AddSingleton<ILLMProvider>(groqProvider);

        // 4. Регистрируем Роутер, передавая ему конкретные экземпляры
        services.AddSingleton<SmartRouter>(sp =>
            new SmartRouter(
                sp.GetRequiredService<CommandExecutor>(),
                sp.GetRequiredService<OllamaProvider>(), // Теперь контейнер знает этот тип!
                sp.GetRequiredService<GroqProvider>(),
                sp.GetRequiredService<ILogger<SmartRouter>>()
            ));

        return services;
    }
}