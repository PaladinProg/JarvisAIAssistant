using Jarvis.Core.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using System.Diagnostics;
using System.Speech.Synthesis;
using Whisper.net;

namespace Jarvis.Server.Services;

public class VoiceService : IDisposable
{
    // ==========================================
    // 👇 НАСТРОЙКИ ГОЛОСА И МОДЕЛЕЙ 👇
    // ==========================================

    // Имя голоса Piper (без расширения .onnx)
    // Варианты: "ru_RU-irina-medium", "ru_RU-ruslan-medium" и т.д. нихуя не работает пока что, ищем другую голосовую модель
    private const string SELECTED_VOICE_NAME = "ru_RU-denis-medium////";

    // Имя модели Whisper для распознавания речи
    private const string WHISPER_MODEL_FILE = "ggml-small-q5_1.bin";

    // ==========================================

    private readonly SmartRouter _router;
    private readonly ILogger<VoiceService> _logger;
    private readonly SpeechSynthesizer _fallbackSynthesizer;

    // Пути к файлам
    private readonly string _piperExePath;
    private readonly string _voiceModelPath;
    private readonly string _voiceConfigPath;
    private readonly string _whisperModelPath;

    // Состояние
    private bool _isListening = false;

    public VoiceService(SmartRouter router, ILogger<VoiceService> logger, IConfiguration config)
    {
        _router = router;
        _logger = logger;
        _fallbackSynthesizer = new SpeechSynthesizer();

        // Поднимаемся на 4 уровня вверх: net8.0 -> Debug -> bin -> Jarvis.Server -> Jarvis
        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

        var piperRoot = Path.Combine(solutionRoot, "Tools", "Piper");
        var modelsRoot = Path.Combine(solutionRoot, "Models");

        // Формируем пути
        _piperExePath = Path.Combine(piperRoot, "piper.exe");
        _voiceModelPath = Path.Combine(piperRoot, "Voices", $"{SELECTED_VOICE_NAME}.onnx");
        _voiceConfigPath = Path.Combine(piperRoot, "Voices", $"{SELECTED_VOICE_NAME}.onnx.json");
        _whisperModelPath = Path.Combine(modelsRoot, WHISPER_MODEL_FILE);

        // Проверки
        if (!File.Exists(_piperExePath))
            _logger.LogWarning("Piper.exe не найден: {Path}", _piperExePath);

        if (!File.Exists(_voiceModelPath))
            _logger.LogWarning("Модель голоса не найдена: {Path}", _voiceModelPath);

        if (!File.Exists(_whisperModelPath))
            _logger.LogError("Модель Whisper не найдена: {Path}", _whisperModelPath);
    }

    public async Task StartListeningAsync(CancellationToken ct)
    {
        _logger.LogInformation("Голосовой режим активирован. Говорите 'выход' для остановки.");
        _isListening = true;

        while (_isListening && !ct.IsCancellationRequested)
        {
            try
            {
                Console.WriteLine("🎤 Слушаю... (говорите)");

                // Записываем 5 секунд аудио
                var audioBytes = await RecordAudioAsync(5000, ct);

                if (audioBytes.Length == 0) continue;

                // Распознаем речь
                var text = await TranscribeAsync(audioBytes, ct);

                if (string.IsNullOrWhiteSpace(text))
                {
                    Console.WriteLine("⚠️ Ничего не разобрал.");
                    continue;
                }

                Console.WriteLine($"👤 Ты сказал: {text}");

                // Проверка на выход
                if (text.Contains("выход", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("стоп", StringComparison.OrdinalIgnoreCase))
                {
                    _isListening = false;
                    break;
                }

                // Обрабатываем запрос
                var response = await _router.AskAsync(text, ct);
                Console.WriteLine($"🤖 Джарвис: {response}");

                // Озвучиваем ответ
                await SpeakAsync(response, ct);

            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка в голосовом цикле");
                await Task.Delay(2000, ct);
            }
        }

        _logger.LogInformation("Голосовой режим остановлен.");
    }

    private async Task<byte[]> RecordAudioAsync(int durationMs, CancellationToken ct)
    {
        using var ms = new MemoryStream();

        using var waveIn = new WaveInEvent();
        waveIn.WaveFormat = new WaveFormat(16000, 16, 1); // 16kHz, 16bit, Mono

        waveIn.DataAvailable += (s, e) =>
        {
            ms.Write(e.Buffer, 0, e.BytesRecorded);
        };

        waveIn.StartRecording();

        try
        {
            await Task.Delay(durationMs, ct);
        }
        catch (OperationCanceledException) { }

        waveIn.StopRecording();
        return ms.ToArray();
    }

    private async Task<string> TranscribeAsync(byte[] audioData, CancellationToken ct)
    {
        // 1. Создаем корректный WAV-файл в памяти
        byte[] wavBytes;
        using (var tempStream = new MemoryStream())
        {
            using (var writer = new WaveFileWriter(tempStream, new WaveFormat(16000, 16, 1)))
            {
                writer.Write(audioData, 0, audioData.Length);
            } // Writer закрывается и дописывает заголовок WAV
            wavBytes = tempStream.ToArray();
        }

        // 2. Передаем WAV в Whisper
        using var readStream = new MemoryStream(wavBytes);

        using var whisperFactory = WhisperFactory.FromPath(_whisperModelPath);

        using var processor = whisperFactory.CreateBuilder()
            .WithLanguage("russian")
            .Build();

        string result = "";
        await foreach (var segment in processor.ProcessAsync(readStream, ct))
        {
            result += segment.Text + " ";
        }

        return result.Trim();
    }

    private async Task SpeakAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // Проверка путей
        if (!File.Exists(_piperExePath) || !File.Exists(_voiceModelPath))
        {
            _logger.LogWarning("Piper недоступен (exe или модель не найдены). Использую системный TTS.");
            try { _fallbackSynthesizer.Speak(text); } catch { }
            return;
        }

        try
        {
            _logger.LogInformation("Озвучиваю через Piper: {Voice}", SELECTED_VOICE_NAME);

            // Аргументы
            var args = $"--model \"{_voiceModelPath}\" --config \"{_voiceConfigPath}\" --output-raw";

            using var process = new Process();
            process.StartInfo.FileName = _piperExePath;
            process.StartInfo.Arguments = args;

            // ВАЖНО: Указываем рабочую директорию, чтобы Piper нашел свои DLL
            process.StartInfo.WorkingDirectory = Path.GetDirectoryName(_piperExePath);

            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.CreateNoWindow = true;

            process.Start();

            // Отправляем текст
            await process.StandardInput.WriteAsync(text);
            process.StandardInput.Close();

            // Читаем аудио
            using var audioStream = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(audioStream, ct);

            // Воспроизведение
            audioStream.Position = 0;

            using var waveOut = new WaveOutEvent();
            // Piper выдает raw PCM 16kHz 16bit mono
            using var rawSource = new RawSourceWaveStream(audioStream, new WaveFormat(16000, 16, 1));

            waveOut.Init(rawSource);
            waveOut.Play();

            // Ждем окончания
            while (waveOut.PlaybackState == PlaybackState.Playing && !ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct);
            }

            // Даем процессу время корректно завершиться
            if (!process.HasExited)
                process.WaitForExit(2000);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка озвучки через Piper. Переключаюсь на системный голос.");
            try { _fallbackSynthesizer.Speak(text); } catch { }
        }


    }
    public void Dispose()
    {
        _fallbackSynthesizer?.Dispose();
    }
}