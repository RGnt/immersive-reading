using AngleSharp.Dom;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Enrichment.Dom;

public static class TokenDomApplier
{
    public static void ApplyTokensToBlock(
        IDocument document,
        IElement blockEl,
        List<DomTextSlice> slices,
        List<EnrichedTokenEntry> tokens)
    {
        foreach (var slice in slices)
        {
            var overlappingTokens = tokens
                .Where(t => Math.Max(slice.StartOffset, t.Token.StartCharOffset) < Math.Min(slice.EndOffset, t.Token.EndCharOffset))
                .ToList();

            if (overlappingTokens.Count == 0) continue;

            if (slice.IsRubyElement && slice.Node is IElement rubyEl)
            {
                var topTok = overlappingTokens[0];
                rubyEl.ClassList.Add("ir-tok");
                rubyEl.SetAttribute("data-tid", topTok.TokenId.ToString());
                ApplyStatusClasses(rubyEl, topTok);
                continue;
            }

            if (slice.Node is not IText textNode || textNode.Parent is null) continue;

            var frag = document.CreateDocumentFragment();
            int currentSliceOffset = slice.StartOffset;

            foreach (var tok in overlappingTokens)
            {
                if (tok.Token.StartCharOffset > currentSliceOffset)
                {
                    int preLen = tok.Token.StartCharOffset - currentSliceOffset;
                    string preText = slice.Text.Substring(currentSliceOffset - slice.StartOffset, preLen);
                    frag.AppendChild(document.CreateTextNode(preText));
                    currentSliceOffset = tok.Token.StartCharOffset;
                }

                int tokSubStart = Math.Max(currentSliceOffset, tok.Token.StartCharOffset);
                int tokSubEnd = Math.Min(slice.EndOffset, tok.Token.EndCharOffset);
                int tokLen = tokSubEnd - tokSubStart;
                if (tokLen <= 0) continue;

                string tokSubText = slice.Text.Substring(tokSubStart - slice.StartOffset, tokLen);
                var span = document.CreateElement("span");
                span.ClassList.Add("ir-tok");
                span.SetAttribute("data-tid", tok.TokenId.ToString());
                ApplyStatusClasses(span, tok);

                if (tok.Token.StartCharOffset < slice.StartOffset || tok.Token.EndCharOffset > slice.EndOffset)
                {
                    span.SetAttribute("data-part", (tok.Token.StartCharOffset < slice.StartOffset ? "1" : "0"));
                }

                RenderRubyUnitsIntoSpan(document, span, tok, tokSubStart - tok.Token.StartCharOffset, tokLen);
                frag.AppendChild(span);

                currentSliceOffset = tokSubEnd;
            }

            if (currentSliceOffset < slice.EndOffset)
            {
                string postText = slice.Text.Substring(currentSliceOffset - slice.StartOffset);
                frag.AppendChild(document.CreateTextNode(postText));
            }

            textNode.Parent.ReplaceChild(frag, textNode);
        }
    }

    public static void ApplyStatusClasses(IElement el, EnrichedTokenEntry tok)
    {
        if (!string.IsNullOrEmpty(tok.Token.Lemma))
        {
            el.SetAttribute("data-lemma", tok.Token.Lemma);
        }

        string statusClass = tok.Status switch
        {
            WordStatus.Learning => "ir-st-learning",
            WordStatus.Known => "ir-st-known",
            WordStatus.Ignored => "ir-st-ignored",
            _ => "ir-st-new"
        };
        el.ClassList.Add(statusClass);

        if (tok.InBookDeck)
        {
            el.ClassList.Add("ir-in-deck");
        }
    }

    public static void RenderRubyUnitsIntoSpan(
        IDocument document,
        IElement span,
        EnrichedTokenEntry tok,
        int subOffset,
        int subLen)
    {
        if (tok.RubyUnits.Count == 0)
        {
            span.AppendChild(document.CreateTextNode(tok.Token.Surface.Substring(subOffset, subLen)));
            return;
        }

        foreach (var u in tok.RubyUnits)
        {
            int uStart = Math.Max(subOffset, u.RelativeStart);
            int uEnd = Math.Min(subOffset + subLen, u.RelativeEnd);
            int len = uEnd - uStart;
            if (len <= 0) continue;

            string baseText = u.BaseText;
            if (u.Reading != null)
            {
                var ruby = document.CreateElement("ruby");
                if (u.Source == RubySource.PluginGenerated)
                {
                    ruby.ClassList.Add("ir-ruby-gen");
                }
                ruby.AppendChild(document.CreateTextNode(baseText));

                var rt = document.CreateElement("rt");
                rt.TextContent = u.Reading;
                ruby.AppendChild(rt);

                span.AppendChild(ruby);
            }
            else
            {
                span.AppendChild(document.CreateTextNode(baseText));
            }
        }
    }
}

