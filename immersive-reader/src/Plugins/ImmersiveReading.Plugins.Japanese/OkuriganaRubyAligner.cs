using System.Text;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Plugins.Japanese;

/// <summary>
/// Splits a token's surface and reading into kanji-stem ruby units and plain okurigana units.
/// Ports the proven okurigana-alignment algorithm from enrichement-extractor/immersive/furigana.py.
/// </summary>
public sealed class OkuriganaRubyAligner : IRubyAligner
{
    public IReadOnlyList<RubyUnit> AlignRuby(
        ReadOnlySpan<char> surface,
        ReadOnlySpan<char> reading,
        IReadOnlyList<SourceRubySpan> overlappingAuthorRubies)
    {
        if (surface.IsEmpty)
        {
            return [];
        }

        string surfaceStr = surface.ToString();
        string readingStr = reading.ToString();
        string hiraReading = KanaNormalizer.ToHiragana(reading);

        // 1. If author-supplied EPUB ruby spans cover this token, respect them as ground truth
        if (overlappingAuthorRubies is { Count: > 0 })
        {
            var authorUnits = TryAlignAuthorRubies(surfaceStr, overlappingAuthorRubies);
            if (authorUnits is not null)
            {
                return authorUnits;
            }
        }

        // 2. If the token contains no kanji, emit a single plain-text unit (no ruby)
        if (!KanaNormalizer.ContainsKanji(surface))
        {
            return [new RubyUnit(0, surface.Length, surfaceStr, null, RubySource.None)];
        }

        // 3. Decompose surface into alternating Kanji runs and fixed Kana anchors
        var segments = SegmentSurface(surfaceStr);

        // If the entire surface is a single kanji block (e.g. "東京" or jukujikun "大人")
        if (segments.Count == 1 && segments[0].IsKanji)
        {
            return [new RubyUnit(0, surface.Length, surfaceStr, hiraReading, RubySource.PluginGenerated)];
        }

        // 4. Align kanji runs against the reading using fixed kana anchors
        var aligned = TryAlignSegments(segments, hiraReading);
        if (aligned is not null)
        {
            return aligned;
        }

        // Fallback: Irregular jukujikun or non-standard okurigana -> whole-word ruby
        return [new RubyUnit(0, surface.Length, surfaceStr, hiraReading, RubySource.PluginGenerated)];
    }

    private static IReadOnlyList<RubyUnit>? TryAlignAuthorRubies(
        string surface,
        IReadOnlyList<SourceRubySpan> authorRubies)
    {
        var result = new List<RubyUnit>();
        int curIdx = 0;

        foreach (var ar in authorRubies)
        {
            int foundIdx = surface.IndexOf(ar.Surface, curIdx, StringComparison.Ordinal);
            if (foundIdx < 0) return null;

            if (foundIdx > curIdx)
            {
                string plain = surface[curIdx..foundIdx];
                result.Add(new RubyUnit(curIdx, foundIdx, plain, null, RubySource.None));
            }

            string arReading = KanaNormalizer.ToHiragana(ar.Reading);
            result.Add(new RubyUnit(foundIdx, foundIdx + ar.Surface.Length, ar.Surface, arReading, RubySource.EpubAuthor));
            curIdx = foundIdx + ar.Surface.Length;
        }

        if (curIdx < surface.Length)
        {
            result.Add(new RubyUnit(curIdx, surface.Length, surface[curIdx..], null, RubySource.None));
        }

        return result;
    }

    private sealed record SurfaceSegment(string Text, bool IsKanji, int StartOffset, int EndOffset);

    private static List<SurfaceSegment> SegmentSurface(string surface)
    {
        var list = new List<SurfaceSegment>();
        int i = 0;
        while (i < surface.Length)
        {
            var firstRune = Rune.GetRuneAt(surface, i);
            bool isKanji = KanaNormalizer.IsKanji(firstRune);
            int start = i;
            i += firstRune.Utf16SequenceLength;

            while (i < surface.Length)
            {
                var r = Rune.GetRuneAt(surface, i);
                if (KanaNormalizer.IsKanji(r) != isKanji) break;
                i += r.Utf16SequenceLength;
            }

            list.Add(new SurfaceSegment(surface[start..i], isKanji, start, i));
        }
        return list;
    }

    private static IReadOnlyList<RubyUnit>? TryAlignSegments(
        List<SurfaceSegment> segments,
        string hiraReading)
    {
        var units = new List<RubyUnit>();
        if (!RecursiveAlign(segments, 0, hiraReading, 0, units))
        {
            return null;
        }
        return units;
    }

    private static bool RecursiveAlign(
        List<SurfaceSegment> segments,
        int segIndex,
        string reading,
        int readingOffset,
        List<RubyUnit> accumulated)
    {
        if (segIndex >= segments.Count)
        {
            return readingOffset == reading.Length;
        }

        var seg = segments[segIndex];
        bool isLast = (segIndex == segments.Count - 1);

        if (!seg.IsKanji)
        {
            // Fixed kana anchor: reading must match exactly at current readingOffset
            string segHira = KanaNormalizer.ToHiragana(seg.Text);
            if (!reading.AsSpan(readingOffset).StartsWith(segHira, StringComparison.Ordinal))
            {
                return false;
            }

            accumulated.Add(new RubyUnit(seg.StartOffset, seg.EndOffset, seg.Text, null, RubySource.None));
            if (RecursiveAlign(segments, segIndex + 1, reading, readingOffset + segHira.Length, accumulated))
            {
                return true;
            }
            accumulated.RemoveAt(accumulated.Count - 1);
            return false;
        }

        // Segment is Kanji: find how many reading characters belong to this kanji stem
        if (isLast)
        {
            if (readingOffset >= reading.Length) return false;
            string restReading = reading[readingOffset..];
            accumulated.Add(new RubyUnit(seg.StartOffset, seg.EndOffset, seg.Text, restReading, RubySource.PluginGenerated));
            return true;
        }

        // Look at the next segment (which is a kana anchor) to anchor the reading slice
        var nextSeg = segments[segIndex + 1];
        string nextHira = KanaNormalizer.ToHiragana(nextSeg.Text);

        // Try candidate stem reading lengths
        for (int matchPos = readingOffset + 1; matchPos <= reading.Length - nextHira.Length; matchPos++)
        {
            if (reading.AsSpan(matchPos).StartsWith(nextHira, StringComparison.Ordinal))
            {
                string stemReading = reading[readingOffset..matchPos];
                accumulated.Add(new RubyUnit(seg.StartOffset, seg.EndOffset, seg.Text, stemReading, RubySource.PluginGenerated));

                if (RecursiveAlign(segments, segIndex + 1, reading, matchPos, accumulated))
                {
                    return true;
                }

                accumulated.RemoveAt(accumulated.Count - 1);
            }
        }

        return false;
    }
}

