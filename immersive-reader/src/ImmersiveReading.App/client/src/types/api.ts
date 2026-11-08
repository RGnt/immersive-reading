// Strongly-typed TypeScript contracts matching ImmersiveReading C# DTOs

export interface AppConfig {
  directories: {
    defaultScanDirectory: string;
    extractorRootDirectory: string;
    pythonExecutablePath: string;
    flashcardsDirectory: string;
  };
  appearance: {
    themePreset: string;
    bgPrimary: string;
    bgSurface: string;
    textPrimary: string;
    textMuted: string;
    borderColor: string;
    accentColor: string;
    rubyColor: string;
    uiFontFamily: string;
  };
  reader: {
    fontFamily: string;
    writingMode: 'vertical-rl' | 'horizontal-tb';
    fontSizePx: number;
    lineHeight: number;
    furiganaMode: 'AllKanji' | 'Uncommon' | 'None';
    autoSavePosition: boolean;
    columnMaxWidthPx: number;
  };
  llm: {
    preset: string;
    endpointUrl: string;
    modelId: string;
    apiKey: string;
    timeoutSeconds: number;
    temperature: number;
    quizThresholdPercent: number;
    personaUnknownThreshold: number;
    enableOfflineHeuristicFallback: boolean;
  };
  dictionaries: {
    maxScanLength: number;
    maxDeinflections: number;
    maxResults: number;
    cardExportFormat: 'tsv' | 'anki' | 'both';
    autoClipboard: boolean;
  };
  library: LibrarySettings;
}

export interface LibrarySettings {
  primaryGrouping: 'series' | 'author' | 'category' | 'publisher' | 'tag' | 'none';
  sortWithinGrouping: 'series_and_volume' | 'title' | 'author' | 'added' | 'custom' | 'completion';
  enableShelves: boolean;
  excludedFolderIds?: string[];
  excludedTagIds?: string[];
  hideCompleted: boolean;
  defaultViewMode: 'grid' | 'table' | 'series';
}

export interface FolderNode {
  folderId: string;
  parentFolderId?: string;
  name: string;
  folderKind: string;
  description: string;
  icon: string;
  sortOrder: number;
  defaultBookSort: string;
  directBookCount: number;
  totalSubtreeBookCount: number;
  tags: string[];
  customFields: Record<string, string>;
  children: FolderNode[];
}

export interface TagRecord {
  tagId: string;
  name: string;
  colorHex: string;
  bookCount: number;
}

export interface CustomFieldDefinition {
  fieldId: string;
  name: string;
  fieldType: 'text' | 'number' | 'date' | 'enum';
  optionsJson?: string;
}

export interface BookRecord {
  bookId: string;
  epubSha256: string;
  folderId: string;
  folderPathBreadcrumb?: string;
  title: string;
  sortTitle: string;
  seriesName?: string;
  seriesIndex?: number;
  customSortOrder: number;
  authors: string;
  authorSort?: string;
  isbn?: string;
  publisher?: string;
  imprint?: string;
  publishedDate?: string;
  language: string;
  description?: string;
  isMetadataEdited: boolean;
  folderPath?: string;
  epubFileName?: string;
  fileSizeBytes: number;
  coverCachePath?: string;
  chapterCount: number;
  totalCharCount: number;
  statsComputed: boolean;
  writingMode: 'vertical-rl' | 'horizontal-tb';
  addedAtUtc: string;
  updatedAtUtc: string;
  hasStudyPack: boolean;
  overallCompletion: number;
  tags: string[];
  customFields: Record<string, string>;
}

export interface BookListResult {
  books: BookRecord[];
  totalCount: number;
  offset: number;
  limit: number;
  queryElapsedMs: number;
}

export interface BootstrapState {
  folders: FolderNode[];
  defaultScanDir: string;
  config: AppConfig;
}

export interface ChapterSummary {
  chapterIndex: number;
  spineHref: string;
  title: string;
  charCount: number;
  readProgressPercent: number;
}

export interface OpfOriginalMetadata {
  title?: string;
  authors?: string;
  sortTitle?: string;
  authorSort?: string;
  publisher?: string;
  language?: string;
  description?: string;
  rawOpfXml?: string;
}

export interface BookDetail {
  summary: BookRecord;
  opfOriginal?: OpfOriginalMetadata;
  chapters: ChapterSummary[];
  effectiveCustomFields?: any[];
  directBookCustomFields?: Record<string, string>;
}

export interface ReaderChapterPayload {
  bookId: string;
  chapterHref: string;
  chapterIndex: number;
  title: string;
  writingMode: 'vertical-rl' | 'horizontal-tb';
  totalTokens: number;
  uniqueLemmas: number;
  enrichedHtml: string;
  tokens: ReaderChapterToken[];
  sentences: ReaderSentence[];
}

export interface ReaderChapterToken {
  tokenId: number;
  surface: string;
  lemma: string;
  readingKana: string;
  pos: string;
  startOffset: number;
  endOffset: number;
  isKnown: boolean;
  isLearning: boolean;
}

export interface ReaderSentence {
  sentenceId: number;
  text: string;
  startTokenId: number;
  endTokenId: number;
}

export interface DictionaryDefinitionBlock {
  dictionaryTitle: string;
  headword: string;
  reading: string;
  glosses: string[];
  pitchAccent?: string[];
  frequencyRank?: number;
}

export interface KanjiDetail {
  kanji: string;
  meanings: string[];
  onyomi: string[];
  kunyomi: string[];
  strokeCount: number;
  grade?: number;
  jlpt?: number;
  frequency?: number;
  strokeSvg?: string;
}

export interface DictionaryResult {
  query: string;
  found: boolean;
  deinflectedQuery?: string;
  deinflectionReason?: string;
  headword: string;
  reading: string;
  definitionBlocks: DictionaryDefinitionBlock[];
  kanjiList: KanjiDetail[];
  isInVocabularyTracker: boolean;
  wordStatus: 'new' | 'learning' | 'known' | 'ignored';
}

export interface FlashcardMinedResult {
  cardId: string;
  headword: string;
  reading: string;
  sentence: string;
  sourceChapter: string;
  minedAt: string;
}

export interface VocabularySet {
  setId: string;
  setTitle: string;
  words: {
    lemma: string;
    reading: string;
    definition: string;
    occurrencesCount: number;
  }[];
}

export interface StudyPackOverview {
  bookId: string;
  hasActivePublication: boolean;
  generation?: string;
  vocabularySets: VocabularySet[];
  masterPdfs: {
    pdfName: string;
    pdfPath: string;
    category: 'kanji' | 'vocabulary' | 'grammar';
  }[];
}

export interface AiQuizQuestion {
  questionId: string;
  questionText: string;
  questionMode: 'mcq' | 'guided' | 'freeform';
  promptSyntheticChapterHref?: string;
  options: string[];
  chapterIndex: number;
  startTokenId: number;
  endTokenId: number;
  sourceParagraphId: number;
  evidenceQuote: string;
}

export interface AiQuizSubmissionResult {
  questionId: string;
  comprehensionScore: number;
  isCorrect: boolean;
  modelAnswer: string;
  critiqueExplanation: string;
  nextRecommendedStage: number;
}

export interface PersonaChatMessage {
  messageId: string;
  sender: 'user' | 'persona';
  personaName: string;
  content: string;
  timestamp: string;
  syntheticChapterHref?: string;
}

