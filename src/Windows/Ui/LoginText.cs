namespace ClaudeStatusBar.Ui;

/// <summary>
/// Every Swedish string of the login / remove flows and the tray menu entries that start them
/// (docs/multi-account.md "Login flow"), kept apart from the WinForms plumbing so the wording is
/// testable and lives in one place. Labels passed in are the AccountLabel ladder's, never a raw
/// organisation name or an email address.
/// </summary>
public static class LoginText
{
    public const string AddAccountMenu = "Lägg till konto…";
    public const string AccountsMenu = "Konton";
    public const string ReloginMenu = "Logga in igen";
    public const string RenameMenu = "Byt namn…";
    public const string RemoveMenu = "Ta bort…";
    public const string ReloginButton = "Logga in igen";

    public const string ZeroAccountsTooltip = "Logga in för att visa kvoten";
    public const string LoadingTooltip = "Hämtar kvoten…";
    public const string LoadingStatusLine1 = "Hämtar kvoten…";
    public const string LoadingStatusLine2 = "Väntar på första avläsningen…";
    public const string LoadingRow = "hämtar…";
    public const string NeedsLoginTooltip = "Inte inloggad – högerklicka och välj Logga in igen";
    public const string NeedsLoginStatusLine1 = "Inloggningen har gått ut eller saknas";

    public const string WindowCaption = "Claude Status Bar";

    /// <summary>The one line printed in the login console before `claude auth login` takes over.</summary>
    public const string ConsoleNotice =
        "Logga in med det konto du vill följa och välj rätt organisation i webbläsaren. Fönstret stängs av sig självt.";

    public static string LoggedIn(string label) => $"Inloggad: {label}";

    public static string LoggedInAgain(string label) => $"Inloggad igen: {label}";

    public static string NeedsLoginToast(string label) => $"{label}: inte inloggad – högerklicka och välj Logga in igen";

    public static string NotLoggedIn(bool relogin) => relogin
        ? "Inloggningen slutfördes inte. Inget ändrades."
        : "Inloggningen slutfördes inte. Inget konto lades till.";

    public static string Duplicate(string existingLabel) =>
        $"Det här är samma inloggning som {existingLabel}, som redan följs. Välj ett annat konto eller en annan organisation i webbläsaren och försök igen.";

    public static string DifferentAccount(string expectedLabel, string loggedInAs) =>
        $"Du loggade in som {loggedInAs}, inte som {expectedLabel}. Inget ändrades. Vill du följa ett nytt konto, välj {AddAccountMenu}";

    public static string CannotVerify(string expectedLabel) =>
        $"Det går inte att läsa vilket konto {expectedLabel} är, så det går inte att bekräfta att du loggade in på samma konto. Inget ändrades. Ta bort kontot och välj {AddAccountMenu} för att följa det igen.";

    public static string RemoveTitle(string label) => $"Ta bort {label}?";

    /// <summary>The plan is added only when the label does not already say it ("Max", not "Max (Max)").</summary>
    public static string RemoveBody(string label, string? plan) =>
        (plan is null || label.Contains(plan, StringComparison.OrdinalIgnoreCase) ? $"{label} slutar följas" : $"{label} ({plan}) slutar följas")
        + " och inloggningen raderas från appens datamapp. Din egen Claude Code-inloggning berörs inte.";

    public static string RenameTitle(string label) => $"Byt namn på {label}";
    public const string RenameHint = "Lämna tomt för det automatiska namnet.";
    public const string RenameOk = "OK";
    public const string RenameCancel = "Avbryt";
    public const string RenameFailed = "Det gick inte att spara namnet. Inget ändrades.";

    public const string RemoveFailed = "Det gick inte att ta bort kontot. Inget ändrades.";

    public static string FlowFailed(bool relogin) => relogin
        ? "Det gick inte att logga in igen. Inget ändrades."
        : "Det gick inte att lägga till kontot. Inget konto lades till.";
}
