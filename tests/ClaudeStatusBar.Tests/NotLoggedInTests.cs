using System.Text.Json;
using ClaudeStatusBar.Data;
using ClaudeStatusBar.Icons;
using ClaudeStatusBar.Model;
using ClaudeStatusBar.Ui;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// docs/multi-account.md "NeedsLogin": the structured "not logged in" parse result, how a
/// NeedsLogin view reads in the tooltip and the panel (distinct from a plain Unknown), and
/// "Senast avläst" meaning the last SUCCESSFUL read.
/// </summary>
public class NotLoggedInTests
{
    static readonly TimeZoneInfo Tz = TimeZoneInfo.CreateCustomTimeZone("Test/+1", TimeSpan.FromHours(1), "Test/+1", "Test/+1");
    static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    // The exact response claude.exe 2.1.292 gives a config dir with no usable login (docs/mac-port.md).
    const string NullShape = """
    {"type":"control_response","response":{"subtype":"success","request_id":"1","response":{
      "session":{"total_cost_usd":0,"total_api_duration_ms":0,"total_duration_ms":488,"total_lines_added":0,"total_lines_removed":0,"model_usage":{}},
      "subscription_type":null,"rate_limits_available":false,"rate_limits":null,"behaviors":null}}}
    """;

    const string LoggedIn = """
    {"type":"control_response","response":{"subtype":"success","request_id":"1","response":{
      "subscription_type":"max","rate_limits_available":true,
      "rate_limits":{"five_hour":{"utilization":42,"resets_at":"2026-09-11T14:00:00.000000+00:00"},"seven_day":{"utilization":13.5,"resets_at":"2026-09-15T10:00:00.000000+00:00"}}}}}
    """;

    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    // ---- parser ----

    [Fact]
    public void Parse_TheNullShape_IsAStructuredNotLoggedIn_NotAGenericParseError()
    {
        UsageParseResult result = UsageParser.Parse(Json(NullShape));

        Assert.True(result.NotLoggedIn);
        Assert.Null(result.Snapshot);
        Assert.Equal(UsageParser.NotLoggedInError, result.Error);
    }

    [Fact]
    public void Parse_TheNullShape_WasAGenericParseErrorBefore_AndTryParseStillSaysNoSnapshot()
    {
        UsageSnapshot? snapshot = UsageParser.TryParse(Json(NullShape), out string? error);

        Assert.Null(snapshot);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.NotEqual(UsageParser.NotLoggedInError, error); // TryParse is unchanged: the structured result lives in Parse
    }

    [Fact]
    public void Parse_ALoggedInResponse_IsASnapshot()
    {
        UsageParseResult result = UsageParser.Parse(Json(LoggedIn));

        Assert.False(result.NotLoggedIn);
        Assert.NotNull(result.Snapshot);
        Assert.Equal("max", result.Snapshot!.SubscriptionType);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("""{"type":"control_response","response":{"response":{"subscription_type":null,"rate_limits_available":false}}}""")]                    // rate_limits missing
    [InlineData("""{"type":"control_response","response":{"response":{"subscription_type":"max","rate_limits_available":false,"rate_limits":null}}}""")] // a plan is reported
    [InlineData("""{"type":"control_response","response":{"response":{"subscription_type":null,"rate_limits_available":true,"rate_limits":null}}}""")]  // limits are said to be available
    [InlineData("""{"type":"control_response","response":{"response":{"subscription_type":null,"rate_limits_available":false,"rate_limits":{"x":1}}}}""")] // limits are present
    [InlineData("""{"type":"control_response","response":null}""")]
    [InlineData("""{"type":"control_response"}""")]
    [InlineData("""{"type":"control_response","response":{"response":{"unrelated":true}}}""")]
    public void Parse_AnythingLessThanTheWholeNullShape_IsAnOrdinaryFailure(string json)
    {
        UsageParseResult result = UsageParser.Parse(Json(json));

        Assert.False(result.NotLoggedIn);
        Assert.Null(result.Snapshot);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    // ---- tooltip + panel: NeedsLogin is not Unknown ----

    static QuotaView NeedsLoginView(DateTimeOffset? lastSuccess = null) => new(
        WindowView.Empty(WindowKind.Session, QuotaWindows.SessionMinutes),
        WindowView.Empty(WindowKind.Weekly, QuotaWindows.WeeklyMinutes),
        Freshness.Unknown, null, lastSuccess, TimeSpan.FromMinutes(2), UsageParser.NotLoggedInError, QuotaState.Measuring, null, NeedsLogin: true);

    static QuotaView UnknownView() => NeedsLoginView() with { NeedsLogin = false };

    [Fact]
    public void Tooltip_NeedsLogin_SaysHowToFixIt_AndDiffersFromUnknown()
    {
        string needs = IconSlot.BuildTooltip(NeedsLoginView(), accountLabel: null);
        string unknown = IconSlot.BuildTooltip(UnknownView(), accountLabel: null);

        Assert.Equal("Inte inloggad – högerklicka och välj Logga in igen", needs);
        Assert.Equal("Kan inte läsa kvoten", unknown);
    }

    [Fact]
    public void Tooltip_NeedsLogin_StaysWithinTheTrayCap_WithALabel()
    {
        string tooltip = IconSlot.BuildTooltip(NeedsLoginView(), accountLabel: "A rather long organisation name AB");

        Assert.True(tooltip.Length <= 63, tooltip);
        Assert.EndsWith("välj Logga in igen", tooltip);
    }

    [Fact]
    public void Icon_NeedsLogin_IsTheSameGreyRingAsUnknown()
    {
        QuotaIconParams needs = QuotaIconParams.Build(NeedsLoginView(), 16, taskbarDark: true, Now);
        QuotaIconParams unknown = QuotaIconParams.Build(UnknownView(), 16, taskbarDark: true, Now);

        Assert.True(needs.Outline);
        Assert.Equal(unknown, needs);
    }

    [Fact]
    public void StatusBox_NeedsLogin_SaysTheLoginExpiredOrIsMissing()
    {
        StatusBoxText box = PanelText.Compose(NeedsLoginView(), Now, Tz).StatusBox;

        Assert.Equal("Inloggningen har gått ut eller saknas", box.Line1);
        Assert.Equal("Logga in igen för att visa kvoten", box.Line2);
        Assert.Equal(PanelColorRole.Unknown, box.Role);
    }

    [Fact]
    public void StatusBox_NeedsLogin_QuotesTheLastSuccessfulRead()
    {
        StatusBoxText box = PanelText.Compose(NeedsLoginView(lastSuccess: new DateTimeOffset(2026, 9, 11, 9, 30, 0, TimeSpan.Zero)), Now, Tz).StatusBox;

        Assert.StartsWith("Senast avläst kl 10:30", box.Line2);
        Assert.Equal("Inloggningen har gått ut eller saknas", box.Line1);
    }

    [Fact]
    public void StatusBox_Unknown_StaysAsItWas_NotTheLoginMessage()
    {
        StatusBoxText box = PanelText.Compose(UnknownView(), Now, Tz).StatusBox;

        Assert.Equal("Kan inte läsa kvoten", box.Line1);
    }

    [Fact]
    public void Wording_TheReloginButtonIsCalledLoggaInIgen()
    {
        Assert.Equal("Logga in igen", LoginText.ReloginButton);
        Assert.Equal("Inloggningen har gått ut eller saknas", LoginText.NeedsLoginStatusLine1);
        Assert.Equal("Max: inte inloggad – högerklicka och välj Logga in igen", LoginText.NeedsLoginToast("Max"));
    }

    // ---- "Senast avläst" = the last SUCCESSFUL read ----

    static string Raw(DateTimeOffset instant) => instant.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffK", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void LastSuccessAt_IsNotMovedByFailures()
    {
        var model = new QuotaModel();
        DateTimeOffset sessionReset = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset weeklyReset = new(2026, 1, 8, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset t0 = sessionReset.AddMinutes(-100);
        model.Ingest(new UsageSnapshot(50.0, Raw(sessionReset), 10.0, Raw(weeklyReset), t0), t0, 0);

        // Three failed polls over the next ten minutes.
        model.IngestFailure("boom", t0.AddMinutes(3), 3 * 60_000);
        model.IngestFailure("boom", t0.AddMinutes(6), 6 * 60_000);
        model.IngestFailure("boom", t0.AddMinutes(10), 10 * 60_000);

        QuotaView view = model.Evaluate(t0.AddMinutes(10), 10 * 60_000);
        Assert.Equal(t0, view.LastSuccessAt);
        Assert.Equal("boom", view.Error);
    }

    [Fact]
    public void LastSuccessAt_MovesOnTheNextSuccess()
    {
        var model = new QuotaModel();
        DateTimeOffset sessionReset = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset weeklyReset = new(2026, 1, 8, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset t0 = sessionReset.AddMinutes(-100);
        model.Ingest(new UsageSnapshot(50.0, Raw(sessionReset), 10.0, Raw(weeklyReset), t0), t0, 0);
        model.IngestFailure("boom", t0.AddMinutes(3), 3 * 60_000);

        DateTimeOffset t1 = t0.AddMinutes(5);
        model.Ingest(new UsageSnapshot(51.0, Raw(sessionReset), 10.0, Raw(weeklyReset), t1), t1, 5 * 60_000);

        Assert.Equal(t1, model.Evaluate(t1, 5 * 60_000).LastSuccessAt);
    }

    [Fact]
    public void LastSuccessAt_BeforeAnySuccess_IsNull()
    {
        var model = new QuotaModel();
        model.IngestFailure("boom", Now, 0);

        Assert.Null(model.Evaluate(Now, 0).LastSuccessAt);
    }

    [Fact]
    public void Panel_UnknownAfterFailures_QuotesTheLastSuccessNotTheLastFailure()
    {
        var model = new QuotaModel();
        DateTimeOffset sessionReset = new(2026, 9, 11, 14, 0, 0, TimeSpan.Zero);
        DateTimeOffset weeklyReset = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset success = new(2026, 9, 11, 9, 0, 0, TimeSpan.Zero);
        model.Ingest(new UsageSnapshot(50.0, Raw(sessionReset), 10.0, Raw(weeklyReset), success), success, 0);

        // Failures for three hours: freshness has long since fallen to Unknown.
        for (int minute = 10; minute <= 180; minute += 10)
            model.IngestFailure("boom", success.AddMinutes(minute), minute * 60_000L);
        DateTimeOffset now = success.AddMinutes(180);
        QuotaView view = model.Evaluate(now, 180 * 60_000L);
        Assert.Equal(Freshness.Unknown, view.Freshness);

        StatusBoxText box = PanelText.Compose(view, now, Tz).StatusBox;

        Assert.StartsWith("Senast avläst kl 10:00", box.Line2); // 09:00 UTC = 10:00 in the +1 test zone; NOT 12:00, the last failure
    }
}
