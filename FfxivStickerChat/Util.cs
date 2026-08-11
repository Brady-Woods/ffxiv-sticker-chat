using System;
using System.Diagnostics;

namespace FfxivStickerChat;

/// <summary>Small helpers with no better home.</summary>
public static class Util
{
    /// <summary>Opens a folder in the system file browser.</summary>
    /// <remarks>
    /// The game runs under Wine on Linux, where <c>explorer</c> still resolves, so this works on both
    /// native Windows and a Proton prefix.
    /// </remarks>
    public static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, $"Could not open {path}");
        }
    }
}
