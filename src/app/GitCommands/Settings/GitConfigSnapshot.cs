using GitExtensions.Extensibility.Configurations;
using GitExtensions.Extensibility.Git;

namespace GitCommands.Settings;

public readonly record struct GitConfigEntry(string Scope, string Key, string Value);

public sealed class GitConfigSnapshot
{
    public IReadOnlyList<GitConfigEntry> Entries { get; }

    private readonly Dictionary<string, string> _effectiveLast = [];
    private readonly Dictionary<string, List<string>> _effectiveAll = [];

    private readonly Dictionary<string, string> _localUnique = [];
    private readonly Dictionary<string, List<string>> _localMulti = [];
    private readonly List<(string Setting, string Value)> _localEntries = [];

    public bool HasInsteadOf { get; }
    public IReadOnlyList<string> RemoteNames { get; }

    internal IReadOnlyList<string>? CachedRemoteNames { get; set; }
    internal IReadOnlyList<Remote>? CachedRemotes { get; set; }

    public GitConfigSnapshot(IReadOnlyList<GitConfigEntry> entries)
    {
        Entries = entries;
        bool hasInsteadOf = false;
        HashSet<string> remoteNames = [];

        for (int index = 0; index < entries.Count; index++)
        {
            GitConfigEntry entry = entries[index];
            string normalizedKey = GitConfigSettingsBase.NormalizeSettingName(entry.Key);

            _effectiveLast[normalizedKey] = entry.Value;

            if (!_effectiveAll.TryGetValue(normalizedKey, out List<string>? allValues))
            {
                allValues = [];
                _effectiveAll[normalizedKey] = allValues;
            }

            allValues.Add(entry.Value);

            if (string.Equals(entry.Scope, "local", StringComparison.OrdinalIgnoreCase))
            {
                _localEntries.Add((entry.Key, entry.Value));

                if (_localMulti.TryGetValue(normalizedKey, out List<string>? localValues))
                {
                    localValues.Add(entry.Value);
                }
                else if (!_localUnique.TryAdd(normalizedKey, entry.Value))
                {
                    string firstValue = _localUnique[normalizedKey];
                    _localUnique.Remove(normalizedKey);
                    _localMulti.Add(normalizedKey, [firstValue, entry.Value]);
                }
            }

            if (!hasInsteadOf && entry.Key.StartsWith("url.", StringComparison.OrdinalIgnoreCase))
            {
                if (entry.Key.EndsWith(".insteadof", StringComparison.OrdinalIgnoreCase)
                    || entry.Key.EndsWith(".pushinsteadof", StringComparison.OrdinalIgnoreCase))
                {
                    hasInsteadOf = true;
                }
            }

            if (entry.Key.StartsWith("remote.", StringComparison.OrdinalIgnoreCase) && entry.Key.IndexOf('.') != entry.Key.LastIndexOf('.'))
            {
                (string section, string? subsection, string _) = IGitConfigSettingsGetter.SplitSetting(entry.Key);
                if (string.Equals(section, "remote", StringComparison.OrdinalIgnoreCase) && subsection is not null)
                {
                    remoteNames.Add(subsection);
                }
            }
        }

        HasInsteadOf = hasInsteadOf;
        RemoteNames = remoteNames.OrderBy(static name => name, StringComparer.Ordinal).ToList();
    }

    public string? GetEffectiveValue(string normalizedName)
    {
        return _effectiveLast.TryGetValue(normalizedName, out string? value) ? value : null;
    }

    public IReadOnlyList<string> GetEffectiveValues(string normalizedName)
    {
        return _effectiveAll.TryGetValue(normalizedName, out List<string>? values) ? values : [];
    }

    public string? GetLocalValue(string normalizedName)
    {
        if (_localUnique.TryGetValue(normalizedName, out string? value))
        {
            return value;
        }

        return _localMulti.TryGetValue(normalizedName, out List<string>? values) ? values[^1] : null;
    }

    public IReadOnlyList<string> GetLocalValues(string normalizedName)
    {
        return _localMulti.TryGetValue(normalizedName, out List<string>? values)
            ? values
            : GetLocalValue(normalizedName) is string value
                ? [value]
                : [];
    }

    public IEnumerable<(string Setting, string Value)> GetAllLocalValues()
    {
        foreach (KeyValuePair<string, string> unique in _localUnique)
        {
            yield return (unique.Key, unique.Value);
        }

        foreach (KeyValuePair<string, List<string>> multi in _localMulti)
        {
            foreach (string value in multi.Value)
            {
                yield return (multi.Key, value);
            }
        }
    }

    public bool IsLocalMulti(string normalizedName)
    {
        return _localMulti.ContainsKey(normalizedName);
    }

    public bool TryGetLocalUnique(string normalizedName, out string? value)
    {
        return _localUnique.TryGetValue(normalizedName, out value);
    }

    public static GitConfigSnapshot Parse(string output)
    {
        ReadOnlySpan<char> span = output.AsSpan().TrimEnd('\0');
        if (span.IsEmpty)
        {
            return new GitConfigSnapshot([]);
        }

        List<GitConfigEntry> entries = [];
        string? currentScope = null;

        foreach (Range range in span.Split('\0'))
        {
            ReadOnlySpan<char> token = span[range];
            if (currentScope is null)
            {
                currentScope = token.ToString();
            }
            else
            {
                int linefeedIndex = token.IndexOf('\n');
                string key;
                string value;
                if (linefeedIndex >= 0)
                {
                    key = token[..linefeedIndex].ToString();
                    value = token[(linefeedIndex + 1)..].ToString();
                }
                else
                {
                    key = token.ToString();
                    value = "true";
                }

                entries.Add(new GitConfigEntry(currentScope, key, value));
                currentScope = null;
            }
        }

        return new GitConfigSnapshot(entries);
    }

    public static GitConfigSnapshot CreateFromLegacy(string effectiveOutput, string localOutput)
    {
        List<GitConfigEntry> entries = [];
        ParseLegacy(effectiveOutput, "effective", entries);
        ParseLegacy(localOutput, "local", entries);
        return new GitConfigSnapshot(entries);

        static void ParseLegacy(string output, string scope, List<GitConfigEntry> list)
        {
            ReadOnlySpan<char> span = output.AsSpan().TrimEnd('\0');
            if (span.IsEmpty)
            {
                return;
            }

            foreach (Range range in span.Split('\0'))
            {
                ReadOnlySpan<char> token = span[range];
                int linefeedIndex = token.IndexOf('\n');
                string key;
                string value;
                if (linefeedIndex >= 0)
                {
                    key = token[..linefeedIndex].ToString();
                    value = token[(linefeedIndex + 1)..].ToString();
                }
                else
                {
                    key = token.ToString();
                    value = "true";
                }

                list.Add(new GitConfigEntry(scope, key, value));
            }
        }
    }
}
