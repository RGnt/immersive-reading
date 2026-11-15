Feature: Spoken conversation with a book character (Phase A, turn-based)
  The reader speaks to a character of the book; the character answers in text and in a designed voice.
  Spec 17. Scenarios tagged @fake run in CI with in-process fake LLM and TTS servers (no GPU, microphone or models).
  Scenarios tagged @hardware are opt-in checks on the reference machine. Status: specification only, nothing implemented yet.

  Background:
    # Decisions 2026-11-14: push to talk only; Japanese books only; TTS is a separate system; personas extend book_personas.
    Given a synthetic book with 3 chapters of known text
    And a fake LLM endpoint configured in Settings → AI
    And a fake TTS endpoint configured as ttsEndpointUrl
    And the book character persona "クーク" exists with a voice design prompt

  # ---------- Story prefix: zero spoiler and append-only ----------

  @fake
  Scenario: The story prefix never contains text the reader has not reached
    Given the reader is in chapter 2 at character offset 1234
    When the story prefix is built with chunk size 500
    Then it contains all of chapter 1
    And it contains chapter 2 text only up to offset 1000
    And it contains no text of chapter 3

  @fake
  Scenario Outline: Rounding is always down, for any position
    Given the reader is in chapter 2 at character offset <offset>
    When the story prefix is built with chunk size 500
    Then the last included character offset of chapter 2 is <included>

    Examples:
      | offset | included |
      | 0      | 0        |
      | 499    | 0        |
      | 500    | 500      |
      | 999    | 500      |

  @fake
  Scenario: Advancing within a chunk does not change the prefix
    Given the prefix built at offset 1010 in chapter 2
    When the reader advances to offset 1400
    Then the prefix bytes are identical

  @fake
  Scenario: Advancing past a chunk only appends
    Given the prefix built at offset 1010 in chapter 2
    When the reader advances to offset 1600
    Then the new prefix starts with all bytes of the old prefix
    And it is longer than the old prefix

  @fake
  Scenario: Per-turn data comes after the story prefix
    When a chat turn request is built
    Then the story prefix is before the n+1 target list, the history and the user message
    And the prefix contains no timestamp or per-turn identifier

  @fake
  Scenario: A very long book drops the oldest whole chapters first
    Given maxContextChars is smaller than the whole book read so far
    When the story prefix is built
    Then the oldest chapters are replaced by a one-line marker
    And the current chapter is never cut in the middle of the included text

  # ---------- LLM request shape ----------

  @fake
  Scenario: Live chat does not request thinking
    When a typed chat turn is sent
    Then the system prompt does not contain the thinking marker

  @fake
  Scenario: Persona extraction requests thinking and a long timeout
    When a persona is extracted for "クーク"
    Then the system prompt starts with the thinking marker
    And the request timeout is the extraction timeout, not LlmSettings.TimeoutSeconds

  @fake
  Scenario: A spoken turn sends the audio as an input_audio part
    Given a 2 second 16 kHz mono WAV recording
    When a voice turn is sent
    Then the user message has an input_audio part with format "wav" before the text part

  @fake
  Scenario: Voice turns never fall back to canned answers
    Given the fake LLM endpoint returns HTTP 500 with a message
    When a voice turn is sent
    Then the turn fails with the status and the message
    And no assistant message is stored and no audio is synthesised

  # ---------- The basic end-to-end case ----------

  @fake @smoke
  Scenario: One spoken turn, end to end
    Given the fake LLM answers {"userTranscript":"こんにちは","reply":"やあ、いっしょに読もうね。","emotionPrompt":"warm and cheerful"}
    And the fake TTS returns a valid WAV
    When I send a voice turn with a 2 second recording for "クーク"
    Then the result has userTranscript "こんにちは"
    And the result has an assistant reply with clickable words
    And the reply was checked by the n+1 vocabulary audit before it was spoken
    And the TTS request text equals the displayed reply
    And the TTS request instruction contains the persona voice design prompt and "warm and cheerful"
    And the TTS request carries the persona seed
    And both messages are stored with the emotion prompt and the audio path
    And the microphone recording is not stored

  @fake
  Scenario: The spoken text is the rewritten reply, not the first candidate
    Given the first reply uses vocabulary outside the n+1 budget
    And the rewrite returns a simpler reply
    When I send a voice turn
    Then the TTS request text equals the rewritten reply

  @fake
  Scenario: Text chat still works when TTS is down
    Given the fake TTS endpoint is unreachable
    When I send a typed turn
    Then I get the reply text
    And the result has no audio and an audio error message

  # ---------- Persona and voice ----------

  @fake
  Scenario: A persona is created from the knowledge graph at the reader's position
    Given a knowledge base exists for the series and the reader is at volume 1 chapter 2
    When I pick the character "クーク" from the list
    Then the persona system prompt uses the profile snapshot for 1:2
    And no fact from a later chapter is present
    And the persona stores knowledge_character_id and the position it was created at

  @fake
  Scenario: The voice design is created by the system and stays editable
    Given the profile snapshot of the character
    When the persona is created
    Then a voice design prompt in English is proposed from the template "voice.design"
    And it is saved only after I confirm or edit it

  @fake
  Scenario: Different roles can use different endpoints
    Given the knowledge builder override points to another endpoint than Settings → AI
    Then voice turns use the Settings → AI endpoint
    And the knowledge build uses the override
    And a blank override falls back to Settings → AI

  @fake
  Scenario: Secrets are never logged
    When any LLM or TTS request is made
    Then no log line or progress message contains an API key

  @fake
  Scenario: The context limit is configurable
    Given maxContextChars is 2000
    When a text chat turn is built with the summary strategy
    Then the story context is at most 2000 characters

  @fake
  Scenario: Extracting a persona uses only text the reader has read
    Given the reader is in chapter 2 at offset 800
    When a persona is extracted for "クーク"
    Then the LLM request contains no text beyond chapter 2 offset 500
    And the proposal is not saved until savePersona is called
    And the saved persona remembers chapter 2 and the extraction offset

  @fake
  Scenario: Saving a persona validates the reference WAV path
    When I save a persona whose referenceWavPath is outside the plugin voices directory
    Then the save is rejected
    And no file outside the plugin data directory is read or written

  @fake
  Scenario: Upgrading an existing database keeps personas and chats
    Given a database at migration v1 with 2 personas and 5 chat messages
    When the plugin migration v2 runs twice
    Then all rows are still there
    And the new voice columns exist with their defaults

  # ---------- TTS endpoint handling ----------

  @fake
  Scenario: Testing the TTS endpoint
    When I run testTtsEndpoint
    Then I get ok, a latency and the model name from /health

  @fake
  Scenario Outline: Bad TTS answers are rejected
    Given the fake TTS answers <answer>
    When the persona voice is previewed
    Then the preview fails with a readable message
    And no file is stored

    Examples:
      | answer                         |
      | HTTP 500 with a text body      |
      | 200 with a non-WAV body        |
      | 200 with a WAV longer than 120 seconds |

  @fake
  Scenario: A remote TTS or LLM endpoint needs explicit permission
    Given ttsEndpointUrl is "http://203.0.113.10:8081"
    And allowRemoteEndpoints is off
    When I run testTtsEndpoint
    Then it is refused with a message that audio would leave this computer

  @fake
  Scenario: Generated audio is bounded
    Given voiceCacheMaxMb is 1 and 5 stored character clips of 400 KB each
    When another clip is stored
    Then the oldest clips are removed until the total is at most 1 MB

  # ---------- Recording ----------

  @fake
  Scenario: The microphone records only while the talk key is held
    Given the talk key is not pressed
    Then no audio is captured and no microphone stream is open
    When I press and hold the talk key
    Then recording starts and the level indicator moves
    When I release the key
    Then the recording is sent as one voice turn

  @fake
  Scenario: Escape cancels a recording without sending
    Given I am holding the talk key
    When I press Escape
    Then nothing is sent and the recording is discarded

  @fake
  Scenario: A very short press is ignored
    When I press and release the talk key within 200 ms
    Then nothing is sent

  @fake
  Scenario: The recording mode is a seam
    Given the recording mode is "pushToTalk"
    Then a "silenceTimeout" mode can be added without changing the voice turn pipeline

  @fake
  Scenario: Voice features are offered for Japanese books only
    Given an English book is open
    Then the push-to-talk control is not shown and a short explanation is displayed

  @fake
  Scenario: The WAV encoder writes a valid 16 kHz mono PCM file
    Given 1 second of samples including values above 1.0 and below -1.0
    When it is encoded
    Then the header says RIFF/WAVE, PCM, 1 channel, 16000 Hz, 16 bit
    And the data length is 32000 bytes
    And the samples are clipped, not wrapped

  @fake
  Scenario: Recordings that are too long or malformed are refused before any model call
    Given a recording longer than maxRecordingSeconds
    When a voice turn is sent
    Then it is refused and the LLM endpoint receives no request

  # ---------- Hardware (opt-in) ----------

  @hardware
  Scenario: Warm turns reuse the cached prefix
    Given a real llama-server with the audio model and a 100k character prefix
    When two consecutive turns are sent
    Then the second turn's prompt-processing time is a small fraction of the first
    And the measured numbers are recorded in the spike report

  @hardware
  Scenario: A spoken Japanese phrase is understood
    Given a real recording of "今日はいい天気ですね"
    When a voice turn is sent
    Then the transcript matches the phrase

  @hardware
  Scenario: The same character keeps the same voice across sentences
    Given a real TTS server and the persona's voice design prompt and seed
    When 10 different sentences are synthesised
    Then a listener recognises one speaker
    And the emotion variants change delivery but not the speaker

  @hardware
  Scenario: Microphone capture works in the app window
    Given the app window on the reference machine
    When I hold the talk button and speak for 3 seconds
    Then a WAV of about 3 seconds at 16 kHz is produced
    And a denied permission shows a clear message in the drawer
