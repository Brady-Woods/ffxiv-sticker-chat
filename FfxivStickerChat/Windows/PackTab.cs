using System;
using System.IO;
using System.Threading.Tasks;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using System.Collections.Generic;
using FfxivStickerChat.Packs;

namespace FfxivStickerChat.Windows;

/// <summary>
/// Pack management: bindings, sender scoping, and sharing by zip.
/// </summary>
public sealed class PackTab
{
    private const float PreviewSize = 48f;

    private readonly Plugin plugin;
    private readonly PackStore store;
    private readonly StickerRegistry registry;
    private readonly FileDialogManager fileDialogs;

    private string phraseFilter = string.Empty;
    private string status = string.Empty;
    private string? selectedPackId;
    private string? pendingDeleteId;
    private string newPackName = string.Empty;
    private string importUrl = string.Empty;
    private bool downloading;
    private string pendingDeleteWarning = string.Empty;

    public PackTab(Plugin plugin, StickerRegistry registry, FileDialogManager fileDialogs)
    {
        this.plugin = plugin;
        this.registry = registry;
        this.fileDialogs = fileDialogs;
        store = plugin.PackStore;
    }

    public void Draw()
    {
        DrawPackList();
        ImGui.Separator();

        ImGui.TextDisabled(
            $"Stickers: .png, one side exactly {StickerLimits.RequiredEdge}px and the other " +
            $"{StickerLimits.RequiredEdge}px or less, up to {StickerLimits.MaxFileBytes / 1024} KB each, " +
            $"{StickerLimits.MaxEntriesPerPack} per pack.");

        var pack = GetSelectedPack();
        if (pack is null)
        {
            ImGui.TextDisabled("Select a pack above.");
            return;
        }

        DrawPackDetails(pack);
        ImGui.Separator();
        DrawEntries(pack);
    }

    private StickerPack? GetSelectedPack()
    {
        if (selectedPackId is not null)
        {
            var found = store.Get(selectedPackId);
            if (found is not null)
                return found;
        }

        var local = store.GetOrCreateLocal();
        selectedPackId = local.Id;
        return local;
    }

    private void DrawPackList()
    {
        ImGui.TextUnformatted($"Packs ({store.Packs.Count})");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(160);
        ImGui.InputTextWithHint("##newname", "new pack name", ref newPackName, 128);

        ImGui.SameLine();
        if (ImGui.Button("Create"))
        {
            var created = store.CreateLocal(newPackName);
            selectedPackId = created.Id;
            status = $"Created \"{created.Name}\".";
            newPackName = string.Empty;
        }

        ImGui.SameLine();
        if (ImGui.Button("Import from zip..."))
        {
            fileDialogs.OpenFileDialog(
                "Import sticker pack",
                "Sticker pack{.zip}",
                (ok, selected) =>
                {
                    if (!ok || selected.Count == 0)
                        return;

                    var result = PackArchive.Import(selected[0], store);
                    status = result.Message;

                    if (result.Success && result.Pack is not null)
                        selectedPackId = result.Pack.Id;
                },
                1,
                string.Empty);
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##url", "https://.../pack.zip", ref importUrl, 512);

        ImGui.SameLine();
        using (ImRaii.Disabled(downloading || string.IsNullOrWhiteSpace(importUrl)))
        {
            if (ImGui.Button(downloading ? "Downloading..." : "Import from URL"))
                BeginUrlImport();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Must be a direct https link to the zip.\n" +
                "Cloud drive share links usually serve a preview page rather than the file.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Open folder"))
        {
            store.EnsureDirectories();
            Util.OpenFolder(store.RootDirectory);
        }

        if (pendingDeleteId is not null && !string.IsNullOrEmpty(pendingDeleteWarning))
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), pendingDeleteWarning);
        else if (!string.IsNullOrEmpty(status))
            ImGui.TextWrapped(status);

        if (!ImGui.BeginTable("##packs", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableSetupColumn("Pack", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Bindings", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Owner", ImGuiTableColumnFlags.WidthFixed, 170);
        ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableHeadersRow();

        foreach (var pack in store.Packs)
        {
            ImGui.TableNextRow();
            ImGui.PushID(pack.Id);

            ImGui.TableNextColumn();
            var on = pack.Enabled;
            if (ImGui.Checkbox("##enabled", ref on))
            {
                pack.Enabled = on;
                store.Save(pack);
            }

            ImGui.TableNextColumn();
            var label = pack.IsLocal ? $"{pack.Name}  (mine)" : pack.Name;
            if (ImGui.Selectable(label, selectedPackId == pack.Id))
                selectedPackId = pack.Id;

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(pack.Entries.Count.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(pack.OwnerDisplay);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatSize(store.GetPackSize(pack.Id)));

            ImGui.TableNextColumn();
            if (pendingDeleteId == pack.Id)
            {
                if (ImGui.SmallButton("Confirm"))
                {
                    var name = pack.Name;
                    status = store.Delete(pack.Id)
                        ? $"Deleted \"{name}\" and its images."
                        : $"Could not delete \"{name}\".";

                    pendingDeleteId = null;
                    pendingDeleteWarning = string.Empty;

                    if (selectedPackId == pack.Id)
                        selectedPackId = null;
                }

                ImGui.SameLine();
                if (ImGui.SmallButton("No"))
                {
                    pendingDeleteId = null;
                    pendingDeleteWarning = string.Empty;
                }

                // Deleting takes the pack's images with it, and for an authored pack those are the
                // originals — worth stating plainly rather than hiding behind a hover.
                pendingDeleteWarning = pack.IsLocal
                    ? $"Delete \"{pack.Name}\" and its {pack.Entries.Count} image(s)? " +
                      "This is your own pack; export it first if you want a copy."
                    : $"Delete \"{pack.Name}\" and its {pack.Entries.Count} image(s)?";
            }
            else if (ImGui.SmallButton("Delete"))
            {
                pendingDeleteId = pack.Id;
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// Downloads a pack off the UI thread and reports the outcome back through the status line.
    /// </summary>
    private void BeginUrlImport(string? expectedHash = null)
    {
        var url = importUrl.Trim();
        downloading = true;
        status = "Downloading...";

        _ = Task.Run(async () =>
        {
            var result = await PackDownloader
                .DownloadAndImportAsync(url, store, expectedHash)
                .ConfigureAwait(false);

            // Touching store state and UI fields belongs on the framework thread.
            await Services.Framework.Run(() =>
            {
                downloading = false;
                status = result.Message;

                if (result.Success && result.Pack is not null)
                {
                    selectedPackId = result.Pack.Id;
                    importUrl = string.Empty;
                }
            }).ConfigureAwait(false);
        });
    }

    private void DrawPackDetails(StickerPack pack)
    {
        if (pack.IsLocal)
        {
            // Stamping is lazy: at the title screen there is no character to name the pack after.
            if (store.StampOwner(pack))
                store.Save(pack);

            ImGui.SetNextItemWidth(240);
            var name = pack.Name;
            if (ImGui.InputText("Name", ref name, 128))
            {
                pack.Name = name;
                store.Save(pack);
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(180);
            var author = pack.Author;
            if (ImGui.InputText("Author", ref author, 128))
            {
                pack.Author = author;
                store.Save(pack);
            }
        }
        else
        {
            ImGui.TextUnformatted(pack.Name);

            if (!string.IsNullOrEmpty(pack.Author))
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"by {pack.Author}");
            }
        }

        ImGui.TextUnformatted("Owner:");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), pack.OwnerDisplay);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Stamped from the character the pack was created on and carried through export, so an\n" +
                "imported pack pairs to its author automatically. Not editable.");
        }

        if (pack.IsLocal)
        {
            ImGui.SetNextItemWidth(430);
            var source = pack.SourceUrl;
            if (ImGui.InputTextWithHint("Download URL", "optional https link where you host this pack", ref source, 512))
            {
                pack.SourceUrl = source.Trim();
                store.Save(pack);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Travels with the pack when exported, so whoever imports it can pull your updates\n" +
                    "without you sending the file again.");
            }

            if (!string.IsNullOrEmpty(pack.ArchiveHash))
            {
                ImGui.TextDisabled($"archive hash: {pack.ArchiveHash}");

                ImGui.SameLine();
                if (ImGui.SmallButton("Copy"))
                    ImGui.SetClipboardText(pack.ArchiveHash);

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        "Hash of the zip produced by the last export. Publish it alongside the URL so\n" +
                        "recipients can verify the download and tell a stale copy from a current one.");
                }
            }
        }
        else if (!string.IsNullOrEmpty(pack.SourceUrl))
        {
            ImGui.TextDisabled($"source: {pack.SourceUrl}");

            ImGui.SameLine();
            using (ImRaii.Disabled(downloading))
            {
                if (ImGui.Button(downloading ? "Updating..." : "Update from source"))
                {
                    importUrl = pack.SourceUrl;

                    // No expected hash here: an update is by definition a different archive than the one
                    // held, so verifying against the current hash would reject every real update. Sync
                    // will supply the author's advertised hash for this.
                    BeginUrlImport();
                }
            }
        }

        if (pack.IsLocal && ImGui.Button("Export to zip..."))
        {
            fileDialogs.SaveFileDialog(
                "Export sticker pack",
                ".zip",
                SanitiseFileName(pack.Name) + ".zip",
                ".zip",
                (ok, selected) =>
                {
                    if (!ok || string.IsNullOrWhiteSpace(selected))
                        return;

                    status = PackArchive.Export(pack, store, selected).Message;
                });
        }

        if (pack.IsLocal)
            ImGui.SameLine();

        if (pack.IsLocal && ImGui.Button("Clean unused images"))
        {
            var removed = store.PruneUnusedMedia(pack);
            status = removed == 0 ? "No unused images." : $"Removed {removed} unused image(s).";
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"v{pack.Version}");
    }

    private void DrawEntries(StickerPack pack)
    {
        var atLimit = pack.Entries.Count >= StickerLimits.MaxEntriesPerPack;

        ImGui.TextUnformatted($"Stickers ({pack.Entries.Count}/{StickerLimits.MaxEntriesPerPack})");
        ImGui.SameLine();

        if (!pack.IsLocal)
        {
            // You author only your own pack. Someone else's is shown as it arrived, so what you see is
            // what their bubbles will use.
            ImGui.TextDisabled("read-only - this pack belongs to " + pack.OwnerDisplay);
        }
        else if (atLimit)
        {
            ImGui.TextDisabled("pack is full");
        }
        else if (ImGui.Button("Add sticker"))
        {
            pack.Entries.Add(new PackEntry());
            store.Save(pack);
        }

        PackEntry? toRemove = null;

        for (var i = 0; i < pack.Entries.Count; i++)
        {
            var entry = pack.Entries[i];
            ImGui.PushID(i);
            ImGui.Separator();

            DrawPreview(pack, entry);

            ImGui.SameLine();
            ImGui.BeginGroup();

            var on = entry.Enabled;
            if (ImGui.Checkbox("##entryEnabled", ref on))
            {
                entry.Enabled = on;
                store.Save(pack);
            }

            ImGui.SameLine();

            if (pack.IsLocal)
            {
                DrawPhrasePicker(pack, entry);

                ImGui.SameLine();
                if (ImGui.Button("Remove"))
                    toRemove = entry;
            }
            else
            {
                ImGui.TextUnformatted(string.IsNullOrEmpty(entry.Phrase) ? "(no phrase)" : entry.Phrase);
            }

            DrawImageRow(pack, entry);

            ImGui.EndGroup();
            ImGui.PopID();
        }

        if (toRemove is not null)
        {
            pack.Entries.Remove(toRemove);
            store.Save(pack);
        }
    }

    private void DrawPreview(StickerPack pack, PackEntry entry)
    {
        var path = store.ResolveMedia(pack, entry);
        if (path is null)
        {
            ImGui.Dummy(new Vector2(PreviewSize, PreviewSize));
            return;
        }

        var wrap = Services.TextureProvider.GetFromFile(path).GetWrapOrDefault();
        if (wrap is null || wrap.Size.X <= 0 || wrap.Size.Y <= 0)
        {
            ImGui.Dummy(new Vector2(PreviewSize, PreviewSize));
            return;
        }

        var scale = Math.Min(PreviewSize / wrap.Size.X, PreviewSize / wrap.Size.Y);
        ImGui.Image(wrap.Handle, wrap.Size * scale);
    }

    private void DrawPhrasePicker(StickerPack pack, PackEntry entry)
    {
        var label = string.IsNullOrEmpty(entry.Phrase) ? "(pick a phrase)" : entry.Phrase;

        ImGui.SetNextItemWidth(280);
        if (!ImGui.BeginCombo("##phrase", label))
            return;

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "search...", ref phraseFilter, 64);

        if (ImGui.BeginChild("##phraselist", new Vector2(0, 240)))
        {
            var shown = 0;

            foreach (var candidate in AutoTranslateCatalog.Search(phraseFilter))
            {
                // Thousands of rows; cap the draw and let the filter narrow it.
                if (shown++ >= 200)
                {
                    ImGui.TextDisabled("...more matches; refine the search");
                    break;
                }

                var selected = entry.Group == candidate.Group && entry.Key == candidate.Key;
                if (ImGui.Selectable(candidate.Display, selected))
                {
                    entry.Group = candidate.Group;
                    entry.Key = candidate.Key;
                    entry.Phrase = candidate.Text;
                    store.Save(pack);
                }
            }

            if (shown == 0)
                ImGui.TextDisabled("No phrases match.");
        }

        ImGui.EndChild();
        ImGui.EndCombo();
    }

    private void DrawImageRow(StickerPack pack, PackEntry entry)
    {
        var path = store.ResolveMedia(pack, entry);

        ImGui.TextDisabled(path is null
            ? "no image"
            : $"{entry.Media[..8]}...{entry.Extension}");

        if (pack.IsLocal)
        {
            ImGui.SameLine();
            if (ImGui.Button("Choose image..."))
            {
            fileDialogs.OpenFileDialog(
                "Choose a sticker image",
                "Stickers{.png}",
                (ok, selected) =>
                {
                    if (!ok || selected.Count == 0)
                        return;

                    // Copying into the pack's own folder means the binding survives the original file
                    // being moved or deleted, and the pack stays self-contained for export.
                    var stored = store.ImportMedia(pack, selected[0], out var error);
                    if (stored is null)
                    {
                        status = error;
                        return;
                    }

                    entry.Media = stored.Value.Hash;
                    entry.Extension = stored.Value.Extension;
                    store.Save(pack);
                    status = string.Empty;
                },
                    1,
                    string.Empty);
            }
        }

        if (path is not null)
        {
            ImGui.SameLine();

            var previewable = entry.Group != 0 || entry.Key != 0;
            if (previewable)
            {
                if (ImGui.Button("Preview"))
                    status = plugin.PreviewSticker(entry.Group, entry.Key);

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        "Shows this phrase as a bubble on your own character so you can see the sticker.\n" +
                        "Local only - nothing is sent and no one else sees it.");
                }
            }
            else
            {
                ImGui.TextDisabled("(pick a phrase to preview)");
            }

            if (pack.IsLocal)
            {
                ImGui.SameLine();
                if (ImGui.Button("Reload"))
                    registry.Invalidate(path);
            }
        }
    }

    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / (1024.0 * 1024.0):0.0} MB"
            : $"{bytes / 1024} KB";

    private static string SanitiseFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        return string.IsNullOrWhiteSpace(name) ? "stickerpack" : name;
    }
}
