namespace ImmersiveReading.Core.Prompts;

/// <param name="Owner">Who registered the built-in (a plugin id): the prompt editor groups by it.</param>
/// <param name="OverrideError">Why the user's file is not in use, when it exists but failed validation; null when there is none or it is fine.</param>
public sealed record PromptInfo(
    string Name, string Owner, int Version, string Purpose, string Response,
    IReadOnlyList<PromptVariable> Variables, string BuiltInText, string? OverrideText, string? OverrideError, bool UsingOverride);

public sealed record PromptCheck(bool Ok, string? Error, int Line, IReadOnlyList<string> Warnings);

/// <summary>
/// The prompts of every feature, in one place: built-in templates are registered by their owners, the user may replace any of
/// them with an edited copy (spec 19, PT-5). An edited copy is used only if it passes the same validation as the built-in
/// **and** keeps the contract of the built-in; otherwise the built-in stays in force and the reason is shown to the user.
/// </summary>
public interface IPromptCatalog
{
    IReadOnlyList<PromptInfo> List();
    PromptInfo? Get(string name);

    /// <summary>The template in force (valid user copy, else the built-in). Throws for an unknown name.</summary>
    PromptTemplate Template(string name);
    string Render(string name, IReadOnlyDictionary<string, object?>? values = null);

    /// <summary>Validates a candidate copy against the built-in's contract without saving it.</summary>
    PromptCheck Check(string name, string text);
    /// <summary>Saves a copy. Throws <see cref="PromptTemplateException"/> (nothing is written) when <see cref="Check"/> would fail.</summary>
    void SaveOverride(string name, string text);
    bool ResetOverride(string name);

    IDisposable RegisterBuiltIns(string owner, IEnumerable<string> templateTexts);

    /// <summary>Writes the text in force of every template of an owner as <c>name.prompt.md</c> files (for a separate process such as the Python builder).</summary>
    int WriteEffective(string owner, string directory);
}

/// <param name="overrideDirectory">Where the user's edited copies live. Empty = built-ins only (a catalog that cannot save copies).</param>
public sealed class PromptCatalog(string overrideDirectory) : IPromptCatalog
{
    private sealed class Entry
    {
        public required string Owner { get; init; }
        public required string BuiltInText { get; init; }
        public required PromptTemplate BuiltIn { get; init; }
        public string? OverrideText { get; set; }
        public PromptTemplate? Override { get; set; }
        public string? OverrideError { get; set; }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    private string PathOf(string name) => Path.Combine(overrideDirectory, name + ".prompt.md");

    // ------------------------------------------------------------------ registration
    public IDisposable RegisterBuiltIns(string owner, IEnumerable<string> templateTexts)
    {
        var parsed = templateTexts.Select(t => (Text: t, Template: PromptTemplate.Parse(t, $"built-in prompt of {owner}"))).ToList();
        lock (_lock)
        {
            foreach (var (_, template) in parsed)
                if (_entries.TryGetValue(template.Name, out var existing) && existing.Owner != owner)
                    throw new InvalidOperationException($"Prompt '{template.Name}' is already registered by '{existing.Owner}'.");
            foreach (string stale in _entries.Where(e => e.Value.Owner == owner).Select(e => e.Key).ToList()) _entries.Remove(stale);   // re-registering replaces

            foreach (var (text, template) in parsed)
            {
                var entry = new Entry { Owner = owner, BuiltInText = text.Replace("\r\n", "\n"), BuiltIn = template };
                LoadOverride(template.Name, entry);
                _entries[template.Name] = entry;
            }
        }
        var names = parsed.Select(p => p.Template.Name).ToList();
        return new Registration(() => { lock (_lock) { foreach (string n in names) if (_entries.TryGetValue(n, out var e) && e.Owner == owner) _entries.Remove(n); } });
    }

    private void LoadOverride(string name, Entry entry)
    {
        if (overrideDirectory.Length == 0) return;
        string path = PathOf(name);
        if (!File.Exists(path)) return;
        try
        {
            string text = File.ReadAllText(path).Replace("\r\n", "\n");
            entry.OverrideText = text;
            var check = Validate(entry.BuiltIn, text, out var template);
            if (check.Ok) entry.Override = template; else entry.OverrideError = check.Error;
        }
        catch (IOException ex)
        {
            entry.OverrideError = $"The file could not be read: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------ validation
    private static PromptCheck Validate(PromptTemplate builtIn, string text, out PromptTemplate? template)
    {
        template = null;
        try
        {
            var candidate = PromptTemplate.Parse(text, $"{builtIn.Name}.prompt.md");
            if (candidate.Name != builtIn.Name) return Fail($"The name must stay '{builtIn.Name}' (it is '{candidate.Name}').");
            if (candidate.Version < builtIn.Version) return Fail($"The version must be at least {builtIn.Version} (the built-in's); it is {candidate.Version}.");
            foreach (var wanted in builtIn.Variables.Values)
            {
                if (!candidate.Variables.TryGetValue(wanted.Name, out var have))
                    return Fail($"The variable '{wanted.Name}' must stay declared: the program fills it in. Declare it in the header even if the body does not use it.");
                if (have.Type != wanted.Type) return Fail($"The variable '{wanted.Name}' must keep type '{wanted.Type}'.");
                if (wanted.Untrusted && !have.Untrusted) return Fail($"The variable '{wanted.Name}' holds text from a book or from the user and must stay 'untrusted'.");
            }
            string? extra = candidate.Variables.Values.FirstOrDefault(v => !builtIn.Variables.ContainsKey(v.Name) && v.Required)?.Name;
            if (extra is not null) return Fail($"The new variable '{extra}' cannot be 'required': the program does not fill it in. Give it a default.");

            template = candidate;
            var warnings = candidate.Unused.Select(u => $"The variable '{u}' is declared but not used in the text.").ToList();
            return new PromptCheck(true, null, 0, warnings);
        }
        catch (PromptTemplateException ex)
        {
            return new PromptCheck(false, ex.Message, ex.Line, []);
        }

        static PromptCheck Fail(string message) => new(false, message, 0, []);
    }

    public PromptCheck Check(string name, string text)
    {
        Entry entry = Require(name);
        return Validate(entry.BuiltIn, text.Replace("\r\n", "\n"), out _);
    }

    // ------------------------------------------------------------------ use
    private Entry Require(string name)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(name, out var e) ? e : throw new KeyNotFoundException($"There is no prompt called '{name}'.");
        }
    }

    private static PromptInfo ToInfo(string name, Entry e)
    {
        var shown = e.Override ?? e.BuiltIn;
        return new PromptInfo(name, e.Owner, shown.Version, e.BuiltIn.Purpose, e.BuiltIn.Response, e.BuiltIn.Variables.Values.ToList(),
            e.BuiltInText, e.OverrideText, e.OverrideError, e.Override is not null);
    }

    public IReadOnlyList<PromptInfo> List()
    {
        lock (_lock)
        {
            return _entries.OrderBy(e => e.Value.Owner, StringComparer.Ordinal).ThenBy(e => e.Key, StringComparer.Ordinal).Select(e => ToInfo(e.Key, e.Value)).ToList();
        }
    }

    public PromptInfo? Get(string name)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(name, out var e) ? ToInfo(name, e) : null;
        }
    }

    public PromptTemplate Template(string name)
    {
        var e = Require(name);
        lock (_lock) { return e.Override ?? e.BuiltIn; }
    }

    public string Render(string name, IReadOnlyDictionary<string, object?>? values = null) => Template(name).Render(values);

    // ------------------------------------------------------------------ editing
    public void SaveOverride(string name, string text)
    {
        var entry = Require(name);
        text = text.Replace("\r\n", "\n");
        var check = Validate(entry.BuiltIn, text, out var template);
        if (!check.Ok) throw new PromptTemplateException(check.Error!, check.Line);

        if (overrideDirectory.Length == 0) throw new InvalidOperationException("This prompt catalog has no folder for edited copies.");
        Directory.CreateDirectory(overrideDirectory);
        string path = PathOf(name), temp = path + ".tmp";
        File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
        lock (_lock)
        {
            entry.OverrideText = text;
            entry.Override = template;
            entry.OverrideError = null;
        }
    }

    public bool ResetOverride(string name)
    {
        var entry = Require(name);
        string path = PathOf(name);
        bool existed = overrideDirectory.Length > 0 && File.Exists(path);
        if (existed) File.Delete(path);
        lock (_lock)
        {
            entry.OverrideText = null;
            entry.Override = null;
            entry.OverrideError = null;
        }
        return existed;
    }

    public int WriteEffective(string owner, string directory)
    {
        List<(string Name, string Text)> files;
        lock (_lock)
        {
            files = _entries.Where(e => e.Value.Owner == owner)
                .Select(e => (e.Key, e.Value.Override is not null ? e.Value.OverrideText! : e.Value.BuiltInText)).ToList();
        }
        Directory.CreateDirectory(directory);
        foreach (string stale in Directory.GetFiles(directory, "*.prompt.md")) File.Delete(stale);
        foreach (var (name, text) in files) File.WriteAllText(Path.Combine(directory, name + ".prompt.md"), text, new System.Text.UTF8Encoding(false));
        return files.Count;
    }

    private sealed class Registration(Action onDispose) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) onDispose();
        }
    }
}
