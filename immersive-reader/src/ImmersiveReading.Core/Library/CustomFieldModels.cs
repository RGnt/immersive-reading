namespace ImmersiveReading.Core.Library;

public sealed record CustomFieldDefinition(
    string FieldKey,
    string DisplayName,
    string FieldType,           // "url" | "text" | "number" | "rating" | "boolean" | "select" | "file_path"
    string TargetScope = "any", // "any" | "folder" | "book"
    bool InheritToChildren = true,
    IReadOnlyList<string>? Options = null,
    bool ShowOnCard = true,
    int SortOrder = 0,
    string CreatedAtUtc = ""
);

public sealed record CustomFieldValueInput(
    string FieldKey,
    string ValueText,
    double? ValueNumber = null
);

public sealed record EffectiveCustomFieldValue(
    string FieldKey,
    string DisplayName,
    string FieldType,
    string EffectiveValueText,
    double? EffectiveValueNumber,
    bool IsInherited,
    string SourceEntityType,    // "book" | "folder"
    string SourceEntityId,
    string SourceEntityName
);

