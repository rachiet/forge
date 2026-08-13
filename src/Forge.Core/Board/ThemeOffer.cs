using Forge.Core.Db;

namespace Forge.Core.Board;

/// <summary>
/// Whether the client has been asked to pick a theme. Raised by the PM's `offer_theme_choice`,
/// which opens the picker on their page, and cleared the moment they choose — the same
/// staged-in-project_meta shape as a pending proposal, so the page learns about it on its next
/// poll rather than through a second channel.
/// </summary>
public static class ThemeOffer
{
    private const string Key = "theme_offer";

    /// <summary>Whether the picker should be open.</summary>
    public static bool Pending(ProjectMetaRepository meta) => meta.Get(Key) == "1";

    /// <summary>Opens the picker on the client's page.</summary>
    public static void Raise(ProjectMetaRepository meta) => meta.Set(Key, "1");

    /// <summary>Closes it, once they have chosen or dismissed it.</summary>
    public static void Clear(ProjectMetaRepository meta) => meta.Set(Key, "0");
}
