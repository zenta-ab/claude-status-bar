using System.Text.Json;

namespace ClaudeStatusBar.Data;

public enum EnvelopeKind { ControlResponseMatched, NotControlResponse, ParseError }

/// <summary>
/// Pure parsing of one raw stdout line into a control_response envelope (Codex
/// review High #6, Medium #9): every step checks JsonValueKind before
/// TryGetProperty/GetString, so a syntactically valid but structurally unexpected
/// envelope -- e.g. <c>{"type":"control_response","response":null}</c> -- comes
/// back as NotControlResponse instead of throwing and killing the stdout pump.
///
/// Ownership: on ParseError, Document is always null (nothing to dispose). On
/// NotControlResponse or ControlResponseMatched, Document is non-null and the
/// CALLER owns it -- ClaudeCliChannel.HandleLine disposes it unless a
/// ControlResponseMatched result's document ownership is transferred into a
/// TaskCompletionSource via TrySetResult (and disposes it there too if
/// TrySetResult returns false, since nothing else then owns it).
/// </summary>
public static class EnvelopeParser
{
    public readonly record struct EnvelopeParseResult(EnvelopeKind Kind, string? RequestId, JsonDocument? Document, string? Error);

    public static EnvelopeParseResult Parse(string line)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            return new EnvelopeParseResult(EnvelopeKind.ParseError, null, null, ex.Message);
        }

        try
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new EnvelopeParseResult(EnvelopeKind.NotControlResponse, null, doc, null);

            if (!root.TryGetProperty("type", out JsonElement typeProp) || typeProp.ValueKind != JsonValueKind.String
                || typeProp.GetString() != "control_response")
                return new EnvelopeParseResult(EnvelopeKind.NotControlResponse, null, doc, null);

            if (!root.TryGetProperty("response", out JsonElement response) || response.ValueKind != JsonValueKind.Object)
                return new EnvelopeParseResult(EnvelopeKind.NotControlResponse, null, doc, null);

            if (!response.TryGetProperty("request_id", out JsonElement reqIdProp))
                return new EnvelopeParseResult(EnvelopeKind.NotControlResponse, null, doc, null);

            string? requestId = reqIdProp.ValueKind switch
            {
                JsonValueKind.String => reqIdProp.GetString(),
                JsonValueKind.Number when reqIdProp.TryGetInt64(out long n) => n.ToString(),
                _ => null,
            };

            return requestId is null
                ? new EnvelopeParseResult(EnvelopeKind.NotControlResponse, null, doc, null)
                : new EnvelopeParseResult(EnvelopeKind.ControlResponseMatched, requestId, doc, null);
        }
        catch (Exception ex)
        {
            // Defensive only: every branch above checks ValueKind before reading, but a
            // structurally-surprising envelope must degrade the parse, never the pump.
            doc.Dispose();
            return new EnvelopeParseResult(EnvelopeKind.ParseError, null, null, ex.Message);
        }
    }
}
