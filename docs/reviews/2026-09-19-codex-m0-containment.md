# Review round — M0, macOS port (Codex, 2026-09-19)

Adversarial round on the first cut of `src/Mac` (`ChildProcess`, `ControlChannel`, `UsageParser`),
run with `codex exec -s read-only -c model_reasoning_effort="high"` after confirming byte-exact that
Codex could read the repo. Codex's verdict was **block**, and on verification most of it held.

Every claim below was checked at `file:line` against the code before being acted on. Two were
rejected with evidence. One bug that the review did *not* find was discovered while re-testing the
fix for another.

## Accepted and fixed

| # | Severity | Finding | Fix |
|---|---|---|---|
| 1 | Critical | Pipes were created without `FD_CLOEXEC`, so a second account's child inherits the first child's stdin **write** end. The first `claude` then never sees EOF when the app dies — the primary containment mechanism, defeated. Invisible with one account, which is all Phase 1 measured. | Every descriptor is `FD_CLOEXEC` at creation; the three the child needs are handed over by `dup2`, which clears the flag on the copy. Two tests, one structural (`F_GETFD`) and one behavioural (a second child must not hold the first's stdin open). |
| 2 | Critical | The orphan sweep matched by `hasPrefix(agentRoot)`, so `…/agent-copy` matched `…/agent`; and it observed a pid via `ps`, inspected it via `lsof`, then signalled it — three moments, with pid reuse possible in between. | The sweep no longer enumerates anything. It reads a record this app wrote at launch (`pid`, start time to the microsecond, kernel-reported executable path) and kills only when all three still match. |
| 3 | Critical | `reap()` set `reaped = true` even when `waitpid(WNOHANG)` returned 0, and `isRunning` never consulted `reaped`. A still-running child was recorded as reaped (→ zombie), and after pid reuse a second `terminate()` could signal an unrelated process **group**. | `reaped` is set only on a real reap (`waitpid == pid`, or `ECHILD`). `isRunning` short-circuits on it, and `terminate` is a no-op once reaped. |
| 4 | High | The request timeout was constructed *after* `FileHandle.write` returned, so a blocking write into a full pipe (a stopped child, enough unanswered requests) hung forever. | Writes are non-blocking with `poll`, serialized under their own lock, and bounded by the same deadline as the wait. |
| 5 | High | Every syntactically valid response was stored, whether or not anyone was waiting. A late reply to a timed-out request leaked permanently; unknown ids grew the dictionary without bound. | Only ids in `pending` are retained; every exit path clears its entry. |
| 6 | High | `limits[]` and the exact keys were never compared. A stale or malformed `limits[]` would shadow correct exact keys and produce a confident wrong number with `error: nil`. | When both are usable they must agree on percentage (±0.5) and on the reset fingerprint; a disagreement returns **no snapshot** with an explanatory error. Silence beats a wrong verdict. |
| 7 | High | `CLAUDE_SECURESTORAGE_CONFIG_DIR` takes precedence over `CLAUDE_CONFIG_DIR` when Claude Code picks a keychain namespace (docs/mac-port.md §2), and the child inherited whatever the user's environment held. Every account would have collapsed onto one login while each still looked isolated. | A pinned account now pins both variables to the same canonical path. |
| 8 | High | The sweep signalled the positive pid, killing only the group leader; the `node` descendant was reparented and survived, and no later sweep would find it. | The sweep signals the process group, matching `terminate`. Verified live: the descendant now dies with it. |
| — | Reject-outright | Every `posix_spawn` setup call was unchecked (`attr_init`, `file_actions_init`, each `adddup2`/`addchdir`, every `strdup`), against a doc comment claiming "fail closed". A truncated `envp` can silently drop `CLAUDE_CONFIG_DIR`. | All checked; any failure closes every descriptor already opened and launches nothing. |
| — | Reject-outright | `ControlChannel` claimed to mirror the Windows channel while omitting its serialized writer, stderr pump, and timeout handling. | All three added. An undrained stderr is a child deadlock once the 64 KB buffer fills; it is now drained continuously with a bounded tail kept for diagnostics. |
| — | High | The reader thread's `[weak self]` was promoted to a strong reference by `guard let self` held across an infinite loop, so the channel could never deallocate and `deinit` never terminated the child. | `self` is re-acquired per iteration. |
| — | High | JSON booleans bridge to `NSNumber`: `{"percent": true}` read as **1.0** (a confident wrong "1 % used"), and `{"request_id": true}` stringified to `"1"`, satisfying a caller waiting on request 1. Verified against Foundation, not assumed. | Booleans are rejected via `CFGetTypeID == CFBooleanGetTypeID()` in both places. |
| — | High | `limits[]` took the first of duplicate `kind` entries, validated only the session percentage, and did not require a session reset — then reported `error: nil` and blocked fallback to valid exact keys. | Duplicate kinds reject the whole representation; session requires a valid percentage *and* a reset; weekly is validated independently. |
| — | Medium | Exact-key weekly utilization was not range-checked, so an out-of-range value travelled inside a successful snapshot. | Validated at the boundary; an invalid weekly becomes `nil` without harming a good session reading. |
| — | High | Timeouts, latency and grace periods used wall-clock `Date`, against this project's own monotonic-duration contract (round-1 decision 6). | `DispatchTime` throughout. |
| — | High | The sweep was called only by `quotaprobe`, so any other entry point silently skipped containment mechanism 3. | It now runs inside `ControlChannel.launch`. |
| — | High | A timeout left `isHealthy` true, so a supervisor would never recycle a channel with an unanswerable request outstanding. | A timeout faults the channel. |

## Rejected, with evidence

- **"`collect` recursively walks attacker-controlled JSON with no depth budget" (Medium).**
  `JSONSerialization` rejects deeply nested input before the parser ever sees it — measured: a
  5 000-deep array fails to decode with "not in the correct format". The traversal cannot be
  reached with input this deep. No change.
- **"Use a dedicated process broker/watchdog binary" (their preferred design).** This closes the
  same hole as the pid-record sweep, at the cost of a second executable to ship, sign and keep
  alive. The record approach was measured to cover the one real case (a `SIGSTOP`ped child
  orphaned onto launchd) and to leave a user's own `claude` untouched. Revisit if the record ever
  proves insufficient; not warranted at M0.
- **"An actor-owned transport instead of `@unchecked Sendable`" (their preferred design).** The
  properties the review wanted from an actor — serialized writes, pending-only correlation,
  cleanup on every exit path, monotonic deadlines, fault on timeout — are all now present. Moving
  to an actor is a larger change that would add no further guarantee here. Noted for when the
  supervisor lands.

## Found while verifying, not by the review

Fixing finding 2 introduced a bug that only a live test caught, and it is worth recording because
it is the exact failure mode this project's rules exist to prevent.

The new sweep confirmed identity partly by checking that the executable's name was `claude`.
`proc_pidpath` reports the **resolved** path, and `~/.local/bin/claude` is a symlink into
`~/.local/share/claude/versions/<version>` — so the last path component is `2.1.271`, never
`claude`. The check matched nothing and **silently disabled the entire sweep**. The unit tests
still passed: "nothing was killed" is what they asserted.

Two changes came out of it: the record stores the kernel's own view of the path, captured at
launch, and is compared like with like; and the test suite now asserts **both** directions — that
a matching record *is* swept, and that a mismatched one is not. A one-directional test would have
passed against dead code.

## Verified live after the fixes

- Reads the real account via `limits[]`, with the new cross-check agreeing against real data.
- A logged-out config directory still reads as "not logged in", never 0 %.
- Force-quitting the parent takes the child and its `node` descendant within 0.25 s.
- A `SIGSTOP`ped child orphaned onto launchd is swept at the next launch, **and its descendant
  with it**.
- An orphaned `claude` that the *user* started, from their own repo, is left alone.
- 47 unit tests green.
