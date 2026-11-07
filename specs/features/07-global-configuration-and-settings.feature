Feature: Global Configuration & Application Settings
  As a reader and language learner
  I want a centralized configuration screen to manage directories, themes, colors, typography, dictionary settings, and AI LLM providers
  So that all preferences persist across sessions and update the reader and library in real time without application restarts

  Background:
    Given an initialized library database with the global configuration schema active

  Scenario: Loading default configuration on fresh installation
    When the application queries "config.getAll"
    Then the configuration should contain valid default settings for all categories:
      | category     | key                     | expected_value                               |
      | directories  | defaultScanDirectory    | ""                                           |
      | appearance   | themeMode               | "dark"                                       |
      | appearance   | accentColor             | "#6366f1"                                    |
      | reader       | defaultWritingMode      | "vertical-rl"                                |
      | reader       | furiganaDisplayMode     | "always"                                     |
      | llm          | activeProfileName       | "Ollama Local"                               |
      | llm          | endpointUrl             | "http://localhost:11434/v1/chat/completions" |
      | dictionaries | maxScanCharacters       | 16                                           |
    And all settings should persist in the "app_settings" database table

  Scenario: Updating directories and validating library scan default
    When the user updates the "directories" configuration with:
      | key                  | value                         |
      | defaultScanDirectory | "G:\\TMW Ebooks\\Collection"  |
      | pythonExecutablePath | "C:\\Python312\\python.exe"   |
    Then the "config.update" operation should succeed
    And querying "library.getBootstrapState" should return "G:\\TMW Ebooks\\Collection" as the default scan directory

  Scenario: Customizing reader appearance and live CSS custom properties
    When the user updates the "appearance" and "reader" settings with:
      | category   | key              | value             |
      | appearance | themeMode        | "sepia"           |
      | appearance | backgroundColor  | "#f4ecd8"         |
      | appearance | textColor        | "#433422"         |
      | reader     | japaneseFontFamily | "Shippori Mincho" |
      | reader     | baseFontSizePx   | 26.0              |
      | reader     | rubyTextColor    | "#b45309"         |
    Then the configuration manager should persist the new values
    And a "config.changed" event should be broadcast with the updated appearance properties
    And querying "config.getCategory" for "appearance" should reflect the "sepia" theme

  Scenario: Configuring LLM provider profile and testing endpoint connectivity
    When the user updates the "llm" settings with:
      | key               | value                                              |
      | activeProfileName | "LM Studio Local"                                  |
      | endpointUrl       | "http://localhost:1234/v1/chat/completions"        |
      | modelId           | "qwen2.5-7b-instruct"                              |
      | temperature       | 0.5                                                |
    Then the active "OpenAiCompatibleLlmProvider" options should update immediately without restarting the host
    When the user triggers "config.testLlmEndpoint" with the current LLM settings
    Then the response should indicate whether the connection succeeded or fell back to deterministic heuristic mode

  Scenario: Resetting a category or all settings to factory defaults
    Given the user has customized their "reader" and "llm" settings
    When the user requests "config.resetDefaults" for category "reader"
    Then the "reader" settings should revert to:
      | key                 | expected_value  |
      | defaultWritingMode  | "vertical-rl"   |
      | furiganaDisplayMode | "always"        |
      | baseFontSizePx      | 22.0            |
    But the "llm" settings should retain their custom profile "LM Studio Local"
    When the user requests "config.resetDefaults" for category "all"
    Then all categories should be restored to their initial factory defaults

