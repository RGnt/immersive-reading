# language: en
@phase1 @library @organization @metadata @custom-fields
Feature: Hierarchical Virtual Folders, Series Ordering, Editable Metadata, Tags, and Multi-Level Custom Fields
  As a collector managing 24,000 Japanese ebooks
  I want to organize books into nested collections/categories/series, fix metadata errors, attach manual tags, and define custom fields like jiten.moe links at any level
  So that my collection is structured cleanly without moving or altering files on disk

  Background:
    Given the library database "library.db" is initialized

  Rule: Virtual folders support arbitrary hierarchy (Collection -> Category -> Series -> Volumes) without moving disk files

    Scenario: Creating a nested Collection -> Category -> Series hierarchy and sorting volumes numerically
      Given the user creates a root collection folder "Japanese"
      And under "Japanese" creates child category folders "Shounen" and "Seinen"
      And under "Seinen" creates a series folder "Monster" with default sort "series_or_title"
      And the user assigns the following books to the "Monster" folder:
        | BookId                  | Title          | SeriesName | SeriesIndex | OriginalDiskPath              |
        | book-0000000000000010   | Monster Vol 10 | Monster    | 10.0        | D:\Ebooks\Monster\vol10.epub  |
        | book-0000000000000001   | Monster Vol 1  | Monster    | 1.0         | D:\Ebooks\Monster\vol01.epub  |
        | book-0000000000000002   | Monster Vol 2  | Monster    | 2.0         | D:\Ebooks\Monster\vol02.epub  |
        | book-0000000000000025   | Monster Vol 2.5| Monster    | 2.5         | D:\Ebooks\Monster\vol02_5.epub|
      When the user lists books in folder "Monster"
      Then the books are returned in numeric volume order:
        | Position | BookId                | SeriesIndex |
        | 1        | book-0000000000000001 | 1.0         |
        | 2        | book-0000000000000002 | 2.0         |
        | 3        | book-0000000000000025 | 2.5         |
        | 4        | book-0000000000000010 | 10.0        |
      And each book's "folder_path" on disk remains unchanged

    Scenario: Listing all books recursively under a parent collection or category folder
      Given "Japanese" contains "Shounen" (with 2 books) and "Seinen -> Monster" (with 4 books)
      When the user requests "library.listBooks" for folder "Japanese" with "includeDescendants = true"
      Then all 6 books under the "Japanese" subtree are returned

  Rule: Bibliographic metadata is editable per-book and in bulk, while preserving original OPF data for reset

    Scenario: Fixing a corrupted book title, author, and ISBN without modifying the EPUB file
      Given a book "book-4e0541b184c4ace9" was imported with OPF title "[かさい まり] こぐまのクーク物語" and empty ISBN
      When the user updates the book metadata to:
        | Field       | New Value                           |
        | Title       | こぐまのクーク物語 はじめての海とキャンプ |
        | Authors     | かさい まり                          |
        | Isbn        | 9784041026229                       |
        | Publisher   | 角川書店                             |
      Then the book's updated fields are persisted in "books" with "is_metadata_edited = 1"
      And the full-text search index "books_fts" is immediately updated with ISBN "9784041026229"
      And when the user later triggers "library.resetBookMetadataToOpf" for "book-4e0541b184c4ace9"
      Then the book's Title reverts to "[かさい まり] こぐまのクーク物語" and "is_metadata_edited" becomes 0

    Scenario: Bulk-editing series name, folder placement, and auto-numbering volumes across multiple books
      Given 3 standalone books are selected in the library table view
      When the user invokes "library.bulkUpdateBooks" with:
        | Patch Property     | Value            |
        | FolderId           | folder-seinen    |
        | SeriesName         | 三日間の幸福       |
        | AutoNumberVolumes  | true (start at 1)|
      Then all 3 books are moved to "folder-seinen" with SeriesName "三日間の幸福"
      And their "SeriesIndex" values are set to 1.0, 2.0, and 3.0 respectively

  Rule: Manual user tags can be created and used for multi-tag filtering

    Scenario: Filtering books by multiple manual tags using AND and OR modes
      Given manual tags "#n2-target", "#mystery", and "#slice-of-life" exist
      And Book A has tags "#n2-target" and "#mystery"
      And Book B has tags "#n2-target" and "#slice-of-life"
      When the user filters books by tags ["#n2-target", "#mystery"] with match mode "AND"
      Then only Book A is returned
      When the user filters books by tags ["#mystery", "#slice-of-life"] with match mode "OR"
      Then both Book A and Book B are returned

  Rule: Custom fields can be defined and populated on any folder/series or book level with inheritance

    Scenario: Inheriting a jiten.moe media link from a Series folder and overriding it on a specific volume
      Given the custom field "jiten_moe_url" has "field_type = 'url'" and "inherit_to_children = 1"
      And a folder hierarchy "Japanese -> Light Novels -> くま クマ 熊 ベアー" exists
      And Book Vol 1 and Book Vol 2 are inside the series folder "くま クマ 熊 ベアー"
      When the user sets custom field "jiten_moe_url" on folder "くま クマ 熊 ベアー" to "https://jiten.moe/decks/media/1024"
      And sets custom field "jiten_moe_url" directly on Book Vol 2 to "https://jiten.moe/decks/media/1024/subdeck/2"
      Then querying effective custom fields for Book Vol 1 returns:
        | FieldKey      | EffectiveValue                      | IsInherited | SourceEntityName    |
        | jiten_moe_url | https://jiten.moe/decks/media/1024  | true        | くま クマ 熊 ベアー    |
      And querying effective custom fields for Book Vol 2 returns:
        | FieldKey      | EffectiveValue                                 | IsInherited | SourceEntityName |
        | jiten_moe_url | https://jiten.moe/decks/media/1024/subdeck/2   | false       | Book Vol 2       |

    Scenario: Creating a new custom numeric field and sorting/filtering books by it
      When the user defines a custom field "jiten_difficulty" with display name "Jiten Difficulty (1-10)" and type "number"
      And sets "jiten_difficulty" to 3.5 on Book A and 7.2 on Book B
      Then the user can filter books where "jiten_difficulty <= 4.0" and receive Book A

