# Immersive Reading — Comprehensive User Guide

Welcome to **Immersive Reading**, an offline-first Japanese reading environment and high-scale EPUB library manager. Designed specifically for Japanese language learners and readers of light novels, web novels, and literature, Immersive Reading combines instant morphological analysis, sub-5-millisecond dictionary popovers, Vim-style keyboard navigation, 1-click Anki sentence mining, and optional local AI reading companions.

---

## Table of Contents

1. [Quick Start & Launching](#1-quick-start--launching)
2. [Library Management & Organization](#2-library-management--organization)
   - [Full-Width Responsive Interface](#full-width-responsive-interface)
   - [Table View vs. Grid View](#table-view-vs-grid-view)
   - [Scanning & Importing Books](#scanning--importing-books)
   - [Collections, Folders & Drag-and-Drop](#collections-folders--drag-and-drop)
   - [Multi-Selection & Batch Actions](#multi-selection--batch-actions)
   - [Pruning Empty Folders & Removing Books](#pruning-empty-folders--removing-books)
3. [Book Information & 1-Click jiten.moe Integration](#3-book-information--1-click-jitenmoe-integration)
   - [Book Info Pop-up Modal](#book-info-pop-up-modal)
   - [1-Click jiten.moe Lookup & Difficulty Ratings](#1-click-jitenmoe-lookup--difficulty-ratings)
   - [Custom Fields & Table of Contents](#custom-fields--table-of-contents)
4. [The Reading Experience](#4-the-reading-experience)
   - [Vertical (`vertical-rl`) vs. Horizontal (`horizontal-tb`) Layouts](#vertical-vertical-rl-vs-horizontal-horizontal-tb-layouts)
   - [Furigana Display Modes (All, Unlearned Only, Hidden)](#furigana-display-modes-all-unlearned-only-hidden)
   - [Font Scaling & Typography](#font-scaling--typography)
   - [Reading Progress & Chapter Navigation](#reading-progress--chapter-navigation)
5. [Vim Motions & Keyboard Navigation](#5-vim-motions--keyboard-navigation)
   - [Keyboard Shortcuts Cheat Sheet](#keyboard-shortcuts-cheat-sheet)
   - [Detailed Keybinding Reference](#detailed-keybinding-reference)
6. [Instant Dictionary Lookup & Vocabulary Tracking](#6-instant-dictionary-lookup--vocabulary-tracking)
   - [Sub-5ms Popover & Deinflection Engine](#sub-5ms-popover--deinflection-engine)
   - [Kanji Detail View & Stroke Orders](#kanji-detail-view--stroke-orders)
   - [Vocabulary Learning States (`1`–`4`)](#vocabulary-learning-states-14)
   - [1-Click Anki Sentence Mining (`m`)](#1-click-anki-sentence-mining-m)
7. [AI Reading Companion & $n+1$ Persona Chat](#7-ai-reading-companion--n1-persona-chat)
   - [Spoiler-Free Comprehension Quizzes](#spoiler-free-comprehension-quizzes)
   - [$n+1$ Vocabulary-Constrained Persona Chat](#n1-vocabulary-constrained-persona-chat)
   - [Local & Privacy-Focused LLM Providers](#local--privacy-focused-llm-providers)
8. [Offline Study Packs & Printable PDFs](#8-offline-study-packs--printable-pdfs)
   - [Ordered 20-Word Study Sets](#ordered-20-word-study-sets)
   - [Printable A4 Master & Chapter PDFs](#printable-a4-master--chapter-pdfs)
9. [Global Configuration & Settings](#9-global-configuration--settings)
   - [Accessing Settings (`Ctrl+,`)](#accessing-settings-ctrl)
   - [Customizing Themes, Colors & Fonts](#customizing-themes-colors--fonts)
   - [Configuring Paths & LLM Endpoints](#configuring-paths--llm-endpoints)
10. [Troubleshooting & Frequently Asked Questions](#10-troubleshooting--frequently-asked-questions)

---

## 1. Quick Start & Launching

Immersive Reading is built on **.NET 10** and **Photino.NET**, providing a lightweight, native desktop window powered by your operating system's native webview.

### Running the Portable Release (Recommended for Friends / Testers)
If you received the portable ZIP distribution (`ImmersiveReading-win-x64.zip`):
1. Extract the ZIP anywhere on your system (e.g. your Desktop or Documents folder).
2. Double-click **`START-READER.bat`** or **`ImmersiveReading.App.exe`**.
3. *No installation, administrative privileges, or .NET runtime installation required!*

### Launching from Source Code (Developer Mode)
Open a terminal in the project root and run:
```powershell
cd k:\immersive-reading\immersive-reader
dotnet run --project src\ImmersiveReading.App
```

To build a standalone ZIP release yourself for sharing:
```powershell
.\scripts\package_windows.ps1
```

### First-Time Launch Experience
When launched for the first time:
- The app initializes a local SQLite database in your local app data directory (never committed to git).
- The default library view opens with an empty collection ready for books.
- Default settings (dark theme, vertical Japanese typography, standard font sizes) are applied automatically.

---

## 2. Library Management & Organization

The library engine is engineered to handle massive collections—tested and optimized for libraries containing over **24,000 EPUB volumes** with instant search and zero UI stutter.

### Full-Width Responsive Interface
The library interface utilizes a clean 2-column layout:
- **Left Sidebar (270px)**: Displays your virtual collection tree, library folders, tag filters, quick search, and system actions.
- **Main View (Full Width)**: Expands dynamically to fill the rest of your screen, maximizing your view of book titles, series indices, reading progress, and cover artwork.

### Table View vs. Grid View
Toggle between views anytime using the view buttons in the library toolbar:
- **Table View (Default)**: A high-density data grid displaying Cover Thumbnail, Title, Series, Author, Progress Bar, Difficulty, and Actions (`📖 Read` and `ℹ️ Info`). Click any column header to sort ascending or descending.
- **Grid View**: A visual bookshelf gallery showcasing high-resolution cover art with subtle hover cards and quick action overlays.
- **📚 Series Grouping Toggle**: Enabled by default in the toolbar. When active, all books belonging to a multi-volume series are folded together into a single series entry. Toggle off anytime to view all books as a flat list.

### Multi-Volume Series Grouping & Drill-Down
To keep large libraries clean and uncluttered, Immersive Reading automatically groups multi-volume book series:
- **Folded Series Cards & Rows**: Multi-volume series (2 or more volumes) appear as stacked cards (in Grid View) or highlighted series rows (in Table View) displaying:
  - The cover artwork and bibliographic details of the **1st volume**.
  - A prominent volume count badge (e.g. `📚 15 Vols`).
  - Quick action buttons: `📚 View All (N)` and `⚡ Jiten`.
- **Standalone Display**: Single-volume books or books without a series name are displayed individually as regular books.
- **Series Drill-Down View**: Clicking any series card or row opens the series view, displaying all volumes ordered by volume number (`#1`, `#2`, `#3`...). A top banner provides one-click navigation back to the library, as well as series-wide batch actions (`⚡ 1-Press Match on jiten.moe` and `📥 Download All Decks (.csv)`).

### Scanning & Importing Books
You can add books to your library in two ways:
1. **Directory Scanning**: Click `📁 Scan Directory` in the sidebar and select a root folder containing `.epub` files. The scanner recursively indexes all books in the background without locking the UI.
2. **Drag-and-Drop Import**: Drag one or more `.epub` files directly from Windows File Explorer onto the Immersive Reading window. The files will be cataloged and added to your active collection immediately.

### Collections, Folders & Drag-and-Drop
Organize your library with unlimited nested folders:
- **Create a Folder**: Click `+ New Folder` in the sidebar to create top-level or nested collections (e.g., `Japanese > Light Novels > Monogatari Series`).
- **Drag-and-Drop Organization**: Drag any book card or table row and drop it directly onto a folder in the sidebar tree to move it into that collection.

### Multi-Selection & Batch Actions
Manage dozens or hundreds of books simultaneously:
- **Select Multiple**: Hold `Ctrl` and click individual books, or hold `Shift` and click to select a continuous range.
- **Batch Drag-and-Drop**: Drag any book in the selection to move all selected books together into a target folder.
- **Batch Context Menu**: Right-click any selected book to apply tags, change reading status, or move the entire batch.

### Pruning Empty Folders & Removing Books
- **Prune Empty Folders**: Click the `🧹 Prune Empty Folders` button in the sidebar. This scans your collection tree and safely removes all empty folders in one click, keeping your library tidy.
- **Trash Can Drop Zone**: The sidebar features a `🗑️ Drop here to remove from collection` drop target. Dragging selected books onto this target disassociates them from the current collection without deleting the underlying EPUB file from disk.

---

## 3. Book Information & 1-Click jiten.moe Integration

### Book Info Pop-up Modal
Next to every `📖 Read` button in Table and Grid views, you will find an `ℹ️ Info` button. Clicking it opens a comprehensive, tabbed pop-up dialog:
- **Bibliographic Metadata**: Edit Book Title, Original Title, Series Name, Series Index, Author, Publisher, Language, and Total Character Count.
- **jiten.moe & Custom Fields**: View and manage external dictionary IDs, difficulty levels, and user-defined key-value attributes.
- **Chapters**: Browse the complete Table of Contents with per-chapter word counts and reading completion markers.
- **Study Pack**: Check whether an offline Python study bundle exists for this book, view vocabulary counts, and access printable PDFs.
- **Raw OPF**: Inspect the raw EPUB package metadata, spine items, and manifest.

### 1-Click jiten.moe Lookup & Deep Vocabulary Intelligence
[jiten.moe](https://jiten.moe) is a popular community index for Japanese media difficulty ratings and vocabulary frequency decks. Immersive Reading integrates directly with the [jiten.moe API](https://api.jiten.moe/index.html) via an **explicit, user-initiated action** (preserving your privacy and offline guarantees):

1. Click `ℹ️ Info` on any book in your library.
2. Switch to the **jiten.moe & Custom Fields** tab.
3. The search box automatically populates with the book's series or title. You can also filter by media format (**Novels / Light Novels**, **Web Novels**, **Manga**, **Visual Novels**, or **Anime**).
4. Click `🔍 Search`. The .NET backend queries the jiten API and presents rich media cards:
   - **Cover Artwork & Titles**: High-resolution cover thumbnails, original Japanese title, Romaji title, and English title.
   - **Difficulty Rating**: Community difficulty rating out of 10 with color coding (Green: Beginner, Amber: Intermediate, Orange: Advanced, Red: Master).
   - **Metrics & Corpus Stats**: Total characters, word count, unique words, unique kanji, and volume count.
   - **Genres & Community Tags**: Badges for genres (Fantasy, Sci-Fi, Slice of Life) and community tags with consensus percentages (e.g. `Magic 88%`, `Military 95%`).
   - **External Links**: Direct links to AniList, Bookmeter, Kakuyomu, Syosetsu, VNDB, MyAnimeList, and Amazon.
5. **Interactive Deep Intelligence Drawers**:
   - `▼ View Volumes (N)`: Expand all volume sub-decks to inspect individual book volumes and bind metadata specifically to Volume 1, Volume 2, etc.
   - `📊 Difficulty Curve`: View percentile difficulty deciles (P10 to P90) and the book's progression curve segment-by-segment.
   - `📈 Milestones`: Inspect vocabulary coverage milestones (e.g. how many unique words needed to achieve 80%, 85%, 90%, 95%, 98%, or 99% text comprehension).
   - `📖 Vocab Preview`: Preview the top 15 most notable vocabulary words before reading, complete with interactive HTML furigana ruby tags, English definitions, frequency ranks, and occurrence counts.
6. Click `⚡ Apply to Book` (or `⚡ Apply Vol` on a specific volume): The official jiten URL and difficulty score are stored in the book's custom fields, matching tags and genres are associated with the book, and the synopsis is populated if currently blank.

### ⚡ 1-Press Match Series on jiten.moe
When you have a multi-volume franchise, you don't need to look up and match each volume one by one:
1. In the Library or Series View, click the **`⚡ Jiten`** or **`⚡ 1-Press Match on jiten.moe`** button.
2. The system searches jiten.moe for the series title, identifies the parent franchise, and intelligently correlates each local volume against Jiten's sub-decks (e.g. mapping Volume 1 to sub-deck #1, Volume 15 to sub-deck #15).
3. A confirmation modal displays:
   - Franchise details, difficulty rating, and volume coverage.
   - An exact mapping table of local books to Jiten sub-decks with volume difficulty ratings.
   - **Community Tags**: Top tags and genres from the community (e.g. `Fantasy`, `School Life`, `Romance`) are pre-selected for import into your library tags.
   - **Auto-Download CSVs**: A checked option to download the official vocabulary `.csv` for all matched volumes at once.
4. Click **`⚡ Confirm & Apply All`**. In one press, all volumes receive their Jiten URLs, difficulties, synopses, library tags, and offline CSV decks!

### 📥 Direct Vocabulary Deck (.csv) Downloads
[jiten.moe](https://jiten.moe) generates comprehensive vocabulary lists for books. Immersive Reading can fetch these directly:
- **Per-Book Download**: Open any book's `ℹ️ Info` modal, switch to the **Study Pack & Flashcards** tab, and click **`Download deck.csv`**.
- **Batch Series Download**: Click **`📥 Download All Decks (.csv)`** from any Series View or during 1-Press Series Matching.
- **Where are they stored?** The downloaded CSV is saved directly as `deck.csv` inside that book's EPUB directory (`book.FolderPath`).
- **Offline Integration**: The Python enrichment engine automatically detects this file and uses it to construct offline Study Packs, 20-word learning sets, and printable Master PDFs without requiring re-parsing!

### 🔑 jiten.moe API Key Configuration (`.env`)
Most public queries work without an API key, but if you have a registered jiten.moe account or reach rate limits:
1. Open the project root folder (`k:\immersive-reading`).
2. Open or create the **`.env`** file (guarded by `.gitignore`, so your key is never committed).
3. Set your API key:
   ```env
   JITEN_API_KEY=your_jiten_api_key_here
   ```
4. Immersive Reading automatically detects and applies this key for all subsequent searches and deck downloads.

---

## 4. The Reading Experience

Click `📖 Read` on any book to enter the distraction-free immersive reading mode.

### Vertical (`vertical-rl`) vs. Horizontal (`horizontal-tb`) Layouts
Japanese literature is traditionally formatted in vertical right-to-left layout:
- **Vertical Mode (`vertical-rl`)**: Text flows top-to-bottom and columns advance right-to-left. Ideal for authentic light novel immersion.
- **Horizontal Mode (`horizontal-tb`)**: Western left-to-right text flow.
- **Toggle Layout**: Press the `v` key on your keyboard or click the `垂直 / 水平` toggle button in the reader topbar to switch between layouts instantly.

### Furigana Display Modes (All, Unlearned Only, Hidden)
Furigana (ruby text) indicates the reading of kanji characters. Immersive Reading lets you adapt furigana to your reading level:
- Press `f` to cycle through the three furigana modes:
  1. **All Furigana (`Furigana: All`)**: Displays ruby readings for every kanji compound in the text.
  2. **Unlearned Only (`Furigana: Unlearned`)**: Reads your personal vocabulary status. If you have marked a word as **Known** (`4`), furigana is automatically stripped from that word on the page. This prevents you from unconsciously leaning on furigana for words you already know!
  3. **Hidden (`Furigana: Off`)**: Completely removes all ruby text for pure immersion and unassisted reading.

### Font Scaling & Typography
- **Zoom In**: Press `+` (or `=`) to increase the font size.
- **Zoom Out**: Press `-` to decrease the font size.
- **Reset Zoom**: Press `0` to restore default font size.
- Additional typographic controls (line spacing, paragraph margins, and custom Japanese font families like *Noto Sans JP*, *BIZ UDPMincho*, or *Yu Mincho*) are accessible in [Settings](#9-global-configuration--settings).

### Reading Progress & Chapter Navigation
- The topbar shows your current chapter title and a reading progress bar.
- Jump to the next chapter with `]` or return to the previous chapter with `[`.
- Press `Esc` or click `✕ Close` in the topbar to return to your library. Your exact reading position is automatically saved to SQLite.

---

## 5. Vim Motions & Keyboard Navigation

The reader includes a comprehensive set of keyboard shortcuts and Vim-style motions for a mouse-free reading experience.

### Keyboard Shortcuts Cheat Sheet
Press `?` at any time while reading (or click `⌨️ Vim Keys (?)` in the topbar) to display the built-in shortcuts cheat sheet:

| Category | Shortcut | Action |
| :--- | :--- | :--- |
| **Scrolling & Page Advance** | `j` / `Down` | Scroll forward / advance line |
| | `k` / `Up` | Scroll backward / retreat line |
| | `Space` / `h` (in vertical) | Advance one full page screen |
| | `l` (in vertical) / `PageUp` | Retreat one full page screen |
| | `Ctrl + d` | Jump down / forward half a screen |
| | `Ctrl + u` | Jump up / backward half a screen |
| | `g g` | Jump to top of chapter |
| | `G` | Jump to end of chapter |
| **Word Token Navigation** | `w` | Move cursor to **next** Japanese word |
| | `b` | Move cursor to **previous** Japanese word |
| **Dictionary & Flashcards** | `e` / `Enter` / `d` | Open instant dictionary popover on current word |
| | `1` | Mark current word as **Unknown** (Red) |
| | `2` | Mark current word as **Learning** (Orange) |
| | `3` | Mark current word as **Familiar** (Yellow) |
| | `4` | Mark current word as **Known** (Green) |
| | `m` | **1-Click Mine Sentence** to Anki flashcards |
| **Display & Layout** | `f` | Cycle furigana mode (*All* $\rightarrow$ *Unlearned* $\rightarrow$ *Hidden*) |
| | `v` | Toggle writing mode (*vertical-rl* $\leftrightarrow$ *horizontal-tb*) |
| | `+` / `=` | Increase font size |
| | `-` | Decrease font size |
| | `0` | Reset font size to default |
| **Drawers & Controls** | `]` / `[` | Next chapter / Previous chapter |
| | `a` | Toggle AI Companion drawer (quizzes & chat) |
| | `s` | Toggle Study Pack drawer |
| | `?` | Toggle keyboard shortcuts cheat sheet |
| | `q` / `Esc` | Close popover / Close modal / Exit to library |

---

## 6. Instant Dictionary Lookup & Vocabulary Tracking

### Sub-5ms Popover & Deinflection Engine
Immersive Reading integrates a local, indexed SQLite dictionary engine that delivers lookup results in **under 5 milliseconds**:
- **Hover or Cursor**: Simply hover over any word token with your mouse or navigate with `w`/`b` and press `e` (or `Enter`).
- **Deinflection**: The engine automatically detects and deinflects conjugated verbs and adjectives (past tense, polite forms, potential, passive, causative, volitional, and te-form) back to their dictionary headword.
- **Multiple Dictionaries**: Supports Yomitan/Yomichan format-3 dictionaries (e.g. JMdict, Daijisen, Meikyo, Kenkyusha, Pixiv light novel slang).

### Kanji Detail View & Stroke Orders
Clicking any individual kanji character in the dictionary popover opens the Kanji Detail Inspector:
- **KANJIDIC2 Data**: On'yomi, Kun'yomi, English meanings, stroke counts, JLPT level (N5–N1), and Joyo classification.
- **Stroke Order Diagrams**: Animated vector stroke orders rendered using embedded KanjiVG data so you can practice correct writing.

### Vocabulary Learning States (`1`–`4`)
Every word token tracks your personal mastery state across your entire library:
- Press `1` = **Unknown**: Word is highlighted with a subtle red indicator.
- Press `2` = **Learning**: Word is highlighted in orange.
- Press `3` = **Familiar**: Word is highlighted in yellow.
- Press `4` = **Known**: Word is marked known (green). In *Unlearned Only* furigana mode, furigana for this word is instantly hidden throughout the book.

### 1-Click Anki Sentence Mining (`m`)
Sentence mining is effortless:
1. When looking up a word or with the word cursor active, press `m` (or click `⭐ Mine Sentence` in the popover).
2. The reader captures:
   - The **exact Japanese sentence** containing the word.
   - The target **headword** and its **kana reading**.
   - The full **dictionary definition**.
   - Book title and chapter provenance.
3. The card is saved to your local mining queue (`flashcards.json`) and can be exported directly into Anki via AnkiConnect or TSV import.
4. *Invariant*: Story sentences are never machine-translated into English, keeping your immersion pure and contextual.

---

## 7. AI Reading Companion & $n+1$ Persona Chat

For readers who want guided comprehension without spoilers, Immersive Reading includes an optional AI Companion plugin.

### Spoiler-Free Comprehension Quizzes
Press `a` to open the AI Companion drawer while reading:
- Click **"Generate Reading Quiz"**.
- The AI analyzes strictly the **just-read passage** (up to your current scroll position) and creates a 3-question comprehension check:
  - *Context check*: Who was speaking in the scene?
  - *Nuance check*: Why did the character hesitate before responding?
  - *Vocabulary in context*: What did the idiom in line 4 mean in this scenario?
- Because the context is strictly capped at what you have already read, the quiz will **never spoil future scenes or chapters**.

### $n+1$ Vocabulary-Constrained Persona Chat
Chat with in-story characters or a Japanese reading tutor:
- Characters reply strictly in Japanese.
- **$n+1$ Constraint**: The AI's responses are filtered and verified through `MeCab.DotNet`. The model is instructed to only use vocabulary from your "Known" list plus at most one new target learning word per sentence ($n+1$), ensuring responses are comprehensible and educational.

### Local & Privacy-Focused LLM Providers
You do not need to send your data to external cloud services:
- Configure **Ollama** or **LM Studio** in Settings to run models like *Llama 3*, *Qwen 2.5*, or *Mistral* entirely on your local GPU.
- Cloud providers (OpenAI, Anthropic) are also supported via standard API keys.

---

## 8. Offline Study Packs & Printable PDFs

For offline study away from the computer, the companion Python engine (`enrichement-extractor`) can pre-generate comprehensive study bundles.

### Ordered 20-Word Study Sets
- The extractor partitions all unique vocabulary in a book into pedagogical sets of 20 words, ordered by appearance and frequency.
- Each set includes kanji breakdowns, example sentences extracted from the book, and cross-references.

### Printable A4 Master & Chapter PDFs
If you prefer pen-and-paper review or bedside reading:
- **`kanji-master.pdf`**: Complete kanji index with stroke order grids, radical components, and on/kun readings.
- **`vocabulary-master.pdf`**: Alphabetical and frequency-ordered vocabulary dictionary for the book.
- **`chapter-XX-*.pdf`**: Compact 2-page study sheets for each individual chapter to review before or after reading.

---

## 9. Global Configuration & Settings

### Accessing Settings (`Ctrl+,`)
Click the `⚙️ Settings` gear icon in the library sidebar or press `Ctrl + ,` anywhere in the application to open the Global Settings panel.

### Customizing Themes, Colors & Fonts
- **Theme Palettes**: Switch between *Dark Mode*, *OLED True Black*, *Warm Sepia*, or *Clean Light*.
- **Custom Accent Colors**: Customize your surface colors, text contrast, and active cyan cursor glow.
- **Furigana Styling**: Adjust furigana opacity, font size ratio (default 50% of parent kanji), and color.
- **Japanese Fonts**: Set primary and fallback fonts for reading (supports all system fonts installed in Windows).

### Configuring Paths & LLM Endpoints
- **Directories**: Set your default book import folder, dictionary storage directory, and study pack output directory.
- **MeCab Integration**: Verify or update your MeCab dictionary path (IPAdic or UniDic).
- **LLM Settings**: Choose your provider (Ollama, LM Studio, OpenAI, Anthropic), set your endpoint URL (e.g., `http://localhost:11434/v1`), model name, and temperature.

---

## 10. Troubleshooting & Frequently Asked Questions

### Q: Why do my Japanese characters render as boxes or question marks?
Ensure you have a Japanese font installed in Windows (such as *Meiryo*, *Yu Gothic*, *Yu Mincho*, or *BIZ UDPGothic*). You can set your preferred font in `Settings` $\rightarrow$ `Typography`.

### Q: Can I read raw, un-analyzed EPUBs immediately?
**Yes.** You do not need to run any offline pre-processing. When you open any EPUB, the .NET reader parses chapters on the fly in ~15 to 40 milliseconds using MeCab.DotNet and AngleSharp.

### Q: How do I import Yomitan format-3 dictionaries?
Place your dictionary `.zip` files into the `data/yomitan/` folder (or specify a custom directory in Settings). The dictionary engine will index the terms into SQLite on next launch.

### Q: Is my reading data or vocabulary list shared anywhere?
**No.** All database files (`library.db`, `dictionaries.db`), reading progress records, and mined flashcards are stored locally on your machine in SQLite and are strictly git-ignored.

---

## Related Documentation

- **[README.md](README.md)** — Project overview, architecture summary, and build commands.
- **[DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md)** — Architecture deep dive, runtime flows, and developer recipes.
- **[LLM-WIKI.md](LLM-WIKI.md)** — Master single-file architectural reference and non-negotiable invariants.
- **[specs/](specs/)** — Technical specifications and Gherkin feature suites.

