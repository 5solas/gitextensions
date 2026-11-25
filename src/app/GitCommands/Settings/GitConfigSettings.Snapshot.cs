// Logic for reading and writing local settings via GitConfigSnapshot.
// Separated to minimize upstream merge conflicts in GitConfigSettings.cs.

using System.Diagnostics.CodeAnalysis;
using GitExtensions.Extensibility;

namespace GitCommands.Settings;

public sealed partial class GitConfigSettings
{
    private bool TryGetAllValuesFromSnapshot([NotNullWhen(true)] out IEnumerable<(string Setting, string Value)>? values)
    {
        if (SnapshotProvider is null || GitSettingLevel != GitExtensions.Extensibility.Git.GitSettingLevel.Local)
        {
            values = null;
            return false;
        }

        values = GetAllValuesFromSnapshot();
        return true;
    }

    private IEnumerable<(string Setting, string Value)> GetAllValuesFromSnapshot()
    {
        GitConfigSnapshot snapshot = SnapshotProvider!.GetSnapshot();

        foreach (KeyValuePair<string, string?> modified in _modifiedSettings)
        {
            if (modified.Value is not null)
            {
                yield return (modified.Key, modified.Value);
            }
        }

        foreach ((string setting, string value) in snapshot.GetAllLocalValues())
        {
            if (!_modifiedSettings.ContainsKey(NormalizeSettingName(setting)))
            {
                yield return (setting, value);
            }
        }
    }

    private bool TryGetLocalValueFromSnapshot(string name, out string? value)
    {
        if (SnapshotProvider is not null && GitSettingLevel == GitExtensions.Extensibility.Git.GitSettingLevel.Local)
        {
            value = SnapshotProvider.GetSnapshot().GetLocalValue(name);
            return true;
        }

        value = null;
        return false;
    }

    private bool TryGetLocalValuesFromSnapshot(string name, [NotNullWhen(true)] out IReadOnlyList<string>? values)
    {
        if (SnapshotProvider is not null && GitSettingLevel == GitExtensions.Extensibility.Git.GitSettingLevel.Local)
        {
            if (_modifiedSettings.TryGetValue(name, out string? modifiedValue))
            {
                values = modifiedValue is not null ? [modifiedValue] : [];
                return true;
            }

            values = SnapshotProvider.GetSnapshot().GetLocalValues(name);
            return true;
        }

        values = null;
        return false;
    }

    private bool TrySetValueFromSnapshot(string name, string? value)
    {
        if (SnapshotProvider is null || GitSettingLevel != GitExtensions.Extensibility.Git.GitSettingLevel.Local)
        {
            return false;
        }

        GitConfigSnapshot snapshot = SnapshotProvider.GetSnapshot();
        if (snapshot.IsLocalMulti(name))
        {
            throw new UserExternalOperationException(new InvalidOperationException($"""
                Changing multi-value git settings is not supported. Tried to set "{name}" = "{value}".
                But "{name}" has multiple values in the {GitSettingLevel.ToString().ToLowerInvariant()} git config. This is unusual for this configuration entry.
                Either it is set several times, or a conditional include ("includeIf") overrides the value of the including config file.
                Please edit the config file(s) manually.
                """));
        }

        if (snapshot.TryGetLocalUnique(name, out string? storedValue) && value == storedValue)
        {
            _modifiedSettings.Remove(name);
        }
        else
        {
            _modifiedSettings[name] = value;
        }

        return true;
    }
}
