using ClaudeStatusBar.Data;
using ClaudeStatusBar.Model;
using ClaudeStatusBar.Ui;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// Model/LoginFlowDecision.cs (docs/multi-account.md "Login flow") and the wording in Ui/LoginText.cs.
/// All identities are placeholders; "Acme AB" and "Globex AB" are made-up organisations.
/// </summary>
public class LoginFlowDecisionTests
{
    const string Person = "11111111-1111-4111-8111-111111111111";
    const string OrgAcme = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    const string OrgGlobex = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    const string OtherPerson = "22222222-2222-4222-8222-222222222222";

    static readonly AuthStatus ClaudeAi = new(true, "claude.ai");

    static AccountIdentity Identity(string account, string org, string orgName = "Acme AB") =>
        new(account, org, orgName, "claude_team", null, null, "alex@example.com", "Alex");

    static TrackedAccount Tracked(string slot, string label, AccountIdentity? identity) => new(slot, label, identity);

    // ---- the login did not take ----

    [Fact]
    public void NotLoggedIn_OrNoStatus_OrAnotherAuthMethod_OrNoIdentity_IsRejected()
    {
        AccountIdentity id = Identity(Person, OrgAcme);
        var tracked = new List<TrackedAccount>();

        Assert.Equal(LoginOutcome.RejectNotLoggedIn, LoginFlowDecision.Decide(null, id, tracked, null).Outcome);
        Assert.Equal(LoginOutcome.RejectNotLoggedIn, LoginFlowDecision.Decide(new AuthStatus(false, "none"), id, tracked, null).Outcome);
        Assert.Equal(LoginOutcome.RejectNotLoggedIn, LoginFlowDecision.Decide(new AuthStatus(true, "console"), id, tracked, null).Outcome);
        Assert.Equal(LoginOutcome.RejectNotLoggedIn, LoginFlowDecision.Decide(ClaudeAi, null, tracked, null).Outcome);
    }

    // ---- add ----

    [Fact]
    public void Add_NewIdentity_IsAdopted_WithALadderLabelNeverTheRawPersonalOrgName()
    {
        AccountIdentity personal = Identity(Person, OrgGlobex, orgName: "alex@example.com's Organization");

        LoginDecision decision = LoginFlowDecision.Decide(ClaudeAi, personal, new List<TrackedAccount>(), null);

        Assert.Equal(LoginOutcome.Adopt, decision.Outcome);
        Assert.DoesNotContain("Organization", decision.NewLabel);
        Assert.Equal("alex", decision.NewLabel); // ladder step 4 (no plan known yet): the email's local part, never "<email>'s Organization"
    }

    [Fact]
    public void Add_UsesTheOrganisationNameWhenItIsARealOne()
    {
        LoginDecision decision = LoginFlowDecision.Decide(ClaudeAi, Identity(Person, OrgAcme), new List<TrackedAccount>(), null);

        Assert.Equal("Acme AB", decision.NewLabel);
    }

    [Fact]
    public void Add_SameIdentityAsATrackedAccount_IsADuplicate_NamingThatAccount()
    {
        var tracked = new List<TrackedAccount>
        {
            Tracked("1", "Max", Identity(Person, OrgGlobex)),
            Tracked("4", "Acme AB", Identity(Person, OrgAcme)),
        };

        LoginDecision decision = LoginFlowDecision.Decide(ClaudeAi, Identity(Person, OrgAcme), tracked, null);

        Assert.Equal(LoginOutcome.RejectDuplicate, decision.Outcome);
        Assert.Equal("Acme AB", decision.MatchedLabel);
    }

    [Fact]
    public void Add_SamePersonInAnotherOrganisation_IsNotADuplicate()
    {
        var tracked = new List<TrackedAccount> { Tracked("1", "Max", Identity(Person, OrgGlobex)) };

        LoginDecision decision = LoginFlowDecision.Decide(ClaudeAi, Identity(Person, OrgAcme), tracked, null);

        Assert.Equal(LoginOutcome.Adopt, decision.Outcome);
    }

    // ---- re-login ----

    [Fact]
    public void Relogin_SameAccount_IsASwap()
    {
        var tracked = new List<TrackedAccount>
        {
            Tracked("1", "Max", Identity(Person, OrgGlobex)),
            Tracked("4", "Acme AB", Identity(Person, OrgAcme)),
        };

        LoginDecision decision = LoginFlowDecision.Decide(ClaudeAi, Identity(Person, OrgAcme), tracked, reloginSlot: "4");

        Assert.Equal(LoginOutcome.Swap, decision.Outcome);
    }

    [Fact]
    public void Relogin_AsADifferentTrackedAccount_ChangesNothing_AndNamesWhoLoggedIn()
    {
        var tracked = new List<TrackedAccount>
        {
            Tracked("1", "Max", Identity(Person, OrgGlobex)),
            Tracked("4", "Acme AB", Identity(Person, OrgAcme)),
        };

        LoginDecision decision = LoginFlowDecision.Decide(ClaudeAi, Identity(Person, OrgGlobex), tracked, reloginSlot: "4");

        Assert.Equal(LoginOutcome.RejectDifferentAccount, decision.Outcome);
        Assert.Equal("Max", decision.MatchedLabel);
    }

    [Fact]
    public void Relogin_AsAnUntrackedAccount_ChangesNothing()
    {
        var tracked = new List<TrackedAccount> { Tracked("4", "Acme AB", Identity(Person, OrgAcme)) };

        LoginDecision decision = LoginFlowDecision.Decide(ClaudeAi, Identity(OtherPerson, OrgGlobex, "Globex AB"), tracked, reloginSlot: "4");

        Assert.Equal(LoginOutcome.RejectDifferentAccount, decision.Outcome);
        Assert.Null(decision.MatchedLabel);
        Assert.Equal("Globex AB", decision.NewLabel);
    }

    [Fact]
    public void Relogin_WhenTheAccountsOwnIdentityIsUnreadable_NothingChanges_NeverASwap()
    {
        var tracked = new List<TrackedAccount>
        {
            Tracked("1", "Max", Identity(Person, OrgGlobex)),
            Tracked("4", "Konto 2", null),
        };

        // It cannot be confirmed that this is the same account, so nothing changes (the spec: re-login that cannot be verified is discarded).
        LoginDecision unverifiable = LoginFlowDecision.Decide(ClaudeAi, Identity(OtherPerson, OrgAcme), tracked, "4");
        Assert.Equal(LoginOutcome.RejectUnverifiable, unverifiable.Outcome);
        Assert.Equal("Konto 2", unverifiable.MatchedLabel);
        Assert.Equal(LoginOutcome.RejectDifferentAccount, LoginFlowDecision.Decide(ClaudeAi, Identity(Person, OrgGlobex), tracked, "4").Outcome);
    }

    [Fact]
    public void Relogin_OfAnAccountThatVanishedMeanwhile_IsTreatedAsAPlainAdd()
    {
        LoginDecision decision = LoginFlowDecision.Decide(ClaudeAi, Identity(Person, OrgAcme), new List<TrackedAccount>(), reloginSlot: "4");

        Assert.Equal(LoginOutcome.Adopt, decision.Outcome);
    }

    // ---- wording ----

    [Fact]
    public void Wording_NamesTheAccountAndTellsTheUserWhatToDoNext()
    {
        string cannotVerify = LoginText.CannotVerify("Acme AB");
        Assert.Contains("Acme AB", cannotVerify);
        Assert.Contains("Inget ändrades", cannotVerify);
        Assert.Equal("Inloggad: Acme AB", LoginText.LoggedIn("Acme AB"));
        Assert.Equal("Inloggad igen: Acme AB", LoginText.LoggedInAgain("Acme AB"));
        Assert.Contains("Acme AB", LoginText.Duplicate("Acme AB"));
        Assert.Contains("annat konto eller en annan organisation", LoginText.Duplicate("Acme AB"));
        string different = LoginText.DifferentAccount("Acme AB", "Max");
        Assert.Contains("Max", different);
        Assert.Contains("Acme AB", different);
        Assert.Contains("Inget ändrades", different);
        Assert.Contains(LoginText.AddAccountMenu, different);
        Assert.Contains("Inget konto lades till", LoginText.NotLoggedIn(relogin: false));
        Assert.Contains("Inget ändrades", LoginText.NotLoggedIn(relogin: true));
    }

    [Fact]
    public void Wording_RemoveConfirmation_NamesTheLabelAndThePlanWhenKnown()
    {
        Assert.Equal("Ta bort Acme AB?", LoginText.RemoveTitle("Acme AB"));
        Assert.Contains("Acme AB (Team)", LoginText.RemoveBody("Acme AB", "Team"));
        string noPlan = LoginText.RemoveBody("Acme AB", null);
        Assert.Contains("Acme AB", noPlan);
        Assert.DoesNotContain("(", noPlan);
        Assert.Contains("berörs inte", noPlan); // the user's own Claude Code login is not touched
    }

    [Fact]
    public void Wording_TheLoginConsoleNotice_AsksForTheRightOrganisationAndSaysTheWindowCloses()
    {
        Assert.Contains("rätt organisation", LoginText.ConsoleNotice);
        Assert.Contains("stängs av sig självt", LoginText.ConsoleNotice);
    }
}
