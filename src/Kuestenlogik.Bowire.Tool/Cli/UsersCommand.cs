// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Projects;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// <c>bowire users</c> — the operator's side of per-identity storage (#97).
/// </summary>
/// <remarks>
/// <para>
/// The workbench asks the person whose data it is. This asks on their behalf,
/// from the host, which is the only place some of these questions can be
/// answered: an operator flipping an install to multi-tenant wants to see what
/// would move before anyone signs in, and an admin who accepted a migration
/// into their own account needs to give it back without a browser session as
/// the person it belongs to.
/// </para>
/// <para>
/// A subject is named rather than inferred. There is no request here, so
/// there is no caller to be — and guessing "the only slot on disk" would be
/// right until the day it silently was not.
/// </para>
/// </remarks>
internal static class UsersCommand
{
    public static Command Build()
    {
        var users = new Command("users",
            "Inspect and migrate per-identity storage (#97). Multi-tenant installs give each authenticated identity its own slot under <storage root>/users/; these commands show what is there and move a single-user install's data into one.");
        users.Add(BuildListCommand());
        users.Add(BuildMigrateCommand());
        return users;
    }

    // ---------- list ----------

    private static Command BuildListCommand()
    {
        var list = new Command("list",
            "Show the identity slots that exist on disk, with whatever migration each one has on record.");
        list.SetAction((pr, _) => Task.FromResult(
            RunList(pr.InvocationConfiguration.Output)));
        return list;
    }

    private static int RunList(TextWriter output)
    {
        var root = StorageRoot();
        var usersRoot = Path.Combine(root, BowireUserSlot.DirectoryName);

        if (!Directory.Exists(usersRoot))
        {
            output.WriteLine($"No identity slots under {usersRoot}.");
            output.WriteLine("This install is single-user, or nobody has signed in yet.");
            return 0;
        }

        var slots = Directory.EnumerateDirectories(usersRoot)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name) && name[0] != '.')
            .Order(StringComparer.Ordinal)
            .ToList();

        if (slots.Count == 0)
        {
            output.WriteLine($"No identity slots under {usersRoot}.");
            return 0;
        }

        output.WriteLine($"{slots.Count} slot(s) under {usersRoot}:");
        output.WriteLine();
        foreach (var slot in slots)
        {
            var receipt = BowireUserMigrator.ReadReceipt(Path.Combine(usersRoot, slot!));
            var note = receipt is null
                ? "no migration on record"
                : receipt.Outcome == BowireUserMigrationOutcome.Migrated
                    ? $"migrated {receipt.DecidedUtc:yyyy-MM-dd}, {receipt.Files} file(s), {receipt.Bytes} byte(s)"
                    : $"declined {receipt.DecidedUtc:yyyy-MM-dd}";
            output.WriteLine($"  {slot,-44}  {note}");
        }

        // The slug is one-way on purpose, so the subject is not recoverable
        // from disk. Say so rather than let the reader assume it is missing.
        output.WriteLine();
        output.WriteLine("Slot names are derived from the subject and cannot be read back into one.");
        return 0;
    }

    // ---------- migrate ----------

    private static Command BuildMigrateCommand()
    {
        var subject = new Argument<string>("subject")
        {
            Description = "The authenticated subject, exactly as the identity provider issues it (the sub claim) — e.g. ada@example.com or auth0|5f3c9a.",
        };
        var apply = new Option<bool>("--apply")
        { Description = "Copy the legacy single-user state into this subject's slot." };
        var decline = new Option<bool>("--decline")
        { Description = "Record that this subject does not want it, so the workbench stops offering." };
        var undo = new Option<bool>("--undo")
        { Description = "Take back a decision. An accepted migration's slot is moved aside, not deleted; a declined one just loses its record." };

        var migrate = new Command("migrate",
            "Show what migrating the single-user state into a subject's slot would do, and carry it out. Without a flag it only reports — nothing on disk changes.");
        migrate.Add(subject);
        migrate.Add(apply);
        migrate.Add(decline);
        migrate.Add(undo);

        migrate.SetAction((pr, _) => Task.FromResult(RunMigrate(
            pr.GetValue(subject) ?? "",
            pr.GetValue(apply),
            pr.GetValue(decline),
            pr.GetValue(undo),
            pr.InvocationConfiguration.Output,
            pr.InvocationConfiguration.Error)));

        return migrate;
    }

    private static int RunMigrate(
        string subject, bool apply, bool decline, bool undo, TextWriter output, TextWriter error)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            error.WriteLine("A subject is required.");
            return 1;
        }

        var chosen = (apply ? 1 : 0) + (decline ? 1 : 0) + (undo ? 1 : 0);
        if (chosen > 1)
        {
            error.WriteLine("Choose one of --apply, --decline or --undo.");
            return 1;
        }

        var plan = BowireUserMigrator.Plan(StorageRoot(), subject);
        Describe(plan, output);

        if (chosen == 0)
        {
            output.WriteLine();
            output.WriteLine(plan.State switch
            {
                BowireUserMigrationState.Available =>
                    "Run again with --apply to copy it, or --decline to record that it is not wanted.",
                BowireUserMigrationState.AlreadyDecided =>
                    "Run again with --undo to take that back.",
                _ => "Nothing to do.",
            });
            return 0;
        }

        try
        {
            if (apply)
            {
                var receipt = BowireUserMigrator.Apply(plan);
                output.WriteLine();
                output.WriteLine($"Copied {receipt.Files} file(s), {receipt.Bytes} byte(s) into {plan.Slot}.");
                output.WriteLine($"The originals are untouched under {plan.StorageRoot}; delete them when you are satisfied.");
                return 0;
            }

            if (decline)
            {
                BowireUserMigrator.Decline(plan);
                output.WriteLine();
                output.WriteLine("Recorded. The workbench will not offer this again for that subject.");
                return 0;
            }

            var aside = BowireUserMigrator.Undo(plan);
            output.WriteLine();
            output.WriteLine(aside is null
                ? "Record removed. The migration is on offer again."
                : $"Slot moved to {aside}. Nothing was deleted — the migration is on offer again.");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            // The plan already said so; this is the same refusal with the
            // reason attached, for the reader who passed the flag anyway.
            error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Could not finish: {ex.Message}");
            error.WriteLine("Nothing was moved into place; the originals are untouched.");
            return 1;
        }
    }

    private static void Describe(BowireUserMigrationPlan plan, TextWriter output)
    {
        output.WriteLine($"Subject:  {plan.Subject}");
        output.WriteLine($"Slot:     {plan.Slot}");
        output.WriteLine($"Source:   {plan.StorageRoot}");
        output.WriteLine($"State:    {plan.State}");

        if (plan.Receipt is not null)
        {
            output.WriteLine($"Decided:  {plan.Receipt.Outcome} on {plan.Receipt.DecidedUtc:u}");
        }

        if (plan.Entries.Count == 0) return;

        output.WriteLine($"Files:    {plan.Entries.Count} ({plan.Bytes} byte(s))");
        // Enough to recognise whose data this is, not so much that it scrolls
        // the verdict off the screen.
        foreach (var entry in plan.Entries.Take(10))
        {
            output.WriteLine($"            {entry.RelativePath}");
        }
        if (plan.Entries.Count > 10)
        {
            output.WriteLine($"            … and {plan.Entries.Count - 10} more");
        }
    }

    private static string StorageRoot() => BowirePaths.Root(BowireStorageScope.Data);
}
