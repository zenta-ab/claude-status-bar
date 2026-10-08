using ClaudeStatusBar.Data;
using ClaudeStatusBar.Diagnostics;

namespace ClaudeStatusBar.Config;

/// <summary>
/// accounts.json is metadata; the slot folders are the source of truth (docs/multi-account.md
/// "Reconciliation"). This turns the one into the other, on every start and after every change:
///
///   1. Migrate a version 1 file (configDir paths) to version 2 (slot ids).
///   2. `retired` folders: finish deleting them (leave them for the next start when locked); never adopt.
///   3. `pending` folders: a login that was interrupted between the browser and the save. If the
///      config dir holds an oauthAccount that is not a duplicate of an active slot, it is adopted
///      as active; otherwise it is retired and deleted.
///   4. accounts.json entries whose folder is missing, or not active, are dropped.
///   5. `active` folders that accounts.json does not list are appended (enabled) -- the recovery
///      path after a lost or quarantined accounts.json, so no login is ever orphaned.
///
/// Folders without a slot.json that no version 1 entry referenced are legacy folders this app did
/// not create: not adopted, not deleted, not touched.
///
/// One bad slot never aborts the pass: each slot is handled on its own and a failure (a junction
/// the path guard refuses, a folder that cannot be read) is logged and that slot is skipped, so
/// everything else is still reconciled.
/// </summary>
public static class AccountReconciler
{
    public sealed record Result(bool Changed, int Adopted, int Dropped, int Retired, int Appended);

    /// <param name="config">Modified in place.</param>
    /// <param name="slots">The slot store whose folders are the truth.</param>
    /// <param name="readIdentity">Test seam; defaults to AccountIdentity.ReadFrom (the oauthAccount in the slot's .claude.json, never a token).</param>
    /// <param name="protectedSlots">Slots a running login flow owns right now (the pending slot a login console is writing into); they are left exactly as they are, in EVERY pass.</param>
    public static Result Reconcile(
        AccountsConfig config,
        SlotStore slots,
        Func<string, AccountIdentity?>? readIdentity = null,
        IReadOnlySet<string>? protectedSlots = null)
    {
        readIdentity ??= AccountIdentity.ReadFrom;
        int adopted = 0, dropped = 0, retired = 0, appended = 0;
        bool changed = false;

        if (config.Version < AccountsConfig.CurrentVersion)
        {
            dropped += Migrate(config, slots);
            changed = true;
        }

        // 2 + 3: act on the folders themselves first, then read the resulting truth.
        IReadOnlyList<SlotInfo> found = slots.Enumerate();
        var activeIdentityKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (SlotInfo info in found)
        {
            if (info.Marker?.State != SlotState.Active) continue;
            try
            {
                if (readIdentity(slots.ConfigDirOf(info.Id)) is { } identity) activeIdentityKeys.Add(identity.StateKey);
            }
            catch (Exception ex)
            {
                SafeLog.Warn($"reconcile: slot {info.Id} skipped while reading identities ({ex.GetType().Name})");
            }
        }

        foreach (SlotInfo info in found)
        {
            if (protectedSlots?.Contains(info.Id) == true) continue;

            try
            {
                if (info.Marker?.State == SlotState.Retired)
                {
                    slots.TryDelete(info.Id); // a locked folder is simply tried again next time
                    retired++;
                }
                else if (info.Marker?.State == SlotState.Pending)
                {
                    AccountIdentity? identity = readIdentity(slots.ConfigDirOf(info.Id));
                    if (identity is not null && activeIdentityKeys.Add(identity.StateKey))
                    {
                        slots.WriteMarker(info.Id, SlotState.Active);
                        adopted++;
                        SafeLog.Info($"slot {info.Id}: interrupted login adopted (key={AccountIdentity.KeyPrefixOf(identity.StateKey)})");
                    }
                    else
                    {
                        slots.WriteMarker(info.Id, SlotState.Retired);
                        slots.TryDelete(info.Id);
                        retired++;
                        SafeLog.Info($"slot {info.Id}: interrupted login discarded");
                    }
                }
            }
            catch (Exception ex)
            {
                SafeLog.Warn($"reconcile: slot {info.Id} skipped ({ex.GetType().Name}); the rest is reconciled");
            }
        }

        // 4 + 5: entries follow the folders.
        IReadOnlyList<SlotInfo> after = slots.Enumerate();
        var activeById = after
            .Where(s => s.Marker?.State == SlotState.Active)
            .ToDictionary(s => s.Id, StringComparer.Ordinal);

        var kept = new List<AccountEntry>();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (AccountEntry entry in config.Accounts)
        {
            if (entry.Slot is { } slot && activeById.ContainsKey(slot) && listed.Add(slot))
            {
                kept.Add(entry);
            }
            else
            {
                dropped++;
                SafeLog.Info("accounts.json entry dropped: its slot is missing, not active, or listed twice");
            }
        }

        foreach (SlotInfo info in activeById.Values.OrderBy(s => s.Marker!.CreatedUtc).ThenBy(s => s.Id, StringComparer.Ordinal))
        {
            if (!listed.Add(info.Id)) continue;
            kept.Add(new AccountEntry { Slot = info.Id, Enabled = true });
            appended++;
        }

        if (dropped > 0 || appended > 0 || adopted > 0 || retired > 0) changed = true;
        config.Accounts = kept;
        config.Version = AccountsConfig.CurrentVersion;
        return new Result(changed, adopted, dropped, retired, appended);
    }

    /// <summary>
    /// Version 1 -> 2. An entry whose configDir -- put into canonical form first (full path,
    /// separators, trailing separator, 8.3 names; SlotStore.CanonicalizeLegacyPath) -- passes the
    /// path guard becomes { slot: id }, and if that folder has no slot.json one is written as
    /// `active` (the user referenced the folder, so the user chose it). An entry that fails the
    /// guard, and the old follower entry (`configDir: null`, "whatever I am logged into" -- no
    /// longer supported), is dropped and logged. Returns the number of entries dropped.
    /// </summary>
    public static int Migrate(AccountsConfig config, SlotStore slots)
    {
        int dropped = 0;
        var migrated = new List<AccountEntry>();

        foreach (AccountEntry entry in config.Accounts)
        {
            string? legacy = entry.LegacyConfigDir;
            entry.LegacyConfigDir = null;

            if (!string.IsNullOrEmpty(entry.Slot))
            {
                migrated.Add(entry); // already slot-shaped (hand-edited): reconciliation checks it
                continue;
            }

            if (string.IsNullOrEmpty(legacy))
            {
                dropped++;
                SafeLog.Info("migration: dropped an entry that followed the default login (no longer supported)");
                continue;
            }

            try
            {
                if (!slots.TryGuardConfigDir(slots.CanonicalizeLegacyPath(legacy), out string? slotId, out string? configDir))
                {
                    dropped++;
                    SafeLog.Warn("migration: dropped an entry whose configDir is not an app-owned slot");
                    continue;
                }

                if (!Directory.Exists(configDir))
                {
                    dropped++;
                    SafeLog.Warn($"migration: dropped slot {slotId}: its folder does not exist");
                    continue;
                }

                if (slots.ReadMarker(slotId) is null)
                    slots.WriteMarker(slotId, SlotState.Active);

                entry.Slot = slotId;
                migrated.Add(entry);
            }
            catch (Exception ex)
            {
                dropped++;
                SafeLog.Warn($"migration: dropped an entry that could not be migrated ({ex.GetType().Name})");
            }
        }

        config.Accounts = migrated;
        config.Version = AccountsConfig.CurrentVersion;
        return dropped;
    }
}
