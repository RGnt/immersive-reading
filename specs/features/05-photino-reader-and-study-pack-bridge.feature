# language: en
@phase2 @phase4 @reader @scheme @studypack
Feature: Photino.NET Custom Scheme EPUB Reader, Vertical Typography, and Study Pack Bridge
  As a reader using the Photino.NET desktop application
  I want to read EPUB chapters seamlessly via ir-book:// in vertical or horizontal layout and connect to enrichement-extractor study packs
  So that relative EPUB images/styles work naturally, my reading position is always saved, and I can generate printable PDFs or flashcards

  Background:
    Given the Photino desktop window has registered custom scheme handlers "ir-app://" and "ir-book://"
    And book "book-4e0541b184c4ace9" is imported in the library

  Rule: The ir-book:// custom scheme serves enriched XHTML chapters and streams relative EPUB assets securely

    Scenario: Intercepting an XHTML spine chapter request and injecting pre-render enrichment
      When the reader WebView requests "ir-book://book-4e0541b184c4ace9/item/xhtml/p-001.xhtml"
      Then the "ir-book://" handler opens the EPUB ZIP entry "item/xhtml/p-001.xhtml"
      And runs "AngleSharpDomEnricher" with the active language plugin
      And injects "ir-reader-injected.css" and "ir-reader-bridge.js" into "<head>"
      And returns the enriched document with MIME type "application/xhtml+xml; charset=utf-8"

    Scenario: Streaming relative EPUB images, stylesheets, and fonts directly from the ZIP archive
      Given the chapter "ir-book://book-4e0541b184c4ace9/item/xhtml/p-001.xhtml" references "../image/cover.jpg"
      When the browser resolves and requests "ir-book://book-4e0541b184c4ace9/item/image/cover.jpg"
      Then the "ir-book://" handler streams "item/image/cover.jpg" from the EPUB archive with MIME type "image/jpeg"

    Scenario: Blocking path-traversal attempts on custom URI schemes
      When a request is made to "ir-book://book-4e0541b184c4ace9/../../Windows/win.ini"
      Then the scheme handler rejects the path escape and returns an empty 400/404 response

  Rule: Reading progress and bookmarks are tracked accurately by token ID and character offset

    Scenario: Saving and restoring exact reading position across app restarts
      Given the user is reading chapter 3 ("item/xhtml/p-004.xhtml") at viewport token 215 (char offset 640)
      When the reader invokes "reader.saveProgress"
      Then "reading_progress" in "library.db" stores chapterIndex 3, tokenId 215, charOffset 640, and updated overall completion %
      When the user reopens "book-4e0541b184c4ace9" in a new session
      Then the reader navigates directly to chapter 3 and scrolls token 215 into view

  Rule: PythonStudyPackBridge links and triggers enrichement-extractor study packs and flashcards

    Scenario: Detecting published study pack PDFs and 20-word vocabulary sets for a book
      Given "enrichement-extractor/output/.publication/active.json" points to a valid release containing "book-4e0541b184c4ace9"
      When the user views the Book Detail drawer for "book-4e0541b184c4ace9"
      Then the UI displays links to "kanji-master.pdf", "vocabulary-master.pdf", "grammar-master.pdf", and chapter 20-word sets
      And acquiring a read lease prevents "immersive.retention" from garbage-collecting the active snapshot while open

