using System.Collections.Concurrent;

namespace Jarvis.Core.Services;

public class ChatMessage
{
    public string Role { get; set; } = "user"; // "system", "user", "assistant"
    public string Content { get; set; } = string.Empty;
}

public class ConversationHistory
{
    private readonly List<ChatMessage> _messages = new();
    private readonly object _lock = new();
    private const int MaxHistory = 10; // Помним последние 5 вопросов и 5 ответов

    public void AddUserMessage(string text)
    {
        lock (_lock)
        {
            _messages.Add(new ChatMessage { Role = "user", Content = text });
            Trim();
        }
    }

    public void AddAssistantMessage(string text)
    {
        lock (_lock)
        {
            _messages.Add(new ChatMessage { Role = "assistant", Content = text });
            Trim();
        }
    }

    public List<ChatMessage> GetMessages(string systemPrompt)
    {
        lock (_lock)
        {
            var result = new List<ChatMessage>
            {
                new ChatMessage { Role = "system", Content = systemPrompt }
            };
            result.AddRange(_messages);
            return result;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _messages.Clear();
        }
    }

    private void Trim()
    {
        while (_messages.Count > MaxHistory)
        {
            _messages.RemoveAt(0);
        }
    }
}