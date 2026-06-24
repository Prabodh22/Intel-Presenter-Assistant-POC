using System.Text.RegularExpressions;

namespace PptPoc.Orchestration;

/// <summary>
/// Centralises the logic for deriving a YAML knowledge-base file path from a
/// PowerPoint presentation identifier, and for detecting whether a cached YAML
/// is stale relative to the source .pptx file.
///
/// Problem this solves
/// ───────────────────
/// When PowerPoint auto-recovers a file it exposes TWO different identifiers
/// for the same deck depending on where you ask:
///
///   presentation.FullName  →  "llm_accuracy_deep_dive.pptx - AutoRecovered"
///                             (a title string, not a real file path)
///
///   GetActivePresentationPath() →  "C:\Users\1\Documents\llm_accuracy_deep_dive [Autosaved].pptx"
///                                  (the real file-system path)
///
/// If each call-site sanitises its own string independently, the YAML key used
/// at SAVE time (KnowledgeBasePreprocessor) and the key used at LOOKUP time
/// (Orchestrator hot-reload) diverge — the cache is always missed and the app
/// re-preprocesses the entire deck every session.
///
/// This helper normalises BOTH sources to the same canonical filename so the
/// key is identical regardless of which identifier is available.
///
/// Staleness detection
/// ───────────────────
/// <see cref="IsYamlStale"/> compares the YAML file's last-write time against
/// the .pptx file's last-write time. If the PPT was saved after the KB was
/// built (e.g. the author edited slides and AutoSave flushed the changes), the
/// cached KB is out-of-date and must be rebuilt before starting the engine.
/// This prevents stale highlights after slide edits between sessions.
/// </summary>
public static class KbPathHelper
{
    // Matches all known PowerPoint auto-save/auto-recover suffix variants:
    //   " - AutoRecovered"
    //   " [Autosaved]"
    //   " (AutoRecovered)"
    //   Any combination of whitespace around the separator
    private static readonly Regex AutoSaveSuffixRegex = new(
        @"\s*[-\[\(]\s*Auto(?:Saved|Recovered)\s*[\]\)]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Default directory where YAML knowledge-base files are stored.
    /// Uses <see cref="AppContext.BaseDirectory"/> (the exe's own folder) so
    /// the path is always absolute and consistent regardless of the CWD.
    /// Override in tests to point at a temp folder.
    /// </summary>
    public static string DefaultKbDirectory { get; set; } = AppContext.BaseDirectory;

    /// <summary>
    /// Derives the canonical YAML path for a presentation.
    /// </summary>
    /// <param name="pptPathOrTitle">
    ///   Either the full file-system path
    ///   (<c>C:\...\llm_accuracy_deep_dive [Autosaved].pptx</c>) or
    ///   the COM presentation title
    ///   (<c>llm_accuracy_deep_dive.pptx - AutoRecovered</c>).
    ///   Both are normalised to the same key.
    /// </param>
    /// <param name="kbDirectory">
    ///   Directory to place the file in.
    ///   Defaults to <see cref="DefaultKbDirectory"/> when null.
    /// </param>
    /// <returns>Absolute path to the YAML knowledge-base file.</returns>
    public static string GetYamlPath(string pptPathOrTitle, string? kbDirectory = null)
    {
        // 1. Take just the filename component (strips the directory part of a
        //    real path; is a no-op when the input is already a bare title).
        string filename = Path.GetFileName(pptPathOrTitle);

        // 2. Strip known auto-save suffixes so both identifier forms collapse
        //    to the same base name:
        //      "llm_accuracy_deep_dive.pptx - AutoRecovered"  →  "llm_accuracy_deep_dive.pptx"
        //      "llm_accuracy_deep_dive [Autosaved].pptx"       →  "llm_accuracy_deep_dive.pptx"
        filename = AutoSaveSuffixRegex.Replace(filename, string.Empty).Trim();

        // 3. Sanitise: replace any character that isn't safe in a filename with '_'
        string safeName = Regex.Replace(filename, "[^a-zA-Z0-9_.-]", "_");

        // 4. Build the final absolute path
        string dir = kbDirectory ?? DefaultKbDirectory;
        return Path.Combine(dir, $"knowledge_base_{safeName}.yaml");
    }

    /// <summary>
    /// Returns <c>true</c> if the cached YAML knowledge base is out of date and
    /// must be rebuilt before starting the engine.
    ///
    /// Staleness rules
    /// ───────────────
    /// • YAML does not exist          → stale  (must build from scratch)
    /// • pptPathOrTitle is a real local file AND the .pptx was written after
    ///   the YAML was written         → stale  (slide edits not reflected in KB)
    /// • pptPathOrTitle is a COM title / SharePoint URL (not a local file),
    ///   or file-time comparison is unavailable → NOT stale (safe default;
    ///   manual "Refresh KB" tray item is the override)
    /// </summary>
    /// <param name="pptPathOrTitle">
    ///   The same value passed to <see cref="GetYamlPath"/> — either a full
    ///   local file path or a COM presentation title.
    /// </param>
    /// <param name="yamlPath">
    ///   The absolute YAML path returned by <see cref="GetYamlPath"/>.
    /// </param>
    public static bool IsYamlStale(string pptPathOrTitle, string yamlPath)
    {
        // No YAML at all → must build
        if (!File.Exists(yamlPath))
            return true;

        // Only compare file times when the PPT identifier is a real local path.
        // For COM titles ("deck.pptx - AutoRecovered") or SharePoint URLs,
        // File.Exists returns false and we fall through to the safe default.
        if (File.Exists(pptPathOrTitle))
        {
            DateTime pptModified  = File.GetLastWriteTimeUtc(pptPathOrTitle);
            DateTime yamlBuilt    = File.GetLastWriteTimeUtc(yamlPath);

            // Give a 30-second grace window — AutoSave flushes frequently and
            // we don't want to rebuild on every minor background save during a
            // live session. Only rebuild when the PPT is meaningfully newer.
            return pptModified > yamlBuilt.AddSeconds(30);
        }

        // Title-only identifier or SharePoint path — cannot compare file times.
        // Treat as not stale; user can force refresh via the tray menu.
        return false;
    }
}
