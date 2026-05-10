using System;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// Resolves stable FieldIds for a component's runtime fields by matching them against persisted FieldR1 entries.
/// Ensures FieldIds remain stable across database reopens even when fields are added, removed, or renamed.
/// </summary>
/// <remarks>
/// <para>
/// Resolution priority for each runtime field:
/// 1. Explicit <c>[Field(FieldId=N)]</c> — use N; error if N conflicts with a different persisted field
/// 2. Name match in persisted fields — reuse the persisted FieldId
/// 3. PreviousName match — reuse the persisted FieldId, record the rename
/// 4. New field — assign <c>max(persisted) + 1</c>, incrementing and skipping used IDs
/// </para>
/// <para>
/// After all fields are resolved, call <see cref="Complete"/> to detect removed fields (persisted fields not matched by any runtime field).
/// </para>
/// </remarks>
internal class FieldIdResolver
{
    private readonly Dictionary<string, FieldR1> _persistedByName;
    private readonly Dictionary<int, string> _persistedNameById;
    private readonly HashSet<string> _matchedPersistedNames;
    private readonly List<(string OldName, string NewName, int FieldId)> _renames;
    private readonly HashSet<int> _usedIds;
    private int _nextNewId;

    public bool HasChanges { get; private set; }
    public IReadOnlyList<(string OldName, string NewName, int FieldId)> Renames => _renames;
    public IReadOnlyList<string> RemovedFieldNames { get; private set; }

    public FieldIdResolver(FieldR1[] persistedFields)
    {
        _persistedByName = new Dictionary<string, FieldR1>(persistedFields.Length);
        _persistedNameById = new Dictionary<int, string>(persistedFields.Length);
        _matchedPersistedNames = [];
        _renames = [];
        _usedIds = [];

        var maxId = -1;
        for (var i = 0; i < persistedFields.Length; i++)
        {
            var f = persistedFields[i];
            var name = f.Name.AsString;
            _persistedByName[name] = f;
            _persistedNameById[f.FieldId] = name;
            _usedIds.Add(f.FieldId);
            if (f.FieldId > maxId)
            {
                maxId = f.FieldId;
            }
        }

        _nextNewId = maxId + 1;
    }

    /// <summary>
    /// Resolves a stable FieldId for a runtime field by matching against persisted fields.
    /// </summary>
    /// <param name="fieldName">Current name of the field in the runtime struct.</param>
    /// <param name="previousName">Optional previous name from <c>[Field(PreviousName="...")]</c>.</param>
    /// <param name="explicitFieldId">Optional explicit FieldId from <c>[Field(FieldId=N)]</c>.</param>
    /// <returns>The resolved FieldId to use for this field.</returns>
    public int ResolveFieldId(string fieldName, string previousName, int? explicitFieldId)
    {
        // Priority 1: Explicit FieldId
        if (explicitFieldId.HasValue)
        {
            var id = explicitFieldId.Value;

            // Validate: if a persisted field has this ID, it must be the same field (by name or PreviousName)
            if (_persistedNameById.TryGetValue(id, out var existingName))
            {
                if (existingName != fieldName && (previousName == null || existingName != previousName))
                {
                    throw new InvalidOperationException(
                        $"Explicit FieldId {id} on field '{fieldName}' conflicts with persisted field '{existingName}' which already has FieldId {id}.");
                }
                _matchedPersistedNames.Add(existingName);
            }

            _usedIds.Add(id);
            return id;
        }

        // Priority 2: Name match in persisted fields
        if (_persistedByName.TryGetValue(fieldName, out var persisted))
        {
            _matchedPersistedNames.Add(fieldName);
            return persisted.FieldId;
        }

        // Priority 3: PreviousName match
        if (previousName != null && _persistedByName.TryGetValue(previousName, out var previous))
        {
            _matchedPersistedNames.Add(previousName);
            _renames.Add((previousName, fieldName, previous.FieldId));
            HasChanges = true;
            return previous.FieldId;
        }

        // Priority 4: New field — assign next available ID
        HasChanges = true;
        while (_usedIds.Contains(_nextNewId))
        {
            _nextNewId++;
        }

        if (_nextNewId > short.MaxValue)
        {
            throw new InvalidOperationException(
                $"FieldId overflow: next available FieldId ({_nextNewId}) exceeds maximum ({short.MaxValue}). " +
                "Too many fields have been added over the component's lifetime.");
        }

        var newId = _nextNewId++;
        _usedIds.Add(newId);
        return newId;
    }

    /// <summary>
    /// Detects persisted fields not matched by any runtime field (removed fields).
    /// Must be called after all runtime fields have been resolved via <see cref="ResolveFieldId"/>.
    /// </summary>
    public void Complete()
    {
        var removed = new List<string>();
        foreach (var kvp in _persistedByName)
        {
            if (!_matchedPersistedNames.Contains(kvp.Key))
            {
                removed.Add(kvp.Key);
            }
        }

        RemovedFieldNames = removed;
        if (removed.Count > 0)
        {
            HasChanges = true;
        }
    }
}
