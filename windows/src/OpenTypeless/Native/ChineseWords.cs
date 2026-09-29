using Windows.Data.Text;

namespace OpenTypeless.Native;

/// <summary>
/// Chinese words through Windows' own word segmenter, so a fix of one character learns the word it belongs to (see
/// CorrectionLearner); the counterpart of NLTokenizer on macOS. Null when the segmenter isn't available.
/// </summary>
internal static class ChineseWords
{
    private static readonly Lazy<WordsSegmenter?> Segmenter = new(() =>
    {
        try
        {
            var segmenter = new WordsSegmenter("zh-CN");
            return segmenter.ResolvedLanguage == "und" ? null : segmenter;
        }
        catch (Exception)
        {
            return null;
        }
    });

    public static IReadOnlyList<(int Start, int End)>? Split(string text)
    {
        if (Segmenter.Value is not { } segmenter) return null;
        try
        {
            return segmenter.GetTokens(text)
                .Select(token => ((int)token.SourceTextSegment.StartPosition,
                                  (int)(token.SourceTextSegment.StartPosition + token.SourceTextSegment.Length)))
                .ToList();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
