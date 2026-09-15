using System.Text;

namespace ClaudeStatusBar.Data;

public enum FrameEventKind { Line, Overflow }

/// <summary>One completed record (Line) or one discarded oversized record (Overflow).</summary>
public readonly record struct FrameEvent(FrameEventKind Kind, string? Line)
{
    public static FrameEvent ForLine(string line) => new(FrameEventKind.Line, line);
    public static readonly FrameEvent Overflow = new(FrameEventKind.Overflow, null);
}

/// <summary>
/// Bounded incremental line framing for the claude.exe stdout/stderr pipes (Codex
/// review High #7): plain TextReader.ReadLine accumulates an unterminated or
/// pathologically long line without any limit, which is unbounded memory growth
/// that can take the whole app down. This is pure and I/O-free so it is directly
/// unit-testable: feed it character chunks exactly as read from the pipe (any
/// chunk size, split anywhere, including mid-record) and it yields one FrameEvent
/// per completed line or per discarded oversized record, in the order they occur.
///
/// A record exceeding MaxRecordChars switches to a "discarding" state that stops
/// buffering and consumes (without retaining) every further character up to and
/// including the next '\n', then yields one Overflow event and resumes normal
/// framing. A producer that never emits a newline at all is still bounded to
/// MaxRecordChars of retained memory, never unbounded growth.
/// </summary>
public sealed class LineFramer
{
    /// <summary>
    /// 4M UTF-16 chars: the task specifies a 4MB-per-record cap for what is,
    /// on this wire protocol, near-always ASCII/UTF-8 JSON, so treating "char count"
    /// as the byte-count proxy is conservative on this workload without needing to
    /// re-encode every character just to measure it.
    /// </summary>
    public const int MaxRecordChars = 4 * 1024 * 1024;

    readonly StringBuilder _buffer = new();
    bool _discarding;

    public IReadOnlyList<FrameEvent> Feed(ReadOnlySpan<char> chunk)
    {
        List<FrameEvent>? events = null;
        foreach (char c in chunk)
        {
            if (c == '\n')
            {
                if (_discarding)
                {
                    (events ??= new List<FrameEvent>()).Add(FrameEvent.Overflow);
                    _discarding = false;
                }
                else
                {
                    string line = _buffer.Length > 0 && _buffer[^1] == '\r'
                        ? _buffer.ToString(0, _buffer.Length - 1)
                        : _buffer.ToString();
                    (events ??= new List<FrameEvent>()).Add(FrameEvent.ForLine(line));
                }
                _buffer.Clear();
                continue;
            }

            if (_discarding) continue;

            _buffer.Append(c);
            if (_buffer.Length > MaxRecordChars)
            {
                _discarding = true;
                _buffer.Clear();
            }
        }
        return (IReadOnlyList<FrameEvent>?)events ?? Array.Empty<FrameEvent>();
    }
}
