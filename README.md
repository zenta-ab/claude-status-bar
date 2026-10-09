# Claude Status Bar

A Windows 11 tray app that shows how much of your Claude subscription quota is left — the current
5-hour session and the weekly limit — and whether you will run out before the quota resets.

It needs no API key and costs no quota: it reads the same numbers Claude Code's `/usage` shows,
through Claude Code logins of its own, one per account you add from the tray menu.

> **Unofficial.** Not affiliated with or endorsed by Anthropic. It relies on undocumented Claude Code
> behaviour (the `get_usage` control request), which can change with any Claude Code update. It never
> reads your credentials. Provided as-is.

## What you see

**Tray icon** (next to the clock)
- Outer ring: weekly usage ("All models"). Inner pie: current 5-hour session.
- Colour is the verdict, driven by the forecast rather than the raw percentage:
  green = lasts until reset, amber = tight, red = runs out before reset, dashed grey = spent
  (the pie then counts down to the reset).
- A lighter slice continues past the used part: where you land at reset at the current pace.
- Dimmed with a dot = data may be stale. Grey ring with a "!" = quota can't be read; hover for why. "Inte inloggad" means the account's login expired (right-click, Konton, Logga in igen).

**Panel** (left-click the icon; click the icon again, click elsewhere or press Esc to close)
- A status box that always answers two things: will the budget last, and when does it reset or
  run out. For example: "⚠ Kvoten tar slut kl 15:01 (om 1 h 43 min) — 1 h 4 min före reset kl 16:05".
- Per window, two bars on the same scale: **Tid** (how much of the window has passed) and
  **Kvot** (how much is used, plus the forecast). A quota bar shorter than the time bar means
  you're under budget.
- The forecast stays in "Mäter takt…" until there is enough data: the first 10 minutes of a session,
  and the first 24 hours of a week (so a morning's work pace isn't extrapolated through nights).

## Requirements

- Windows 11.
- **Claude Code**, installed with the official installer (it puts `claude.exe` in
  `%USERPROFILE%\.local\bin`) or with any other `claude.exe` on `PATH`.
- **A Pro, Max, Team or Enterprise subscription to log in with.** Quota windows only exist for
  subscriptions; an API-key login has no plan limits to show. You log in from the app itself; your own
  Claude Code login (terminal, VS Code) is neither needed nor used.
- To build: .NET SDK 10.0 (`global.json` pins 10.0.204 and rolls forward to newer patches).

## Install on a computer

```powershell
gh repo clone zenta-ab/claude-status-bar   # or: git clone https://github.com/zenta-ab/claude-status-bar.git
cd claude-status-bar
.\scripts\install.ps1
```

`install.ps1` publishes a Release build to `%LOCALAPPDATA%\Programs\ClaudeStatusBar`, starts it at
Windows sign-in (`HKCU\...\Run`, no admin rights), launches it, and pins the icon next to the clock.

**First run:** the icon is grey and its tooltip reads "Logga in för att visa kvoten". Right-click it and
choose **Lägg till konto…** (or left-click the icon): a console window opens and sends you to the browser;
log in with the account you want and pick the right organisation there. The window closes by itself and the
account's icon appears.

- **Update** after pulling or changing code: run `.\scripts\install.ps1` again. The running app is asked to
  exit cleanly first and is only killed if it has not exited after 15 s.
- **Uninstall**: `.\scripts\install.ps1 -Uninstall` (logs are kept, and so are the logins the app holds —
  the command prints where). Add `-RemoveLogins` to delete those too:
  `.\scripts\install.ps1 -Uninstall -RemoveLogins`.
- Autostart can also be switched off in Task Manager → Startup apps.

If PowerShell blocks the script: `powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1`.

## How it gets the data

The app runs one long-lived Claude Code child process
(`claude -p --input-format stream-json --output-format stream-json --verbose`) and sends it the
`get_usage` control request — the same path Anthropic's VS Code extension uses. Control requests are
not metered. The app keeps its own Claude Code logins in its data folder
(`%LOCALAPPDATA%\ClaudeStatusBar\accounts`); they are created and managed by Claude Code itself, and the app
never reads token values.

- Polls every 30 s while you're using quota, every 2.5 min when idle, 5 min when a quota is spent,
  plus one poll right after each reset. Countdowns tick locally every second.
- Claude Code refreshes its quota data about every 5 minutes; the panel header shows how old the
  numbers really are.
- Each child runs in its own folder under `%LOCALAPPDATA%\ClaudeStatusBar\agent`, so its session (and any `SessionStart`
  hooks) never touches your repositories. If it crashes or Claude Code auto-updates, the app restarts
  it with backoff.

## Several accounts

If you hold more than one Claude plan (a personal Pro/Max, a Team seat, ...), Status Bar can
track all of them at once — each with its own tray icon and its own claude.exe child, so one
account's quota, forecast and history never mixes with another's.

- **Add an account**: right-click the icon and choose **Lägg till konto…**. A console opens and sends you
  to the browser; log in with the account you want and choose the right organisation there. Logging in
  as an account the app already follows is refused, and says which one it is.
- **All accounts in one panel**: right-click, **Visa alla konton i panelen**. Clicking any icon then opens a
  panel with a compact card per account (also those without an icon); click a card for that account's details.
- **Rename** (right-click, **Konton**, the account, **Byt namn…**): give an account a name of your own
  (up to 40 characters); leave it empty to go back to the automatic one. When two accounts would get the
  same automatic name, the email domain tells them apart ("Max (example.com)" / "Max (example.org)").
- **Log in again / remove** (right-click, **Konton**, the account): **Logga in igen** replaces an
  expired login (the panel and tooltip say "Inte inloggad" and offer the same button); **Ta bort…**
  stops following the account and deletes its login from the app's data folder. Neither signs the
  account out on Anthropic's side, and neither touches your own Claude Code login.
- **Every account has a login of its own**, created by the app and unaffected by what you do in your
  terminal or VS Code. Nothing ever follows "whichever account is logged in now".
- **Two display modes**, toggled from the tray icon's right-click menu or in `accounts.json`:
  `perAccount` (default) shows one icon per account, up to `maxIcons` (3); `binding` shows a
  single icon for whichever account is closest to being blocked. Either way, left-clicking an
  icon opens the panel for that account, and the panel's "other accounts" list lets you switch to
  any account that isn't showing an icon right now.
- **Cost**: one resident `claude.exe` child (~230 MB) and one poll cycle per account. Control
  requests are unmetered, so accounts never consume each other's quota, and one account failing
  or logged out only degrades its own icon and panel row.

See `docs/multi-account.md` for the full design (login slots, the login flow, labels, identity handling).

## Troubleshooting

| Symptom | Check |
|---|---|
| Icon not visible | Click `^` next to the clock and drag it out, or re-run `install.ps1` |
| "Kan inte läsa kvoten" | Is Claude Code installed (`claude --version`)? The panel says when the last successful read was. |
| "Inte inloggad" / "Inloggningen har gått ut eller saknas" | The account's login expired. Right-click, **Konton**, the account, **Logga in igen** (or the button in the panel). |
| Grey icon, "Logga in för att visa kvoten" | No account yet. Right-click, **Lägg till konto…**. |
| Numbers differ from claude.ai | Reload the claude.ai page — it only refreshes on load |
| Anything else | Logs in `%LOCALAPPDATA%\ClaudeStatusBar\logs` (daily raw log, monthly `window-shape-YYYY-MM.csv`) |

## Development

```powershell
dotnet build ClaudeStatusBar.slnx
dotnet test ClaudeStatusBar.slnx
dotnet run --project src\Windows -- --demo       # cycles through every state, no Claude child
dotnet run --project src\Windows -- --selftest   # icon pipeline GDI/USER handle leak check
```

A running installed copy locks nothing in the repo; a running `dotnet run` copy locks `bin\Debug`.

## Privacy guard

This repo is public and the app reads account data, so nothing personal may land in it.
Before committing, run:

```powershell
.\scripts\check-privacy.ps1 -All        # scan every tracked file
.\scripts\check-privacy.ps1 -Install    # enable the pre-commit hook for this clone
```

It blocks email addresses, UUIDs, user paths and API tokens, allowing only obvious
placeholders (reserved example domains, repeated-character UUIDs). Git hooks are per clone,
so each contributor runs `-Install` once.

## Docs

- `docs/forecast-and-states.md` — forecast model, states, freshness, decision record
- `docs/panel-v2.md` — panel layout and copy rules
- `docs/multi-account.md` — accounts: login slots, the login flow, labels, identity handling, configuration shape
- `docs/reviews/` — independent reviews (Codex, and a Claude reviewer for the own-login design) and the decisions taken on each finding
- `docs/backlog.md` — open ideas, e.g. learning your working-hours pattern for the forecast

## License

MIT, see [`LICENSE`](LICENSE).
