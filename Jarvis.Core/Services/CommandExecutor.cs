using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Services;

public class CommandExecutor
{
    private readonly ILogger<CommandExecutor> _logger;
    private readonly HttpClient _http;

    // ИСПРАВЛЕНО: Именительный падеж для API
    private const double DEFAULT_LAT = 59.9343;
    private const double DEFAULT_LON = 30.3351;
    private const string DEFAULT_CITY_NAME = "Санкт-Петербург";

    public CommandExecutor(ILogger<CommandExecutor> logger)
    {
        _logger = logger;
        _http = new HttpClient();
    }

    public bool TryExecute(string query, out string? result)
    {
        result = null;
        var lowerQuery = query.ToLowerInvariant();

        // 1. Проверка погоды
        if (lowerQuery.Contains("погода") || lowerQuery.Contains("температура") ||
            lowerQuery.Contains("дождь") || lowerQuery.Contains("прогноз") || lowerQuery.Contains("градус"))
        {
            string cityName = ExtractCityName(query);
            _logger.LogInformation("Попытка узнать погоду для города: '{City}'", cityName);

            result = GetWeatherForCityAsync(cityName).GetAwaiter().GetResult();
            return true;
        }

        // 2. Открытие программ 
        if (lowerQuery.Contains("открой") || lowerQuery.Contains("запусти"))
        {
            if (lowerQuery.Contains("браузер") || lowerQuery.Contains("chrome"))
                return LaunchApp("chrome.exe", "Браузер открыт", out result);

            if (lowerQuery.Contains("блокнот") || lowerQuery.Contains("notepad"))
                return LaunchApp("notepad.exe", "Блокнот открыт", out result);

            if (lowerQuery.Contains("vs code") || lowerQuery.Contains("код"))
                return LaunchApp("code.cmd", "VS Code запущен", out result);
        }

        // 3. Системные команды
        if (lowerQuery.Contains("который час") || lowerQuery.Contains("время"))
        {
            result = $"Сейчас {DateTime.Now:HH:mm}";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Улучшенный экстрактор названия города
    /// </summary>
    private string ExtractCityName(string query)
    {
        var lower = query.ToLowerInvariant();
        string[] triggers = { "в ", "на ", "для ", "про " };

        foreach (var trigger in triggers)
        {
            int index = lower.IndexOf(trigger);
            if (index != -1)
            {
                // Берем подстроку после триггера
                string potentialCity = query.Substring(index + trigger.Length);

                // Очищаем от мусора: знаки препинания, пробелы в конце
                char[] trimChars = { '?', '!', '.', ',', ' ', '\t', '\n' };
                potentialCity = potentialCity.Trim(trimChars);

                // Если после очистки осталось что-то осмысленное
                if (potentialCity.Length > 1)
                    return potentialCity;
            }
        }

        return DEFAULT_CITY_NAME;
    }

    private async Task<string> GetWeatherForCityAsync(string cityName)
    {
        try
        {
            // ШАГ 1: Геокодинг
            // Используем Uri.EscapeDataString для безопасной передачи кириллицы
            var geoUrl = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(cityName)}&count=1&language=ru&format=json";

            _logger.LogDebug("Запрос геокодинга: {Url}", geoUrl);

            var geoResponse = await _http.GetAsync(geoUrl);

            if (!geoResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Геокодинг API вернул ошибку: {Code}", geoResponse.StatusCode);
                return $"Не удалось найти город '{cityName}' на карте.";
            }

            var geoData = await geoResponse.Content.ReadFromJsonAsync<JsonElement>();

            double lat, lon;
            string resolvedCityName;

            // Безопасная проверка наличия результатов
            if (geoData.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
            {
                var firstResult = results[0];
                lat = firstResult.GetProperty("latitude").GetDouble();
                lon = firstResult.GetProperty("longitude").GetDouble();

                // Берем официальное название из ответа API (оно всегда в именительном падеже)
                resolvedCityName = firstResult.GetProperty("name").GetString() ?? cityName;

                // Можно добавить страну, если нужно, но для краткости оставим только город
                // if (firstResult.TryGetProperty("country", out var countryProp)) ...
            }
            else
            {
                _logger.LogWarning("Город '{City}' не найден в базе Open-Meteo", cityName);
                // Фоллбэк на СПб
                lat = DEFAULT_LAT;
                lon = DEFAULT_LON;
                resolvedCityName = DEFAULT_CITY_NAME;
            }

            // ШАГ 2: Погода
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var weatherUrl = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(culture)}&longitude={lon.ToString(culture)}&current=temperature_2m,weather_code,wind_speed_10m&timezone=auto";

            _logger.LogDebug("Запрос погоды: {Url}", weatherUrl);

            var weatherResponse = await _http.GetAsync(weatherUrl);
            weatherResponse.EnsureSuccessStatusCode();

            var weatherData = await weatherResponse.Content.ReadFromJsonAsync<JsonElement>();

            // Безопасный доступ к свойствам
            if (weatherData.TryGetProperty("current", out var current))
            {
                var temp = current.GetProperty("temperature_2m").GetDouble();
                var wind = current.GetProperty("wind_speed_10m").GetDouble();
                var weatherCode = current.GetProperty("weather_code").GetInt32();

                var weatherDesc = DecodeWeatherCode(weatherCode);

                return $"В {resolvedCityName} сейчас {temp:F1}°C, {weatherDesc}. Ветер {wind:F1} м/с.";
            }

            return "Получил данные, но не смог их расшифровать.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Критическая ошибка при получении погоды для {City}", cityName);
            return "Извини, произошла ошибка при запросе погоды. Попробуй позже.";
        }
    }

    private bool LaunchApp(string fileName, string successMessage, out string? result)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName) { UseShellExecute = true });
            _logger.LogInformation("Выполнена команда: запуск {App}", fileName);
            result = successMessage;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось запустить {App}", fileName);
            result = $"Ошибка: не удалось открыть {fileName}.";
            return true;
        }
    }

    private string DecodeWeatherCode(int code)
    {
        return code switch
        {
            0 => "ясно",
            1 or 2 or 3 => "переменная облачность",
            45 or 48 => "туман",
            51 or 53 or 55 => "морось",
            61 or 63 or 65 => "дождь",
            71 or 73 or 75 => "снег",
            80 or 81 or 82 => "ливень",
            95 or 96 or 99 => "гроза",
            _ => "неизвестная погода"
        };
    }
}