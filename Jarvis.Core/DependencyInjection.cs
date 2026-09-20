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
        //Единая история диалога (память контекста)
        services.AddSingleton<ConversationHistory>();

        //Исполнитель системных команд C#
        services.AddSingleton<CommandExecutor>();

        // 3. Локальный провайдер Ollama (с памятью диалога)
        services.AddSingleton(sp => new OllamaProvider(
            config["Ollama:BaseUrl"] ?? "http://localhost:11434",
            config["Ollama:Model"] ?? "qwen2.5:3b",
            sp.GetRequiredService<ConversationHistory>(),
            sp.GetService<ILogger<OllamaProvider>>()
        ));

        //Облачный провайдер OpenRouter
        services.AddSingleton(sp => new CloudLLMProvider(
            config["OpenRouter:ApiKey"] ?? "",
            config["OpenRouter:Model"] ?? "deepseek/deepseek-chat:free",
            sp.GetService<ILogger<CloudLLMProvider>>()
        ));

        // 5. Умный маршрутизатор (выбирает команды, локалку или облако)
        services.AddSingleton(sp => new SmartRouter(
            sp.GetRequiredService<CommandExecutor>(),
            sp.GetRequiredService<OllamaProvider>(),
            sp.GetRequiredService<CloudLLMProvider>(),
            sp.GetRequiredService<ILogger<SmartRouter>>()
        ));

        return services;
    }
}