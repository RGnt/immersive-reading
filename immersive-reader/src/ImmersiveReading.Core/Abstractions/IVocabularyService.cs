using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Core.Vocabulary;

namespace ImmersiveReading.Core.Abstractions;

public interface IVocabularyService
{
    WordStatus GetStatus(string language, string lemma, string reading);
    VocabularyRecord? GetRecord(string language, string lemma, string reading);
    void SetStatus(string language, string lemma, string reading, WordStatus status, string? customGloss = null);
    void IncrementLookupCount(string language, string lemma, string reading, string? bookId = null);
    IReadOnlyList<VocabularyRecord> GetWordsByStatus(string language, WordStatus status);
}
