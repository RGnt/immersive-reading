Feature: Prompt templates and the conversation coach
  Spec 19. @shared scenarios run in BOTH the C# and the Python suites from one JSON file of vectors.
  Status: specification only, nothing implemented yet.

  # ---------- Templates ----------

  @shared
  Scenario: Variables are inserted at send time
    Given a template "Explain in {{coach_language}}."
    When it is rendered with coach_language "Español"
    Then the result is "Explain in Español."

  @shared
  Scenario: A missing required variable is an error and nothing is sent
    Given a template that requires "utterance"
    When it is rendered without it
    Then rendering fails naming the template and "utterance"

  @shared
  Scenario: An undeclared variable in the body is an error
    Given a template whose body uses {{typo}} and declares no such variable
    Then loading the template fails naming "typo"

  @shared
  Scenario: Defaults apply
    Given a variable with default "English"
    When it is not supplied
    Then "English" is used

  @shared
  Scenario: Enum values are enforced
    Given a variable strictness with values low, medium, high
    When "extreme" is supplied
    Then rendering fails

  @shared
  Scenario: Optional sections
    Given "{{#if hint}}Hint: {{hint}}{{/if}}"
    Then with an empty hint nothing is emitted
    And with hint "x" the result is "Hint: x"

  @shared
  Scenario: Lists repeat
    Given "{{#each words}}- {{this}}\n{{/each}}"
    When words are ["a","b"]
    Then the result is "- a\n- b\n"

  @shared
  Scenario: Untrusted text goes into a fenced block it cannot close
    Given an untrusted variable utterance
    When its value contains the closing marker "UTTERANCE>>>"
    Then the marker inside the value is neutralised
    And the rendered text has exactly one closing marker

  @shared
  Scenario: Rendering is deterministic
    When the same template and variables are rendered twice
    Then the bytes are identical

  Scenario: The same vectors pass in both languages
    Then the C# suite and the Python suite both load the shared vector file and agree on every case

  Scenario: A valid user override replaces the built-in template
    Given a file "coach.improve.prompt.md" in the plugin prompts folder that passes validation
    Then the next render uses it

  Scenario: An invalid override is ignored and reported
    Given an override that omits a required variable declaration
    Then the built-in template is used
    And the plugin settings show the validation error

  Scenario: Rendered prompts are not logged by default
    When a prompt is rendered and sent
    Then nothing containing book text is written to logs or files unless promptDebug is on

  # ---------- Coach ----------

  Background:
    Given a fake LLM endpoint
    And the persona "クーク" with a stored conversation

  @fake
  Scenario: Feedback is a separate call that does not delay the reply
    When the learner sends a turn
    Then the character reply is returned without waiting for the coach
    And the coach request is sent afterwards with the template "coach.improve"
    And the feedback arrives as a later event for the same message

  @fake
  Scenario: Explanations use the configured language
    Given coachLanguage is "Deutsch"
    When feedback is requested
    Then the prompt contains "Deutsch" as the explanation language
    And the Japanese examples in the prompt stay Japanese

  @fake
  Scenario: Changing the language applies to the next turn only
    Given feedback was shown in English
    When coachLanguage changes to "日本語"
    Then earlier cards stay as stored
    And the next request uses "日本語"

  @fake
  Scenario: The coach sees no book knowledge
    When feedback is requested
    Then the prompt contains the learner utterance, the character's last reply and the speaking register
    And it contains no profile block, no story text and no knowledge-graph content

  @fake
  Scenario: The card is capped by level and maxItems
    Given the fake model returns 7 issues and coachMaxItems is 3
    Then the card shows 3 items with the most important first

  @fake
  Scenario: Naturalness tips carry alternatives
    Given the fake model returns a naturalness tip with two alternatives
    Then the card lists both alternatives

  @fake
  Scenario: Spoken turns show what the model heard and allow a corrected re-run
    Given the transcript is "私は学校に行きます"
    Then the card starts with "heard as: 私は学校に行きます"
    When the user edits the transcript and re-runs the coach
    Then the coach receives the edited text as a typed utterance

  @fake
  Scenario: A failing coach never affects the conversation
    Given the coach endpoint returns HTTP 500
    When the learner sends a turn
    Then the character reply and its audio are unchanged
    And the card shows "feedback unavailable" with the status and message
    And no canned feedback is produced

  @fake
  Scenario: Invalid JSON is retried once
    Given the fake model answers invalid JSON first and valid JSON second
    Then the card shows the valid feedback

  @fake
  Scenario: The coach can be switched off
    Given coachEnabled is false
    When the learner sends a turn
    Then no coach request is made

  @fake
  Scenario: Feedback is stored with the message
    When feedback arrives
    Then it is saved linked to the learner message
    And it is removed when the chat is deleted

  @spike
  Scenario: Real model follows the language rule
    Given a real model and coachLanguage "English"
    Then explanations are in English and examples in Japanese on a sample of 20 utterances
