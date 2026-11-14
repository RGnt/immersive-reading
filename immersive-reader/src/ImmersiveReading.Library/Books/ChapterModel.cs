namespace ImmersiveReading.Library.Books;

/// <summary>
/// Version of the chapter list shape stored for a book (spec 12).
/// 1 = one record per spine file (legacy, with generated "(Part N)"/"(Illustration)" records);
/// 2 = one record per ToC entry with member spine files.
/// </summary>
public static class ChapterModel
{
    public const int CurrentVersion = 2;
}
