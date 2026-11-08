using ImmersiveReading.Core.Library;

namespace ImmersiveReading.Core.Abstractions;

public interface IStudyPackCatalog
{
    StudyPackBookOverview GetBookStudyPack(string bookId);
}

