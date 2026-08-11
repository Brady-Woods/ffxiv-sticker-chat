using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace FfxivStickerChat;

/// <summary>One selectable auto-translate phrase.</summary>
public sealed record AutoTranslateEntry(uint Group, uint Key, string Text, string GroupTitle)
{
    /// <summary>Label for a dropdown: the phrase, qualified by its category.</summary>
    public string Display => string.IsNullOrEmpty(GroupTitle) ? Text : $"{Text}  ({GroupTitle})";
}

/// <summary>
/// The game's full auto-translate dictionary, read from the <c>Completion</c> sheet.
/// </summary>
/// <remarks>
/// Bindings previously came only from phrases observed in chat, which meant you could not create one
/// until someone happened to send it. The sheet is the same source the game's own auto-translate window
/// uses, so every phrase is available up front.
/// </remarks>
public static class AutoTranslateCatalog
{
    private static List<AutoTranslateEntry>? cache;

    /// <summary>Every usable phrase, sorted by category then text.</summary>
    public static IReadOnlyList<AutoTranslateEntry> All => cache ??= Load();

    /// <summary>Phrases whose text contains <paramref name="filter"/>, case-insensitively.</summary>
    public static IEnumerable<AutoTranslateEntry> Search(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return All;

        return All.Where(e =>
            e.Text.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            e.GroupTitle.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Finds the catalogue entry for a group/key pair, or null if it is not a plain phrase.</summary>
    public static AutoTranslateEntry? Find(uint group, uint key)
        => All.FirstOrDefault(e => e.Group == group && e.Key == key);

    private static List<AutoTranslateEntry> Load()
    {
        var entries = new List<AutoTranslateEntry>();

        try
        {
            var sheet = Services.DataManager.GetExcelSheet<Completion>();
            if (sheet is null)
                return entries;

            foreach (var row in sheet)
            {
                var text = row.Text.ExtractText();
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                // A non-empty LookupTable means the row is a category that expands into another sheet
                // (every action, every place name, and so on) rather than a phrase you can send.
                if (!string.IsNullOrEmpty(row.LookupTable.ExtractText()))
                    continue;

                entries.Add(new AutoTranslateEntry(
                    row.Group,
                    row.Key,
                    text,
                    row.GroupTitle.ExtractText()));
            }

            entries.Sort((a, b) =>
            {
                var byGroup = string.CompareOrdinal(a.GroupTitle, b.GroupTitle);
                return byGroup != 0 ? byGroup : string.CompareOrdinal(a.Text, b.Text);
            });

            Services.Log.Information($"Auto-translate catalogue: {entries.Count} phrases.");
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Could not read the Completion sheet");
        }

        return entries;
    }
}
