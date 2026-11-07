# language: en
@phase5 @extensions @ai-companion @comprehension @persona-chat @n-plus-one
Feature: Extensible Feature Plugins, Progressive AI Reading Comprehension, and n+1 Persona Chat
  As a language learner progressing from intermediate to advanced reading
  I want spoiler-free comprehension questions from what I just read (progressing from multiple-choice to free-form) and character persona chat constrained to my n+1 vocabulary
  So that I can verify my reading comprehension and practice active conversation without spoilers or overwhelming vocabulary

  Background:
    Given the "AiCompanionPlugin" is registered with the "IFeaturePlugin" extension host
    And the user is reading "book-4e0541b184c4ace9" at Chapter 2, viewport tokenId 320

  Rule: Reader context extraction must never leak plot spoilers beyond the user's current reading position

    Scenario: Extracting the just-read passage strictly up to the current viewport token
      Given Chapter 2 has 900 tokens total, and the user's previous checkpoint was at tokenId 100 and current viewport is at tokenId 320
      When "IReaderContextService.GetJustReadPassageAsync" is called for "book-4e0541b184c4ace9"
      Then the returned "ReadPassageSlice" contains only tokens 100 through 320 of Chapter 2
      And zero tokens after tokenId 320 or from Chapter 3+ are included in the prompt context

  Rule: Reading comprehension questions adapt from Multiple-Choice (MCQ) to Guided to Free-Form as mastery improves

    Scenario: Generating and grading a Multiple-Choice question at Stage 1 with passage citation
      Given the user's comprehension stage for the book is "MultipleChoice"
      When the user clicks "Check Comprehension" on the just-read passage (tokens 100..320)
      Then "ComprehensionQuizService" generates a 4-option multiple-choice question grounded in tokens 100..320
      And the question prompt and options are enriched via "ISnippetEnricher" so every word has furigana and < 5ms hover dictionary lookup
      When the user selects the correct option
      Then the answer is graded immediately as 100% correct
      And displays the supporting evidence quote with a clickable "[Jump to Paragraph]" link

    Scenario: Graduating from Multiple-Choice to Free-Form comprehension questions as accuracy reaches >= 80%
      Given the user has answered the last 5 Multiple-Choice and Guided questions with an average score of 90%
      When the user starts a new comprehension check
      Then the session mixer selects "FreeForm" question mode
      When the user submits a free-form answer in English or Japanese
      Then the evaluator grades the response against the passage rubric and returns:
        | Field                      | Description                                                      |
        | ComprehensionScore         | Numeric score (0 to 100) based on passage facts                  |
        | EvidenceQuote              | Exact Japanese sentence from the passage supporting the answer   |
        | MissingOrMisunderstood     | Any key plot/character nuance missed                             |
        | ModelAnswerHtml            | Enriched model answer with <span class="ir-tok"> and furigana    |

  Rule: Persona chat enforces both a strict spoiler boundary and a MeCab-verified n+1 vocabulary budget

    Scenario: Creating a book persona from highlighted character dialogue with spoiler protection
      Given the user highlights dialogue lines spoken by "クーク" in Chapter 1 and creates a "BookPersona"
      When the user opens a Persona Chat session while their reading progress is at Chapter 2, tokenId 320
      Then the persona adopts the configured pronouns, speech register, and voice samples of "クーク"
      And the persona's story knowledge is strictly capped at Chapter 2, tokenId 320

    Scenario: Enforcing the n+1 vocabulary constraint on persona replies using MeCab.DotNet verification
      Given the user's "vocabulary_state" has 1,500 "Known" lemmas (the n set)
      And the current chapter's "Learning" pool has target words ["焚き火", "テント", "海岸"] (the +1 set)
      When the user sends a chat message to the persona
      And the LLM drafts an initial candidate reply containing 3 out-of-budget advanced words ["漸次", "趨勢", "躊躇"]
      Then "PersonaChatService" tokenizes the candidate reply with "MeCab.DotNet"
      And detects that ["漸次", "趨勢", "躊躇"] are outside the user's "Known" (n) + "Target" (+1) budget
      And automatically instructs the LLM to rewrite the reply replacing those lemmas with Known words while keeping a "+1" target word like "焚き火"
      And runs "ISnippetEnricher" ("AngleSharpDomEnricher") on the final reply so every word in the chat bubble has furigana, a "+1 Target" badge on "焚き火", and < 5ms hover dictionary lookup

