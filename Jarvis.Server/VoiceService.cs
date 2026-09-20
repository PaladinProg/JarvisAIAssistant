using Jarvis.Core.Routing;
using Jarvis.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;

namespace Jarvis.Server;

public class VoiceService : IDisposable
{
    private readonly SmartRouter _router;
    private readonly ILogger<VoiceService> _logger;
    private readonly IConfiguration _config;
    private readonly HttpClient _httpClient = new();
    private readonly ConversationHistory _history;

    private WhisperFactory? _whisperFactory;
    private Process? _sileroProcess;
    private bool _isListening;

    // === ГРОМКОСТЬ РЕЧИ ДЖАРВИСА ===
    // Значение от 0.0f (тишина) до 1.0f (максимум). 0.65f — комфортные 65%.
    private float _volume = 0.65f;

    private const string TTS_URL = "http://127.0.0.1:8008/tts";
    private const string SPEAKER = "aidar";

    public VoiceService(SmartRouter router, ILogger<VoiceService> logger, IConfiguration config, ConversationHistory history)
    {
        _router = router;
        _logger = logger;
        _config = config;
        _history = history;

        // 1. Считываем громкость из appsettings.json секции "Voice:Volume" (если есть)
        if (float.TryParse(config["Voice:Volume"], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float cfgVolume))
        {
            _volume = Math.Clamp(cfgVolume, 0.0f, 1.0f);
        }

        InitWhisper();
        CheckOrStartSileroServer();
    }

    /// <summary>
    /// Возможность менять громкость на лету (например, из голосовой команды «сделай потише»)
    /// </summary>
    public void SetVolume(float volume)
    {
        _volume = Math.Clamp(volume, 0.0f, 1.0f);
        _logger.LogInformation("Громкость Джарвиса установлена на {Vol:P0}", _volume);
    }

    private void InitWhisper()
    {
        // Проверяем возможные места расположения модели:
        var possiblePaths = new[]
        {
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "ggml-small-q5_1.bin"),
        Path.Combine(Directory.GetCurrentDirectory(), "Models", "ggml-small-q5_1.bin"),
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Models", "ggml-small-q5_1.bin"), // Папка Jarvis.Server
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Models", "ggml-small-q5_1.bin") // Корень D:\AI\Jarvis
    };

        string? modelPath = null;
        foreach (var p in possiblePaths)
        {
            var fullPath = Path.GetFullPath(p);
            if (File.Exists(fullPath))
            {
                modelPath = fullPath;
                break;
            }
        }

        if (modelPath != null)
        {
            _whisperFactory = WhisperFactory.FromPath(modelPath);
            _logger.LogInformation("Whisper успешно загружен из: {Path}", modelPath);
        }
        else
        {
            _logger.LogWarning("Файл модели Whisper не найден ни по одному из стандартных путей.");
        }
    }

    private void CheckOrStartSileroServer()
    {
        var possibleScripts = new[]
        {
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Silero", "tts_server.py"),
        Path.Combine(Directory.GetCurrentDirectory(), "Silero", "tts_server.py"),
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Silero", "tts_server.py"), // Папка Jarvis.Server
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Silero", "tts_server.py") // Корень D:\AI\Jarvis
    };

        string? scriptPath = null;
        foreach (var p in possibleScripts)
        {
            var fullPath = Path.GetFullPath(p);
            if (File.Exists(fullPath))
            {
                scriptPath = fullPath;
                break;
            }
        }

        if (scriptPath == null)
        {
            _logger.LogError("Скрипт Silero не найден. Проверьте расположение папки Silero.");
            return;
        }

        try
        {
            using var testClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
            testClient.GetAsync(TTS_URL).Wait();
            _logger.LogInformation("Silero TTS сервер уже запущен.");
        }
        catch
        {
            _logger.LogInformation("Запускаю локальный сервер Silero TTS: {Path}...", scriptPath);
            _sileroProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"\"{scriptPath}\"",
                    WorkingDirectory = Path.GetDirectoryName(scriptPath), // Важно для относительных путей в python
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            _sileroProcess.Start();
            Thread.Sleep(2500); // Даем 2.5 сек на прогрев Silero
        }
    }

    public async Task StartListeningAsync(CancellationToken ct)
    {
        _logger.LogInformation("Голосовой режим активирован. Говори 'выход' или 'стоп' для остановки.");
        _isListening = true;

        while (_isListening && !ct.IsCancellationRequested)
        {
            try
            {
                Console.WriteLine("\nСлушаю... (говорите)");
                var audioBytes = await RecordAudioWithVadAsync(ct);
                if (audioBytes.Length == 0) continue;

                var text = await TranscribeAsync(audioBytes, ct);
                if (string.IsNullOrWhiteSpace(text))
                {
                    Console.WriteLine("Ничего не разобрал.");
                    continue;
                }

                Console.WriteLine($"Ты: {text}");

                if (text.Contains("выход", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("стоп", StringComparison.OrdinalIgnoreCase))
                    {
                        _isListening = false;
                        await SpeakAsync("До связи, братанчик.", ct);
                        break;
                    }

                if (text.Contains("забудь", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("сбрось контекст", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("очисти память", StringComparison.OrdinalIgnoreCase))
                    {
                        _history.Clear(); // Очищаем историю
                        await SpeakAsync("Всё, проехали. О чём базар?", ct);
                        continue;
                    }

                var response = await _router.AskAsync(text, ct);
                Console.WriteLine($"Джарвис: {response}");
                await SpeakAsync(response, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка в голосовом цикле");
                await Task.Delay(1500, ct);
            }
        }
    }

    private async Task<string> TranscribeAsync(byte[] audioData, CancellationToken ct)
    {
        if (_whisperFactory == null) return string.Empty;

        byte[] wavBytes;
        using (var tempStream = new MemoryStream())
        {
            using (var writer = new WaveFileWriter(tempStream, new WaveFormat(16000, 16, 1)))
            {
                writer.Write(audioData, 0, audioData.Length);
            }
            wavBytes = tempStream.ToArray();
        }

        using var readStream = new MemoryStream(wavBytes);
        using var processor = _whisperFactory.CreateBuilder()
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

        // 1. Очищаем текст от markdown-мусора, скобок и спецсимволов перед синтезом
        var cleanText = text
            .Replace("*", "")
            .Replace("#", "")
            .Replace("`", "")
            .Replace("\"", "")
            .Replace("(", "")
            .Replace(")", "");

        // 2. Разбиваем длинный текст на предложения
        var sentences = cleanText.Split(new[] { '.', '!', '?', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        var currentChunk = new StringBuilder();

        foreach (var rawSentence in sentences)
        {
            var sentence = rawSentence.Trim();
            if (string.IsNullOrEmpty(sentence)) continue;

            // Если текущий кусок + новое предложение меньше 250 символов — копим
            if (currentChunk.Length + sentence.Length < 250)
            {
                currentChunk.Append(sentence).Append(". ");
            }
            else
            {
                // Отправляем накопленный кусок в Silero
                if (currentChunk.Length > 0)
                {
                    await PlayAudioChunkAsync(currentChunk.ToString().Trim(), ct);
                    currentChunk.Clear();
                }
                currentChunk.Append(sentence).Append(". ");
            }
        }

        // Доозвучиваем остаток
        if (currentChunk.Length > 0)
        {
            await PlayAudioChunkAsync(currentChunk.ToString().Trim(), ct);
        }
    }

    private async Task PlayAudioChunkAsync(string chunk, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(chunk)) return;

        try
        {
            var url = $"{TTS_URL}?text={Uri.EscapeDataString(chunk)}&speaker={SPEAKER}";
            var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var audioBytes = await response.Content.ReadAsByteArrayAsync(ct);

            using var ms = new MemoryStream(audioBytes);
            using var reader = new WaveFileReader(ms);

            // Применяем громкость
            var sampleProvider = reader.ToSampleProvider();
            var volumeProvider = new VolumeSampleProvider(sampleProvider) { Volume = _volume };

            using var waveOut = new WaveOutEvent();
            waveOut.Init(volumeProvider);
            waveOut.Play();

            // Ждем завершения воспроизведения кусочка
            while (waveOut.PlaybackState == PlaybackState.Playing && !ct.IsCancellationRequested)
            {
                await Task.Delay(50, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка озвучки Silero TTS для фрагмента: '{Chunk}'", chunk);
        }
    }

    private async Task<byte[]> RecordAudioWithVadAsync(CancellationToken ct)
    {
        var ms = new MemoryStream();
        var waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 50
        };

        var tcs = new TaskCompletionSource<bool>();
        bool isSpeaking = false;
        bool isStopped = false;
        var lockObj = new object();

        DateTime lastSpeechTime = DateTime.MinValue;
        DateTime recordStartTime = DateTime.Now;

        const double VoiceThreshold = 800.0;
        const int SilenceTimeoutMs = 800;    // Пауза после слов
        const int MaxRecordDurationMs = 12000; // Макс время на фразу
        const int NoSpeechTimeoutMs = 6000;   // Если молчишь 6 сек - перезапуск

        void OnDataAvailable(object? s, WaveInEventArgs e)
        {
            lock (lockObj)
            {
                if (isStopped) return;

                ms.Write(e.Buffer, 0, e.BytesRecorded);
                double rms = CalculateRms(e.Buffer, e.BytesRecorded);
                var now = DateTime.Now;

                if (rms > VoiceThreshold)
                {
                    if (!isSpeaking)
                    {
                        isSpeaking = true;
                        Console.Write(" [говорит...]");
                    }
                    lastSpeechTime = now;
                }
                else if (isSpeaking)
                {
                    if ((now - lastSpeechTime).TotalMilliseconds >= SilenceTimeoutMs)
                    {
                        isStopped = true;
                        tcs.TrySetResult(true);
                    }
                }
                else
                {
                    if ((now - recordStartTime).TotalMilliseconds >= NoSpeechTimeoutMs)
                    {
                        isStopped = true;
                        tcs.TrySetResult(false);
                    }
                }

                if ((now - recordStartTime).TotalMilliseconds >= MaxRecordDurationMs)
                {
                    isStopped = true;
                    tcs.TrySetResult(true);
                }
            }
        }

        waveIn.DataAvailable += OnDataAvailable;
        waveIn.StartRecording();

        try
        {
            using (ct.Register(() => tcs.TrySetCanceled()))
            {
                bool hasSpeech = await tcs.Task;
                lock (lockObj)
                {
                    isStopped = true;
                }
                waveIn.StopRecording();
                waveIn.DataAvailable -= OnDataAvailable;

                if (!hasSpeech && !isSpeaking)
                {
                    return Array.Empty<byte>();
                }

                return ms.ToArray();
            }
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<byte>();
        }
        finally
        {
            lock (lockObj)
            {
                isStopped = true;
            }
            waveIn.Dispose();
            ms.Dispose();
        }
    }

    private static double CalculateRms(byte[] buffer, int bytesRecorded)
    {
        long sum = 0;
        int sampleCount = bytesRecorded / 2;
        if (sampleCount == 0) return 0;

        for (int i = 0; i < bytesRecorded; i += 2)
        {
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            sum += sample * sample;
        }

        return Math.Sqrt((double)sum / sampleCount);
    }

    public void Dispose()
    {
        _whisperFactory?.Dispose();
        _httpClient?.Dispose();

        if (_sileroProcess != null && !_sileroProcess.HasExited)
        {
            try { _sileroProcess.Kill(); } catch { }
            _sileroProcess.Dispose();
        }
    }
}