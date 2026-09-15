# Panel v2 — answer first

Replaces the "Panel (complete)" section of `docs/forecast-and-states.md`.

**Why:** the user couldn't tell from v1 whether there was a problem, or when. The verdict sat in small
grey text, the forecast wedge in the ring read as a rendering glitch, and the timeline showed elapsed
time but not usage. The only question the panel exists to answer is: **will I break the budget
before it resets, and when does it reset?** It must answer that at the top, in words, every time.

## Layout (top to bottom, 340 px wide, height fits content)

### 1. Header
"Claude Code", the freshness line ("Uppdaterad för 1 min sedan", warning style when Stale), and the
DEMO badge in demo mode.

### 2. Status box — ALWAYS shown, driven by the most severe window (IconSeverity)

A coloured box (tinted background, state-coloured left edge or icon). Line 1 is the verdict: large,
bold, in the state colour. Line 2 always states **when**.

| State | Line 1 | Line 2 |
|---|---|---|
| Safe | ✓ Kvoten räcker till reset | Sessionen landar på ~57 % · återställs kl 13:20 (om 1 h 1 min) |
| Tight | Tajt — kvoten räcker precis | Sessionen landar på ~95 % · återställs kl 13:20 (om 1 h 1 min) |
| DryEarly | ⚠ Kvoten tar slut kl 12:36 | 44 min före reset kl 13:20, om du fortsätter i samma takt |
| Spent | Kvoten är slut | Öppnar igen kl 13:20 (om 1 h 41 min) |
| Measuring | Mäter takt… | 3 % använt · återställs kl 13:20 (om 4 h 52 min) |
| Stale | (verdict as above) + line 3: "Datan kan vara inaktuell — senast ändrad för 23 min sedan" |
| Unknown | Kan inte läsa kvoten | Senast avläst kl 12:10 · försöker igen |

- When the weekly window drives it, say "Veckokvoten" and use weekday times: "⚠ Veckokvoten tar slut
  tis 14:00", "2 d 17 h före reset fre 07:00, om du fortsätter i samma takt".
- If the window that is NOT driving the box is Tight/DryEarly/Spent, add one short line for it.
- A reset time always includes both the clock time and the countdown. The countdown ticks at 1 Hz.
- The copy stays conditional ("i nuvarande takt" / "om du fortsätter i samma takt") because the
  model extrapolates.

### 3. One section per window: session first, then weekly

```
AKTUELL SESSION   (small ring, same geometry as the tray icon, ~28 px)
                                     återställs om 1 h 1 min · kl 13:20
Tid   ████████████████████████░░░░░░   80 % av 5 h har gått
Kvot  █████████████▒▒▒▒░░░░░░░░░░░░░   45 % använt → ~57 % vid reset
```

- **Both bars share one 0–100 % scale and are left-aligned**, so "quota bar shorter than time bar =
  fine" can be read directly.
- **Tid bar**: elapsed fraction `(W − t_reset) / W`, neutral colour. Label: "80 % av 5 h har gått" /
  "4 % av veckan har gått".
- **Kvot bar**: used part solid in the state colour, then a forecast segment (same colour, ~40 % alpha,
  thin outline) to `min(projected, 100)`. When `projected > 100`, draw the forecast segment to the end
  in the DryEarly colour and put a marker at the end of the bar. Label, right of or under the bar:
  - Safe/Tight: "45 % använt → ~57 % vid reset"
  - DryEarly: "56 % använt → slut kl 12:36"
  - Measuring: "9 % använt · för tidigt för prognos" (no forecast segment)
  - Spent: "100 % · slut" (bar full, Spent colour)
  - Awaiting reset: "Nytt fönster väntas"
- Weekly times use a weekday: "återställs om 6 d 18 h · fre 07:00".
- All labels stay inside the panel bounds. Never clip.

### 4. Footer
"Senast avläst kl 12:17:47 · uppdateras var 30 s".

## Time text (one shared formatter, used everywhere, including the tooltip)

Never show raw minutes above an hour ("6975 min" is unreadable).

- **Durations**: under 2 min → "1 min 30 s"; under 1 h → "44 min"; under 24 h → "20 h 15 min"
  (drop "0 min"); 24 h and up → "4 d 20 h" (drop minutes).
- **Points in time**: today → "kl 12:36"; tomorrow → "i morgon kl 10:44"; later → weekday
  "sön kl 10:44". Weekly resets always carry the weekday.
- **Running out always states both *when* and *how long until*:**
  - Status box, DryEarly: line 1 "⚠ Veckokvoten tar slut sön kl 10:44 (om 1 d 22 h)", line 2
    "4 d 20 h före reset fre kl 07:00, om du fortsätter i samma takt".
  - Kvot bar label: "11 % använt → slut sön 10:44 (om 1 d 22 h)".
- Resets: "återställs fre kl 07:00 (om 6 d 18 h)" / "återställs kl 13:20 (om 37 min)".

## Removed
- The big ring cards: their % is duplicated by the bars, and their forecast wedge confused the user.
- The session and weekly timelines: replaced by the Tid bar.

## Tray tooltip (≤ 63 characters)
It carries the same verdict and time as the status box, e.g. "Räcker · ~57 % vid reset 13:20",
"Slut kl 12:36 — 44 min före reset", "Kvoten slut · öppnar 13:20", "Mäter takt · reset 13:20".
Tight uses the same shape as Safe: "Tajt · ~95 % vid reset 13:20".

## Implementation notes (decisions the sections above don't pin down)

These were filled in during the panel-v2 build (`Ui/TimeText.cs`, `Ui/PanelText.cs`,
`Ui/PanelForm.cs`) where the spec above describes the *shape* of the output but not
every case:

- **Which window drives when severity ties.** The status box picks the more severe
  window (session vs weekly); on an exact tie it picks the session window. Same
  rule for which Measuring window's reason is shown when neither carries one of the
  two "special" reasons (AwaitingReset / TooEarlyInWeek).
- **The weekly-drives word substitution** ("say Veckokvoten") is applied
  consistently across all five status-box rows (Safe/Tight/DryEarly/Spent/Measuring),
  not just DryEarly where the doc's only example lives: "Kvoten"/"kvoten" ->
  "Veckokvoten"/"veckokvoten", and "Sessionen" -> "Veckan" in the landing-percentage
  clause.
- **Window section titles**: "AKTUELL SESSION" and "VECKA" (uppercase, matching the
  layout sketch's session title; the weekly one isn't spelled out above).
- **Secondary window line wording** (item 2's "add one short line for it" has no
  literal example): "Dessutom tajt: {subject} räcker precis · återställs …",
  "Dessutom: {subject} tar slut …", "Dessutom: {subject} är slut · öppnar …".
- **Measuring row, special reasons.** When the driving window's MeasuringReason is
  AwaitingReset ("Nytt fönster väntas"), line 2 is that reason alone -- the window's
  own reset has already passed, so pairing it with a stale/negative countdown would
  be worse than omitting it. TooEarlyInWeek is instead folded into the generic
  "X % använt · återställs …" line as an extra clause, since its reset is a genuine
  future time worth stating. The same AwaitingReset/TooEarlyInWeek gate decides the
  per-window Kvot-bar label (reason text alone vs "X % använt · för tidigt för
  prognos").
- **"Running out" states the countdown on every day, not just later ones.** An
  earlier revision had the status box's DryEarly line 1, the Kvot-bar label and the
  secondary "Dessutom: … tar slut …" line drop "(om …)" for a today depletion,
  carrying it only on a later/weekday one. That was inconsistent with "always
  states both when and how long until" and has been corrected: all three surfaces
  say "kl HH:mm (om …)" for today, exactly like the later-day "{weekday} kl HH:mm
  (om …)" form, via the shared `TimeText.Depletion` helper.
- **Kvot-bar depletion wording still differs from the status box's** by design,
  following the two literal examples in this doc exactly as written: the status
  box's DryEarly line 1 says "kl HH:mm (om …)" (today) or "{weekday} kl HH:mm
  (om …)" (later); the Kvot-bar label instead drops "kl" before the weekday
  ("slut sön 10:44 (om …)") -- so it builds its own string rather than calling
  `TimeText.Depletion`, which always keeps "kl". The section-3 reset header follows
  the same weekday-has-no-"kl" spelling ("återställs om 6 d 18 h · fre 07:00").
- **A reset or depletion at or before now renders as "nu"**, never "(om 0 s)" or a
  negative duration (`TimeText.Reset`, `TimeText.Depletion`, `TimeText.
  CountdownFragment`) -- a race between the 1 Hz redraw and the next poll, or a
  window genuinely awaiting its next observation, must never show a stale/negative
  countdown.
- **A window section whose reset has passed and no later window has been observed
  yet** (`MeasuringReason` is AwaitingReset) replaces its whole reset header and Tid
  label rather than computing them from the stale `ResetsAt`/`UsedPct`: header
  "väntar på nytt fönster", Tid bar drawn full with label "fönstret är slut", Kvot
  bar drawn EMPTY (not the old window's usage) with label "Nytt fönster väntas" --
  same reason text the Kvot-bar label already used. The status box's own handling
  of this case (line 2 is the reason alone) is unchanged.
