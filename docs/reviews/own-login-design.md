# Review: every tracked account owns its own login (Windows)

**Reviewer.** An independent Claude reviewer with no prior context, given only the design and the
questions below. This was *not* a Codex review: Codex was out of credits at the time, so the same
challenge was run as a fresh, separate Claude pass instead. The reviewer's transcript is not
reproduced here; this file records the challenge and the decisions taken from it
(the implemented design is in `docs/multi-account.md`).

## What was challenged

The Windows app tracked "accounts" as `accounts.json` entries pointing at a Claude config directory,
or at `null`, meaning "whatever Claude Code is logged into". Accounts were added with a PowerShell
script, identified by their position in a list built once at startup, and stopped by killing the
child. The challenge: *what assumptions here are weak, what would you reject, what failure modes were
missed, is there a simpler or more robust model?*

## Decisions, and the weakness each one answers

The reasoning is written up here by the implementer from the decided design; the reviewer's own wording is not reproduced.

| # | Weakness | Decision |
|---|---|---|
| 1 | **A follower entry (`configDir: null`) changes account whenever the user logs in elsewhere**, and two entries can silently converge on one login. Its identity file also lives at a different, sibling location. | Remove followers entirely. Every tracked account is a login the app created and owns. Old `null` entries are dropped on migration (their history stays on disk keyed by identity). |
| 2 | **Nothing stops a bad path reaching a login, a delete or a spawn.** A hand-edited or migrated `configDir` could point at the user's own `%USERPROFILE%\.claude`, and "remove" or "re-login" would act on it. | One path guard in front of every login / delete / spawn: exactly `<accountsRoot>\<id>\config` (or `<id>` for delete), a strict id shape, no reparse points, compared as a literal string. Unit-tested with traversal, UNC, junctions, case and trailing separators. |
| 3 | **`accounts.json` as the source of truth orphans logins**: a lost or malformed file, or a crash between the browser login and the save, leaves a logged-in folder nothing refers to. | Slots are the truth. Each has a `slot.json` state (`pending` / `active` / `retired`); startup reconciliation adopts an interrupted login that has an identity, finishes deleting retired ones, appends forgotten active ones and drops entries without an active folder. A malformed file is still quarantined, then rebuilt from the slots. |
| 4 | **Re-login into the existing folder can destroy a working login** if the user picks the wrong account in the browser, and a half-finished login looks like a logged-out account. | Add and re-login always log into a **new** slot. Only after `auth status` and the new slot's `oauthAccount` agree with the intent is the old slot replaced; otherwise the new one is retired and deleted and nothing changes. |
| 5 | **`auth logout` may revoke a grant another slot depends on.** Two slots for the same person each hold their own grant; whether revoking one affects the other is unverified. | Never run `auth logout`. Removing, swapping and discarding only delete folders. Recorded as an open question in `docs/backlog.md`. |
| 6 | **Inherited environment overrides the stored login.** An app started from inside Claude Code, an IDE, or a shell with an API key hands those to every child. `CLAUDE_SECURESTORAGE_CONFIG_DIR` also outranks `CLAUDE_CONFIG_DIR`. | One environment builder for the polling child, `auth login` and `auth status`: pin both config variables, remove the credential / provider / endpoint / identity overrides found in the 2.1.292 binary (listed in `docs/multi-account.md`), give each slot its own agent folder. |
| 7 | **"Logged out" and "could not read" look the same**, so a dead login shows as a generic failure that waits for a retry that can never succeed. | Parse the null `get_usage` shape (`subscription_type` null, `rate_limits_available` false, `rate_limits` null) as a structured result. First answer: restart that child once; if the fresh child says the same, the account is NeedsLogin, with its own tooltip, panel message and re-login button and one toast per transition. `auth status` is deliberately not used for this. |
| 8 | **"Senast avläst" was moved by failures**, so an account that could not be read claimed a recent read. | It is the time of the last successful read; failures do not move it. |
| 9 | **The account list is addressed by position** (icons, panel focus, right-click target, duplicate target) but is built once, so any add / remove would retarget them. | Rebuild the whole set on any change (stop all gracefully in parallel, reconcile, recreate, restart). Focus and every per-account action are kept by slot id. |
| 10 | **No state for "no accounts"**: an empty list was invalid and replaced by a follower default, and with nothing to show there was no icon to reach Exit from. | An empty list is valid. With no enabled account there is exactly one grey icon ("Logga in för att visa kvoten") with the full menu; a left click starts the add flow. |
| 11 | **Stopping a child meant killing it**, which interrupts whatever Claude Code is flushing, and the supervisor could leak a child launched while it was being disposed. | Graceful stop: stop polling, mark the supervisor disposed (so the clean exit is not relaunched), close stdin under the stdin lock, wait for exit, then kill the tree and wait. The bound was measured, not guessed. The supervisor re-checks its disposed flag after `Start`. `install.ps1` asks the app to exit through a named event before it kills anything. |
| 12 | **The console that hosts the login might not be waitable** if the default terminal hands the window to another process. | Verified rather than assumed (see below). |
| 13 | **Uninstall left logins behind with no way to know.** | `-Uninstall` prints where the logins are; `-Uninstall -RemoveLogins` deletes them after the app has exited (no logout). |

## Rejected alternatives

- **Keep followers but flag them.** Still changes account silently, still needs the sibling identity
  file, still collides with a pinned entry.
- **Copy the default login into a slot.** Does not work: refresh-token rotation invalidates the copy
  within minutes (`docs/multi-account.md`, "Each account needs its own login").
- **Read `rate_limits` expiry from the credentials file to warn early.** Would mean reading the
  credentials file, which the app deliberately never does. Deferred (`docs/backlog.md`).
- **Decide NeedsLogin from `auth status`.** It spawns another process per decision and its output holds
  the email. The `get_usage` answer is the signal the poll already has.

## Measured while implementing

On Windows 11 with Windows Terminal installed and no default-terminal override, claude.exe 2.1.292, fresh
empty config directories created and deleted by the experiments:

- **Exit after stdin close** (`-p --input-format stream-json ...`): 0.39–0.54 s; the first `get_usage`
  answer about 0.55 s after launch. Stop wait set to 2 s.
- **Null-shape answer** from an empty config dir: exactly the shape in item 7, repeatably.
- **`auth status --json` with no login**: exit code 1, about 0.3 s, `loggedIn: false`, `authMethod: "none"`.
- **Waiting for a console child from a GUI parent** (the same launch the login uses, with a harmless
  child): the handle is the real process, not a stub — a 1.5 s wait timed out while the child slept 4 s,
  the unbounded wait returned at about 4.5 s, and the child's exit code came back.

## Not done / still open

- Whether revoking one grant affects another for the same account (item 5).
- The Mac app still uses the earlier model; parity is in `docs/backlog.md`.

## Second pass: review of the implementation

A review of the first implementation (again an independent Claude pass, not Codex) found ordering and
protection gaps, all fixed; the orderings are now tested in `AccountFlowControllerTests`:

| Finding | Fix |
|---|---|
| Remove showed its dialog before taking the busy flag, so "Lägg till konto…" could start under it, and a later rebuild's reconciliation retired and deleted the pending slot under a running `auth login`. | Busy flag first (released on No); the in-flight slot is passed as a protected slot to EVERY reconciliation pass; a second live instance exits at start (named mutex). |
| The login console was in a kill-on-close job, which would kill a browser cold-started by `claude auth login` when the job closed. | No job; the console is never killed; an app exit during a login leaves it running and the next start adopts the pending slot. |
| A swap retired the old slot before the new one was committed, and an unanswered `auth status` deleted a just-completed login. | New slot active and entry saved first; failures after that never discard it. `auth status` is retried once longer, then a readable `oauthAccount` is accepted. Remove writes the retired marker first and deletes nothing if it cannot. |
| A remove could act on an entry a swap had repointed; a re-login with an unreadable old identity was accepted. | The entry is looked up again after the dialog; an unverifiable re-login changes nothing. |
| Migration compared v1 paths literally and quarantined a file over `maxIcons`; one bad slot aborted reconciliation; every rebuild re-toasted NeedsLogin; focus could resolve mid-rebuild; dialogs could open behind the browser; a timed-out stop was not followed by a kill; deletes ran on the UI thread. | Canonical comparison and clamping; per-slot isolation; toast state kept by slot id; ticks gated while the set changes; topmost owner for dialogs; kill-after-timeout; deletes on the thread pool. |
| `dotnet test` created empty `agent\<id>` folders under the real data folder. | `ClaudeAuthCli` now honours its agent-root seam; the test asserts the full path. |

The dead-login shape was also checked against the raw logs of a real incident (about 70 answers over
roughly 11 hours): all of them were the null shape; no authentication-error response ever appeared
(`docs/multi-account.md`, "NeedsLogin").
