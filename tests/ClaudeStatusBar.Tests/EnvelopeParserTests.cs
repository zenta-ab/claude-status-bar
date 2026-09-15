using ClaudeStatusBar.Data;
using Xunit;

namespace ClaudeStatusBar.Tests;

public class EnvelopeParserTests
{
    [Fact]
    public void Parse_MatchesAWellFormedControlResponse()
    {
        string line = """{"type":"control_response","response":{"request_id":"7","five_hour":{"utilization":1}}}""";
        EnvelopeParser.EnvelopeParseResult result = EnvelopeParser.Parse(line);
        try
        {
            Assert.Equal(EnvelopeKind.ControlResponseMatched, result.Kind);
            Assert.Equal("7", result.RequestId);
            Assert.NotNull(result.Document);
        }
        finally
        {
            result.Document?.Dispose();
        }
    }

    [Fact]
    public void Parse_AcceptsANumericRequestId()
    {
        string line = """{"type":"control_response","response":{"request_id":7}}""";
        EnvelopeParser.EnvelopeParseResult result = EnvelopeParser.Parse(line);
        try
        {
            Assert.Equal(EnvelopeKind.ControlResponseMatched, result.Kind);
            Assert.Equal("7", result.RequestId);
        }
        finally
        {
            result.Document?.Dispose();
        }
    }

    [Fact]
    public void Parse_NullResponse_IsNotControlResponse_NeverThrows()
    {
        // Codex review High #6's exact reproduction case: a syntactically valid envelope
        // whose "response" is null rather than an object must degrade gracefully.
        string line = """{"type":"control_response","response":null}""";
        EnvelopeParser.EnvelopeParseResult result = EnvelopeParser.Parse(line);
        try
        {
            Assert.Equal(EnvelopeKind.NotControlResponse, result.Kind);
            Assert.Null(result.RequestId);
            Assert.NotNull(result.Document); // caller still owns and must dispose it
        }
        finally
        {
            result.Document?.Dispose();
        }
    }

    [Fact]
    public void Parse_MissingRequestId_IsNotControlResponse()
    {
        string line = """{"type":"control_response","response":{"five_hour":{"utilization":1}}}""";
        EnvelopeParser.EnvelopeParseResult result = EnvelopeParser.Parse(line);
        try
        {
            Assert.Equal(EnvelopeKind.NotControlResponse, result.Kind);
        }
        finally
        {
            result.Document?.Dispose();
        }
    }

    [Fact]
    public void Parse_NonControlResponseEnvelope_IsIgnored()
    {
        string line = """{"type":"system","subtype":"init"}""";
        EnvelopeParser.EnvelopeParseResult result = EnvelopeParser.Parse(line);
        try
        {
            Assert.Equal(EnvelopeKind.NotControlResponse, result.Kind);
        }
        finally
        {
            result.Document?.Dispose();
        }
    }

    [Fact]
    public void Parse_TopLevelArray_IsNotControlResponse_NeverThrows()
    {
        EnvelopeParser.EnvelopeParseResult result = EnvelopeParser.Parse("[1,2,3]");
        try
        {
            Assert.Equal(EnvelopeKind.NotControlResponse, result.Kind);
        }
        finally
        {
            result.Document?.Dispose();
        }
    }

    [Fact]
    public void Parse_MalformedJson_IsParseError_DocumentIsNull()
    {
        EnvelopeParser.EnvelopeParseResult result = EnvelopeParser.Parse("{not json");
        Assert.Equal(EnvelopeKind.ParseError, result.Kind);
        Assert.Null(result.Document);
        Assert.NotNull(result.Error);
    }
}
