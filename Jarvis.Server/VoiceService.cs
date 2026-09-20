using Jarvis.Core.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using System.Diagnostics;
using Whisper.net;

namespace Jarvis.Server.Services;

public class VoiceService : IDisposable
{
    private const string WHISPER_MODEL_FILE = "ggml-small-q5_1.bin";
    private const string TTS_URL = "http://127.0.0.1:8008/tts";
    private const string SPEAKER = "aidar"; // Мужской голос для Джарвиса (или "eugene", "kseniya")

    private readonly SmartRouter _router;
    private readonly ILogger<VoiceService> _logger;
    private readonly WhisperFactory? _whisperFactory;
    private readonly HttpClient _httpClient;
    private Process? _sileroProcess;
    private bool _isListening = false;

    public VoiceService(SmartRouter router, ILogger<VoiceService> logger, IConfiguration config)
    {
        _router = router;
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

        // 1. Автозапуск локального сервера Silero в фоне (если он еще не запущен)
        StartSileroServer(solutionRoot);

        // 2. Инициализация модели Whisper
        var whisperModelPath = Path.Combine(solutionRoot, "Models", WHISPER_MODEL_FILE);
        if (File.Exists(whisperModelPath))
        {
            _whisperFactory = WhisperFactory.FromPath(whisperModelPath);
        }
        else
        {
            _logger.LogError("Модель Whisper не найдена: {Path}", whisperModelPath);
        }
    }

    private void StartSileroServer(string solutionRoot)
    {
        var scriptPath = Path.Combine(solutionRoot, "Silero", "tts_server.py");
        if (!File.Exists(scriptPath))
        {
            _logger.LogWarning("Скрипт Silero не найден: {Path}. Запустите его вручную.", scriptPath);
            return;
        }

        try
        {
            // Проверяем, не запущен ли он уже
            using var testClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
            testClient.GetAsync(TTS_URL).Wait();
        }
        catch
        {
            // Не запущен — стартуем автоматически в фоне
            _logger.LogInformation("Запускаю локальный сервер Silero TTS...");
            _sileroProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"\"{scriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            _sileroProcess.Start();
            Thread.Sleep(2000); // Даем пару секунд на старт модели
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
                    await SpeakAsync("Отключаюсь, сэр.", ct);
                    break;
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

        try
        {
            // 1. Запрашиваем WAV у локального Silero
            var url = $"{TTS_URL}?text={Uri.EscapeDataString(text)}&speaker={SPEAKER}";
            var wavBytes = await _httpClient.GetByteArrayAsync(url, ct);

            // 2. Воспроизводим через NAudio
            using var stream = new MemoryStream(wavBytes);
            using var reader = new WaveFileReader(stream);
            using var waveOut = new WaveOutEvent();

            waveOut.Init(reader);
            waveOut.Play();

            while (waveOut.PlaybackState == PlaybackState.Playing && !ct.IsCancellationRequested)
            {
                await Task.Delay(50, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка озвучки Silero TTS");
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
        const int SilenceTimeoutMs = 800; // Пауза после слов
        const int MaxRecordDurationMs = 12000;//Макс время на разговорчики
        const int NoSpeechTimeoutMs = 6000;//Если молчишь 6 сек - перезапуск

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

    // Вспомогательный расчет среднеквадратичной громкости (RMS)
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