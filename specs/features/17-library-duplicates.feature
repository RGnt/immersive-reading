Feature: Library duplicate detection
  Spec 21. Covered by DuplicateDetectorTests, DuplicateServiceTests and the library RPC tests.

  Scenario Outline: Different files of the same book are grouped
    Given book A "<titleA>" by "<authorA>" and book B "<titleB>" by "<authorB>"
    When duplicates are searched
    Then A and B are in one group with confidence "<level>"

    Examples:
      | titleA                | authorA        | titleB                          | authorB      | level   |
      | 魔法科高校の劣等生 1  | 佐島 勤        | 魔法科高校の劣等生（1）【電子版】 | 佐島勤       | certain |
      | ソードアート・オンライン 3 | 川原 礫   | ソードアート・オンライン　第3巻 | 川原礫（著） | certain |
      | 吾輩は猫である        | 夏目漱石       | 吾輩は猫である [eBook]          | 夏目 漱石    | certain |

  Scenario: Different volumes of one series are never grouped
    Given "ソードアート・オンライン 1" and "ソードアート・オンライン 2" by the same author with similar length
    When duplicates are searched
    Then they are not in one group

  Scenario: Volume markers in different notations are the same volume
    Given "Re:ゼロから始める異世界生活 第3巻" and "Re:ゼロから始める異世界生活 3" and "Re:ゼロから始める異世界生活 vol.4"
    Then the first two are one group and the third is separate

  Scenario: Different languages are not duplicates
    Given two books with the same title, one Japanese and one English
    Then they are not grouped

  Scenario: Same ISBN is certain
    Given two books with different titles and the same ISBN-13
    Then they are one group with confidence "certain"

  Scenario: Chains cannot join different volumes
    Given A "Title" (no volume), B "Title 1" and C "Title 2"
    When duplicates are searched
    Then no group contains both B and C

  Scenario: Length changes the score
    Given two matching books whose length differs by 40 percent
    Then the reasons mention a very different length and the score is lower than for equal lengths

  Scenario: Recommendation protects the user's work
    Given the duplicate with 42 percent reading progress and one with none
    Then the one with progress is recommended and the reason says so

  Scenario: Resolving keeps the user's data
    Given a group where the removed book has the tag "favourite" and a vocabulary deck CSV and the kept book has neither
    When the user keeps the other book and removes it
    Then the kept book has the tag, the deck CSV and any missing series information
    And the removed book is gone from the library and its file is untouched

  Scenario: A removed duplicate is not imported again by a scan
    Given a removed duplicate whose file is still in the scanned folder
    When the folder is scanned
    Then the file is skipped with a warning and counted as a removed duplicate
    When the user allows it again
    Then the next scan imports it

  Scenario: Not duplicates is remembered
    Given a group the user marks as not duplicates
    When duplicates are searched again
    Then that group is not offered

  Scenario: Scale
    Given 20000 synthetic books of which 200 are duplicate pairs
    When duplicates are searched
    Then exactly those pairs are found and the search finishes within the time bound

  # ---------- Real-library shapes (found by tuning against a 2,188-book library) ----------

  Scenario Outline: Volume numbers in the shapes real files use
    Given the title "<title>"
    Then its volume is "<volume>"

    Examples:
      | title                                                       | volume |
      | ロクでなし魔術講師と禁忌教典2                               | 2      |
      | お隣の天使様にいつの間にか駄目人間にされていた件５         | 5      |
      | TrymenT ―今を変えたいと願うあなたへ― VI                    | 6      |
      | 淡海乃海 水面が揺れる時～...の逆襲～十一【電子書籍限定書き下ろしSS付き】 | 11     |
      | 淡海乃海 水面が揺れる時～...の逆襲～弐                      | 2      |
      | やはり俺の青春ラブコメはまちがっている。14.5               | 14.5   |
      | 境界線上のホライゾンII＜上＞                                | 2.1    |

  Scenario: Titles that differ only by a number are different books
    Given "失格紋の最強賢者", "失格紋の最強賢者２" and "失格紋の最強賢者３" by the same author
    Then none of them is grouped with another

  Scenario: A title without a number is volume 1, not any volume
    Given "魔女と猟犬", "魔女と猟犬 1" and "魔女と猟犬 2" by the same author
    Then the first two are grouped as "likely" and the third is separate

  Scenario: An identifier stamped on every volume proves nothing
    Given three volumes and a spin-off that all carry the same publication identifier
    Then no group is formed

  Scenario: Publisher imprint and edition remarks do not hide a duplicate
    Given "落第騎士の英雄譚＜キャバルリィ＞２【電子特装版】 (GA文庫)" and "落第騎士の英雄譚＜キャバルリィ＞２"
    Then they are grouped as "certain"
