using Jarvis.Core;
using Jarvis.Core.Routing;
using Jarvis.Server.Services; // Не забудь добавить using для VoiceService
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// 1. Создаем и настраиваем хост
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile("appsettings.json", optional: false);
builder.Configuration.AddUserSecrets<Program>();
builder.Services.AddJarvisCore(builder.Configuration);

var app = builder.Build();

// 2. Получаем ВСЕ необходимые сервисы из контейнера
// Это гарантирует, что типы (особенно ILogger<T>) будут правильными
var router = app.Services.GetRequiredService<SmartRouter>();
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var config = app.Services.GetRequiredService<IConfiguration>(); // <-- Исправление ошибки CS1061
var voiceLogger = app.Services.GetRequiredService<ILogger<VoiceService>>(); // <-- Исправление ошибки CS1503

Console.WriteLine("Джарвис запущен!");
Console.WriteLine("Выберите режим:");
Console.WriteLine("1. Текстовый (консоль)");
Console.WriteLine("2. Голосовой");
Console.Write("Ваш выбор: ");

var choice = Console.ReadLine();

if (choice == "2")
{
    try
    {
        // Запускаем голосовой сервис
        // Теперь передаем правильные зависимости
        using var voiceService = new VoiceService(router, voiceLogger, config);

        // Получаем токен отмены для корректного завершения
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        await voiceService.StartListeningAsync(lifetime.ApplicationStopping);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ошибка запуска голосового режима: {ex.Message}");
        Console.WriteLine("Проверьте, что модель Whisper лежит в папке Models/ggml-base.bin");
    }
}
else
{
    // Старый текстовый режим
    Console.WriteLine("Текстовый режим. Введи 'exit' для выхода:");
    while (true)
    {
        Console.Write("\n Ты: ");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input) || input.ToLower() == "exit") break;
        var response = await router.AskAsync(input);
        Console.WriteLine($"Джарвис: {response}");
    }
}