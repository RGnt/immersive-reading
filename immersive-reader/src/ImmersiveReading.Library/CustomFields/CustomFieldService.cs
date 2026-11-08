using System.Globalization;
using System.Text.Json;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Library.Folders;
using ImmersiveReading.Library.Internal;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Library.CustomFields;

public sealed class CustomFieldService
{
    private readonly LibraryDatabase _db;
    private readonly TimeProvider _timeProvider;

    public CustomFieldService(LibraryDatabase db, TimeProvider? timeProvider = null)
    {
        _db = db;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<CustomFieldDefinition> ListCustomFieldDefinitions()
    {
        using var conn = _db.OpenConnection();
        return LoadCustomFieldDefinitionsInternal(conn);
    }

    public CustomFieldDefinition SaveCustomFieldDefinition(
        string fieldKey,
        string displayName,
        string fieldType = "text",
        string targetScope = "any",
        bool inheritToChildren = true,
        IReadOnlyList<string>? options = null,
        bool showOnCard = true,
        int sortOrder = 100)
    {
        if (string.IsNullOrWhiteSpace(fieldKey))
        {
            throw new ArgumentException("Field key cannot be empty.", nameof(fieldKey));
        }

        string cleanKey = fieldKey.Trim().ToLowerInvariant().Replace(' ', '_');
        string now = _timeProvider.GetUtcNow().UtcDateTime.ToString("O");
        string optionsJson = JsonSerializer.Serialize(options ?? []);

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO custom_field_definitions (
                field_key, display_name, field_type, target_scope, inherit_to_children, options_json, show_on_card, sort_order, created_at_utc
            ) VALUES (
                $key, $name, $type, $scope, $inherit, $opts, $show, $order, $now
            )
            ON CONFLICT(field_key) DO UPDATE SET
                display_name = excluded.display_name,
                field_type = excluded.field_type,
                target_scope = excluded.target_scope,
                inherit_to_children = excluded.inherit_to_children,
                options_json = excluded.options_json,
                show_on_card = excluded.show_on_card,
                sort_order = excluded.sort_order;
            """;
        cmd.Parameters.AddWithValue("$key", cleanKey);
        cmd.Parameters.AddWithValue("$name", displayName.Trim());
        cmd.Parameters.AddWithValue("$type", fieldType.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$scope", targetScope);
        cmd.Parameters.AddWithValue("$inherit", inheritToChildren ? 1 : 0);
        cmd.Parameters.AddWithValue("$opts", optionsJson);
        cmd.Parameters.AddWithValue("$show", showOnCard ? 1 : 0);
        cmd.Parameters.AddWithValue("$order", sortOrder);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        return new CustomFieldDefinition(cleanKey, displayName.Trim(), fieldType, targetScope, inheritToChildren, options ?? [], showOnCard, sortOrder, now);
    }

    public void SetCustomFieldValue(
        string entityType,
        string entityId,
        string fieldKey,
        string valueText,
        double? valueNumber = null)
    {
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        SetEntityCustomFieldsInTransaction(
            conn,
            tx,
            entityType,
            entityId,
            [new CustomFieldValueInput(fieldKey, valueText, valueNumber)]);

        if (string.Equals(entityType, "book", StringComparison.OrdinalIgnoreCase))
        {
            FtsIndexer.RebuildFtsForBookInTransaction(conn, tx, entityId);
        }

        tx.Commit();
    }

    public void SetEntityCustomFieldsInTransaction(
        SqliteConnection conn,
        SqliteTransaction tx,
        string entityType,
        string entityId,
        IReadOnlyList<CustomFieldValueInput> fields)
    {
        string now = _timeProvider.GetUtcNow().UtcDateTime.ToString("O");
        foreach (var f in fields)
        {
            if (string.IsNullOrWhiteSpace(f.ValueText) && !f.ValueNumber.HasValue)
            {
                using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM custom_field_values WHERE entity_type = $et AND entity_id = $eid AND field_key = $k;";
                del.Parameters.AddWithValue("$et", entityType);
                del.Parameters.AddWithValue("$eid", entityId);
                del.Parameters.AddWithValue("$k", f.FieldKey);
                del.ExecuteNonQuery();
                continue;
            }

            double? numVal = f.ValueNumber;
            if (!numVal.HasValue && double.TryParse(f.ValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                numVal = parsed;
            }

            string textVal = !string.IsNullOrWhiteSpace(f.ValueText)
                ? f.ValueText.Trim()
                : (numVal?.ToString(CultureInfo.InvariantCulture) ?? "");

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO custom_field_values (entity_type, entity_id, field_key, value_text, value_number, updated_at_utc)
                VALUES ($et, $eid, $k, $vt, $vn, $now)
                ON CONFLICT(entity_type, entity_id, field_key) DO UPDATE SET
                    value_text = excluded.value_text,
                    value_number = excluded.value_number,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            cmd.Parameters.AddWithValue("$et", entityType);
            cmd.Parameters.AddWithValue("$eid", entityId);
            cmd.Parameters.AddWithValue("$k", f.FieldKey);
            cmd.Parameters.AddWithValue("$vt", textVal);
            cmd.Parameters.AddWithValue("$vn", (object?)numVal ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }
    }

    public static List<CustomFieldDefinition> LoadCustomFieldDefinitionsInternal(SqliteConnection conn)
    {
        var list = new List<CustomFieldDefinition>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT field_key, display_name, field_type, target_scope, inherit_to_children,
                   options_json, show_on_card, sort_order, created_at_utc
            FROM custom_field_definitions
            ORDER BY sort_order ASC, display_name COLLATE NOCASE ASC;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var opts = JsonSerializer.Deserialize<List<string>>(r.GetString(5)) ?? [];
            list.Add(new CustomFieldDefinition(
                FieldKey: r.GetString(0),
                DisplayName: r.GetString(1),
                FieldType: r.GetString(2),
                TargetScope: r.GetString(3),
                InheritToChildren: r.GetInt32(4) != 0,
                Options: opts,
                ShowOnCard: r.GetInt32(6) != 0,
                SortOrder: r.GetInt32(7),
                CreatedAtUtc: r.GetString(8)));
        }
        return list;
    }

    public static Dictionary<(string EntityType, string EntityId), Dictionary<string, (string ValueText, double? ValueNumber)>> LoadAllCustomFieldValues(SqliteConnection conn)
    {
        var map = new Dictionary<(string, string), Dictionary<string, (string, double?)>>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT entity_type, entity_id, field_key, value_text, value_number FROM custom_field_values;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var key = (r.GetString(0), r.GetString(1));
            if (!map.TryGetValue(key, out var inner))
            {
                inner = new Dictionary<string, (string, double?)>(StringComparer.OrdinalIgnoreCase);
                map[key] = inner;
            }
            string fk = r.GetString(2);
            string vt = r.GetString(3);
            double? vn = r.IsDBNull(4) ? null : r.GetDouble(4);
            inner[fk] = (vt, vn);
        }
        return map;
    }

    internal static IReadOnlyList<EffectiveCustomFieldValue> ResolveEffectiveCustomFieldsForBook(
        string bookId,
        string bookTitle,
        string? folderId,
        Dictionary<string, RawFolderRow> folderLookup,
        IReadOnlyList<CustomFieldDefinition> definitions,
        Dictionary<(string EntityType, string EntityId), Dictionary<string, (string ValueText, double? ValueNumber)>> allValues)
    {
        var result = new List<EffectiveCustomFieldValue>();
        allValues.TryGetValue(("book", bookId), out var directBookVals);

        foreach (var def in definitions)
        {
            // 1. Check direct value on the book itself
            if (directBookVals is not null &&
                directBookVals.TryGetValue(def.FieldKey, out var directVal) &&
                (!string.IsNullOrWhiteSpace(directVal.ValueText) || directVal.ValueNumber.HasValue))
            {
                result.Add(new EffectiveCustomFieldValue(
                    FieldKey: def.FieldKey,
                    DisplayName: def.DisplayName,
                    FieldType: def.FieldType,
                    EffectiveValueText: directVal.ValueText,
                    EffectiveValueNumber: directVal.ValueNumber,
                    IsInherited: false,
                    SourceEntityType: "book",
                    SourceEntityId: bookId,
                    SourceEntityName: bookTitle));
                continue;
            }

            // 2. If InheritToChildren is enabled, walk up the folder ancestor chain
            if (def.InheritToChildren && !string.IsNullOrEmpty(folderId))
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                string? curFolderId = folderId;
                while (!string.IsNullOrEmpty(curFolderId) &&
                       visited.Add(curFolderId) &&
                       folderLookup.TryGetValue(curFolderId, out var folderRow))
                {
                    if (allValues.TryGetValue(("folder", curFolderId), out var fVals) &&
                        fVals.TryGetValue(def.FieldKey, out var fVal) &&
                        (!string.IsNullOrWhiteSpace(fVal.ValueText) || fVal.ValueNumber.HasValue))
                    {
                        result.Add(new EffectiveCustomFieldValue(
                            FieldKey: def.FieldKey,
                            DisplayName: def.DisplayName,
                            FieldType: def.FieldType,
                            EffectiveValueText: fVal.ValueText,
                            EffectiveValueNumber: fVal.ValueNumber,
                            IsInherited: true,
                            SourceEntityType: "folder",
                            SourceEntityId: curFolderId,
                            SourceEntityName: folderRow.Name));
                        break;
                    }
                    curFolderId = folderRow.ParentFolderId;
                }
            }
        }

        return result;
    }

    internal static IReadOnlyList<EffectiveCustomFieldValue> ResolveEffectiveCustomFieldsForFolder(
        string folderId,
        Dictionary<string, RawFolderRow> folderLookup,
        IReadOnlyList<CustomFieldDefinition> definitions,
        Dictionary<(string EntityType, string EntityId), Dictionary<string, (string ValueText, double? ValueNumber)>> allValues)
    {
        var result = new List<EffectiveCustomFieldValue>();
        if (!folderLookup.TryGetValue(folderId, out var selfFolder)) return result;

        foreach (var def in definitions)
        {
            if (allValues.TryGetValue(("folder", folderId), out var directVals) &&
                directVals.TryGetValue(def.FieldKey, out var dVal) &&
                (!string.IsNullOrWhiteSpace(dVal.ValueText) || dVal.ValueNumber.HasValue))
            {
                result.Add(new EffectiveCustomFieldValue(
                    FieldKey: def.FieldKey,
                    DisplayName: def.DisplayName,
                    FieldType: def.FieldType,
                    EffectiveValueText: dVal.ValueText,
                    EffectiveValueNumber: dVal.ValueNumber,
                    IsInherited: false,
                    SourceEntityType: "folder",
                    SourceEntityId: folderId,
                    SourceEntityName: selfFolder.Name));
                continue;
            }

            if (def.InheritToChildren && !string.IsNullOrEmpty(selfFolder.ParentFolderId))
            {
                var visited = new HashSet<string>(StringComparer.Ordinal) { folderId };
                string? curId = selfFolder.ParentFolderId;
                while (!string.IsNullOrEmpty(curId) &&
                       visited.Add(curId) &&
                       folderLookup.TryGetValue(curId, out var ancRow))
                {
                    if (allValues.TryGetValue(("folder", curId), out var ancVals) &&
                        ancVals.TryGetValue(def.FieldKey, out var aVal) &&
                        (!string.IsNullOrWhiteSpace(aVal.ValueText) || aVal.ValueNumber.HasValue))
                    {
                        result.Add(new EffectiveCustomFieldValue(
                            FieldKey: def.FieldKey,
                            DisplayName: def.DisplayName,
                            FieldType: def.FieldType,
                            EffectiveValueText: aVal.ValueText,
                            EffectiveValueNumber: aVal.ValueNumber,
                            IsInherited: true,
                            SourceEntityType: "folder",
                            SourceEntityId: curId,
                            SourceEntityName: ancRow.Name));
                        break;
                    }
                    curId = ancRow.ParentFolderId;
                }
            }
        }

        return result;
    }
}

