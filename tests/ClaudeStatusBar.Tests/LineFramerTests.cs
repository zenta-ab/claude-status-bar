using ClaudeStatusBar.Data;
using Xunit;

namespace ClaudeStatusBar.Tests;

public class LineFramerTests
{
    [Fact]
    public void Feed_YieldsOneLinePerNewline()
    {
        var framer = new LineFramer();
        IReadOnlyList<FrameEvent> events = framer.Feed("hello\nworld\n");

        Assert.Equal(2, events.Count);
        Assert.Equal(FrameEventKind.Line, events[0].Kind);
        Assert.Equal("hello", events[0].Line);
        Assert.Equal("world", events[1].Line);
    }

    [Fact]
    public void Feed_HandlesCrLf()
    {
        var framer = new LineFramer();
        IReadOnlyList<FrameEvent> events = framer.Feed("hello\r\n");

        Assert.Single(events);
        Assert.Equal("hello", events[0].Line); // trailing \r stripped
    }

    [Fact]
    public void Feed_ReassemblesALineSplitAcrossMultipleChunks()
    {
        var framer = new LineFramer();
        var all = new List<FrameEvent>();
        all.AddRange(framer.Feed("hel"));
        all.AddRange(framer.Feed("lo wor"));
        all.AddRange(framer.Feed("ld\n"));

        Assert.Single(all);
        Assert.Equal("hello world", all[0].Line);
    }

    [Fact]
    public void Feed_PartialLineWithNoNewlineYet_YieldsNothing()
    {
        var framer = new LineFramer();
        IReadOnlyList<FrameEvent> events = framer.Feed("no newline yet");
        Assert.Empty(events);
    }

    [Fact]
    public void Feed_OversizedRecord_YieldsOverflow_NotTheAccumulatedText()
    {
        // Codex review High #7: an unterminated/oversized line must never grow memory without
        // bound. Feed a record well past MaxRecordChars with no newline at all -- this alone
        // proves the framer doesn't retain the oversized content once the cap is crossed.
        var framer = new LineFramer();
        string chunk = new string('x', LineFramer.MaxRecordChars + 1000);
        IReadOnlyList<FrameEvent> events = framer.Feed(chunk);

        Assert.Empty(events); // no newline yet -- framer is now in "discarding" state, buffer cleared

        IReadOnlyList<FrameEvent> after = framer.Feed("\n");
        Assert.Single(after);
        Assert.Equal(FrameEventKind.Overflow, after[0].Kind);
        Assert.Null(after[0].Line);
    }

    [Fact]
    public void Feed_ResumesNormalFramingAfterAnOverflow()
    {
        var framer = new LineFramer();
        string oversized = new string('x', LineFramer.MaxRecordChars + 1) + "\n";
        framer.Feed(oversized); // discarded, one Overflow event

        IReadOnlyList<FrameEvent> events = framer.Feed("back to normal\n");
        Assert.Single(events);
        Assert.Equal(FrameEventKind.Line, events[0].Kind);
        Assert.Equal("back to normal", events[0].Line);
    }

    [Fact]
    public void Feed_OneCharAtATime_StillReassemblesCorrectly()
    {
        var framer = new LineFramer();
        var all = new List<FrameEvent>();
        Span<char> one = stackalloc char[1];
        foreach (char c in "a\nbc\n")
        {
            one[0] = c;
            all.AddRange(framer.Feed(one));
        }

        Assert.Equal(2, all.Count);
        Assert.Equal("a", all[0].Line);
        Assert.Equal("bc", all[1].Line);
    }
}
