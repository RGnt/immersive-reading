Feature: Plugin UI bundles load in the desktop app
  As a plugin author or user
  I want plugin user interfaces to load reliably
  So that plugins can add buttons, drawers, pages and settings panels

  Scenario: Enabling a plugin loads its UI
    Given the "posHighlighter" plugin is disabled
    When I enable it
    Then its UI bundle is fetched over RPC and activated
    And the "Highlights" button appears in the reader top bar

  Scenario: Built-in plugin UIs load at startup
    Given "aiCompanion" and "posHighlighter" are enabled
    When the application starts
    Then both plugin UIs are activated and register their slots

  Scenario: Disabling removes everything the plugin added
    Given the "posHighlighter" UI is active
    When I disable the plugin
    Then its toolbar button, injected styles and live reader styles are removed

  Scenario: Plugins use normal imports
    Given a plugin UI imports 'react', 'lucide-react' and '@ir/sdk'
    When the plugin loads
    Then every imported name resolves without a hand-maintained export list

  Scenario: Unsupported import gives a readable error
    Given a plugin UI imports 'left-pad'
    When the plugin loads
    Then loading fails with a message naming the plugin and 'left-pad'
    And the message is shown on the plugin card in Settings

  Scenario: Plugin stylesheet is applied and removed
    Given a plugin manifest declares a styleEntry
    When the plugin is enabled then disabled
    Then its stylesheet is injected and then removed

  Scenario: Asset access is confined
    When a client requests plugin asset "../secret.json" or "native.dll"
    Then the request is rejected

  Scenario: Reloading picks up changed code
    Given an external plugin whose ui.js changed on disk
    When the plugin is disabled and enabled again
    Then the new code runs

  Scenario: Verification fails loudly
    Given a first-party plugin UI fails to load
    When "--verify-ui" runs
    Then it exits with a failure and names the plugin
