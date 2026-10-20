namespace BamCam.IO.Cb;

/// <summary>
/// Reads CamBam translation tables ("translate_de" etc.: lines of "English &lt;tab&gt;&lt;tab&gt; Translation") so that
/// drawings saved by a localised CamBam, which contain translated enum values, can be read.
/// </summary>
public static class CbLocalization
{
    public static int LoadTranslationFile(string path)
    {
        var count = 0;
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var english = parts[0].Trim();
            var translated = parts[^1].Trim();
            // ".info" lines are help texts, not values.
            if (english.EndsWith(".info", StringComparison.Ordinal) || english.Length == 0 || translated.Length == 0) continue;
            if (english.Equals(translated, StringComparison.OrdinalIgnoreCase)) continue;
            lock (CbFormat.LocalizedToEnglish)
            {
                if (CbFormat.LocalizedToEnglish.TryAdd(translated, english)) count++;
            }
        }
        return count;
    }
}
