# language: en
@phase1 @library @ingestion @scale-24k
Feature: EPUB Library Ingestion, Metadata Extraction, and 24,000-Book Scale Scanning
  As a collector with ~24,000 Japanese and foreign-language EPUBs
  I want fast, non-destructive EPUB indexing with accurate bibliographic extraction and instant search
  So that I can catalog, search, and browse my entire library without lag or modifying source EPUB files

  Background:
    Given the library database "library.db" is initialized in WAL mode
    And the default custom field definitions "jiten_moe_url", "vocab_deck_csv", and "jiten_difficulty" exist

  Rule: Book identity must be 100% deterministic and compatible with enrichement-extractor

    Scenario Outline: Computing deterministic book-<16 hex> ID from EPUB bytes
      Given an EPUB file "<file_name>" on disk
      When the library computes the book identity from the EPUB byte stream
      Then the resulting BookId must match the pattern "^book-[0-9a-f]{16}$"
      And the BookId must equal "<expected_book_id>"
      And the source EPUB file on disk must remain byte-for-byte unmodified

      Examples:
        | file_name                                             | expected_book_id      |
        | [かさい まり] こぐまのクーク物語　はじめての海とキャンプ.epub | book-4e0541b184c4ace9 |
        | くま クマ 熊 ベアー (PASH! ブックス) - くまなの.epub        | book-93a87170fe31025d |

    Scenario: Re-importing an identical EPUB deduplicates by SHA-256
      Given an EPUB with BookId "book-4e0541b184c4ace9" is already registered in the library
      When the user imports another copy of the same EPUB file
      Then no duplicate row is created in the "books" table
      And the existing "book-4e0541b184c4ace9" record is returned with its user edits intact

  Rule: Bibliographic metadata and ISBNs are extracted from OPF with filename heuristic fallback

    Scenario: Extracting complete OPF 2.0/3.0 metadata including ISBN-13 and series metadata
      Given an EPUB whose OPF contains:
        | Field                   | Value                          |
        | dc:title                | 本好きの下剋上 第一部「兵士の娘Ⅰ」 |
        | opf:file-as (title)     | ほんずきのげこくじょう 01          |
        | dc:creator              | 香月美夜                        |
        | opf:file-as (creator)   | かづきみや                      |
        | dc:publisher            | TOブックス                      |
        | dc:date                 | 2015-01-25                     |
        | dc:language             | ja                             |
        | dc:identifier (ISBN)    | urn:isbn:978-4-86472-342-8     |
        | belongs-to-collection   | 本好きの下剋上                   |
        | group-position          | 1                              |
      When the EPUB is imported into the library
      Then the stored book record has:
        | Property       | Expected Value                 |
        | Title          | 本好きの下剋上 第一部「兵士の娘Ⅰ」 |
        | SortTitle      | ほんずきのげこくじょう 01          |
        | Authors        | 香月美夜                        |
        | AuthorSort     | かづきみや                      |
        | Publisher      | TOブックス                      |
        | PublishedDate  | 2015-01-25                     |
        | Language       | ja                             |
        | Isbn           | 9784864723428                  |
        | SeriesName     | 本好きの下剋上                   |
        | SeriesIndex    | 1.0                            |
      And an immutable snapshot of the raw OPF values is saved in "opf_original_json"

    Scenario Outline: Falling back to Japanese filename heuristics when OPF metadata is incomplete
      Given an EPUB file named "<file_name>" whose OPF lacks author, imprint, and series fields
      When the library parses metadata for "<file_name>"
      Then the fallback parser populates:
        | CleanTitle   | Authors   | Imprint   | SeriesName   | SeriesIndex    |
        | <clean_title>| <authors> | <imprint> | <series_name>| <series_index> |

      Examples:
        | file_name                                                   | clean_title                              | authors     | imprint        | series_name        | series_index |
        | [かさい まり] こぐまのクーク物語　はじめての海とキャンプ.epub       | こぐまのクーク物語　はじめての海とキャンプ     | かさい まり   |                |                    |              |
        | くま クマ 熊 ベアー (PASH! ブックス) - くまなの.epub              | くま クマ 熊 ベアー                         | くまなの      | PASH! ブックス |                    |              |
        | [川原 礫] ソードアート・オンライン 第01巻 (電撃文庫).epub          | ソードアート・オンライン 第01巻              | 川原 礫       | 電撃文庫        | ソードアート・オンライン | 1            |

  Rule: Batch scanning and querying must scale to 24,000+ books with sub-5ms UI queries

    Scenario: Two-stage parallel directory scan indexes EPUBs without parsing full chapter XHTML bodies upfront
      Given a root directory containing 1,000 EPUB archives across nested subdirectories
      When the user triggers "library.scanDirectory" with recursive set to true
      Then the scanner reads only "META-INF/container.xml", the OPF package, and cover thumbnails from each ZIP central directory
      And commits records in batched SQLite WAL transactions
      And marks each newly scanned book with "stats_computed = 0" for lazy chapter character counting
      And emits incremental scan progress events to the UI

    Scenario: Server-side paginated listing and FTS5 search across 24,000 books
      Given the library catalog contains 24,000 indexed Japanese books
      When the UI requests "library.listBooks" with search query "くま", offset 0, and limit 50
      Then the query executes against "books_fts" and covering indexes in under 50 ms
      And returns at most 50 "BookSummary" items along with the exact "totalCount" of matching books

