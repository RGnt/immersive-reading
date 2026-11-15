using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ImmersiveReading.Core.Prompts;

/// <param name="Line">1-based line of the file the problem is on, when it is known (body errors); 0 otherwise.</param>
public sealed class PromptTemplateException(string message, int line = 0) : Exception(message)
{
    public int Line { get; } = line;
}

public sealed record PromptVariable(string Name, bool Required, bool Untrusted, string Type, string? Default, IReadOnlyList<string> Enum);

/// <summary>
/// A prompt template (spec 19, part A). The C# twin of <c>knowledge-graph/immersive_knowledge/templates.py</c>: the same file
/// format, the same tiny syntax (<c>{{name}}</c>, <c>{{#if name}}</c>, <c>{{#each name}}</c> with <c>{{this}}</c>), the same strict rules,
/// and the same shared vectors (<c>specs/fixtures/prompt-vectors.json</c>) run by both test suites.
/// </summary>
public sealed class PromptTemplate
{
    private static readonly Regex NameRx = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex TokenRx = new(@"\{\{\s*(#if|#each|/if|/each)?\s*([A-Za-z_][A-Za-z0-9_]*)?\s*\}\}", RegexOptions.Compiled);
    private static readonly Regex IntRx = new(@"^-?\d+$", RegexOptions.Compiled);
    private static readonly string[] Types = ["string", "int", "list"];

    public string Name { get; private set; } = "";
    public int Version { get; private set; }
    public string Purpose { get; private set; } = "";
    public string Response { get; private set; } = "";
    public IReadOnlyDictionary<string, PromptVariable> Variables { get; private set; } = new Dictionary<string, PromptVariable>();
    /// <summary>Declared variables the body never uses (a warning, not an error).</summary>
    public IReadOnlyList<string> Unused { get; private set; } = [];

    private List<Node> _body = [];

    private abstract record Node;
    private sealed record TextNode(string Text) : Node;
    private sealed record VarNode(string Name) : Node;
    private sealed record ThisNode : Node;
    private sealed record BlockNode(bool Each, string Name, List<Node> Children) : Node;

    private PromptTemplate() { }

    // ------------------------------------------------------------------ loading
    public static PromptTemplate Parse(string text, string source = "<template>")
    {
        text = text.Replace("\r\n", "\n");
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
            throw new PromptTemplateException($"{source}: a template must start with a '---' front matter block", 1);
        int end = text.IndexOf("\n---\n", 3, StringComparison.Ordinal);
        if (end < 0) throw new PromptTemplateException($"{source}: front matter is not closed with '---'", 1);
        string header = end >= 4 ? text[4..end] : "";
        string bodyText = text[(end + 5)..];
        int bodyFirstLine = text[..(end + 5)].Count(c => c == '\n') + 1;

        var meta = new Dictionary<string, string>();
        var specs = new Dictionary<string, PromptVariable>(StringComparer.Ordinal);
        bool inVariables = false;
        foreach (string line in header.Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            if (line.StartsWith("variables:", StringComparison.Ordinal)) { inVariables = true; continue; }
            if (inVariables && line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                var spec = ParseVariable(line.TrimStart()[2..], source);
                if (!specs.TryAdd(spec.Name, spec)) throw new PromptTemplateException($"{source}: variable '{spec.Name}' is declared twice");
                continue;
            }
            inVariables = false;
            int colon = line.IndexOf(':');
            if (colon < 0) throw new PromptTemplateException($"{source}: cannot read front matter line '{line}'");
            meta[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        string name = meta.GetValueOrDefault("name") ?? "";
        if (name.Length == 0)
        {
            string file = Path.GetFileName(source);
            int cut = file.IndexOf(".prompt", StringComparison.Ordinal);
            name = cut >= 0 ? file[..cut] : file;
        }
        if (name.Length == 0) throw new PromptTemplateException($"{source}: missing 'name'");
        if (!int.TryParse(meta.GetValueOrDefault("version", "1"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int version))
            throw new PromptTemplateException($"template '{name}': 'version' must be an integer");

        var (tree, used) = ParseBody(bodyText, name, specs, bodyFirstLine);
        return new PromptTemplate
        {
            Name = name, Version = version, Purpose = meta.GetValueOrDefault("purpose") ?? "", Response = meta.GetValueOrDefault("response") ?? "",
            Variables = specs, _body = tree, Unused = specs.Keys.Where(k => !used.Contains(k)).Order(StringComparer.Ordinal).ToList()
        };
    }

    private static PromptVariable ParseVariable(string line, string source)
    {
        var parts = line.Split('|').Select(p => p.Trim()).ToList();
        string name = parts[0];
        if (!NameRx.IsMatch(name)) throw new PromptTemplateException($"{source}: invalid variable name '{name}'");
        bool required = false, untrusted = false;
        string type = "string";
        string? def = null;
        IReadOnlyList<string> en = [];
        foreach (string token in parts.Skip(1))
        {
            if (token == "required") required = true;
            else if (token == "untrusted") untrusted = true;
            else if (token.StartsWith("default=", StringComparison.Ordinal)) def = token["default=".Length..];
            else if (token.StartsWith("enum=", StringComparison.Ordinal))
                en = token["enum=".Length..].Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
            else if (token.StartsWith("type=", StringComparison.Ordinal))
            {
                type = token["type=".Length..];
                if (!Types.Contains(type)) throw new PromptTemplateException($"{source}: variable '{name}' has unknown type '{type}'");
            }
            else throw new PromptTemplateException($"{source}: variable '{name}' has unknown option '{token}'");
        }
        if (required && def is not null) throw new PromptTemplateException($"{source}: variable '{name}' cannot be both required and have a default");
        if (type == "list" && def is not null) throw new PromptTemplateException($"{source}: list variable '{name}' cannot have a default");
        if (type == "int" && def is not null && !IntRx.IsMatch(def)) throw new PromptTemplateException($"{source}: default of '{name}' is not an integer");
        if (en.Count > 0 && def is not null && !en.Contains(def)) throw new PromptTemplateException($"{source}: default of '{name}' is not in its enum");
        return new PromptVariable(name, required, untrusted, type, def, en);
    }

    private static (List<Node> Tree, HashSet<string> Used) ParseBody(string body, string template, Dictionary<string, PromptVariable> specs, int firstLine)
    {
        var root = new List<Node>();
        var stack = new Stack<(bool Each, List<Node> Parent)>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var current = root;
        int pos = 0;

        PromptTemplateException Fail(string message, int at)
            => new($"template '{template}': {message}", firstLine + body[..Math.Min(at, body.Length)].Count(c => c == '\n'));

        foreach (Match match in TokenRx.Matches(body))
        {
            if (match.Index > pos) current.Add(new TextNode(body[pos..match.Index]));
            pos = match.Index + match.Length;
            string directive = match.Groups[1].Success ? match.Groups[1].Value : "";
            string? name = match.Groups[2].Success ? match.Groups[2].Value : null;

            if (directive is "/if" or "/each")
            {
                if (stack.Count == 0 || stack.Peek().Each != (directive == "/each")) throw Fail($"unexpected {{{{{directive}}}}}", match.Index);
                current = stack.Pop().Parent;
                continue;
            }
            if (name is null) throw Fail("empty {{ }}", match.Index);
            if (name == "this")
            {
                if (directive.Length > 0 || !stack.Any(s => s.Each)) throw Fail("{{this}} can only be used inside {{#each}}", match.Index);
                current.Add(new ThisNode());
                continue;
            }
            if (!specs.TryGetValue(name, out var spec)) throw Fail($"variable '{name}' is used but not declared", match.Index);
            used.Add(name);
            if (directive is "#if" or "#each")
            {
                bool each = directive == "#each";
                if (each && spec.Type != "list") throw Fail($"'{name}' is iterated with #each but is not declared type=list", match.Index);
                var node = new BlockNode(each, name, []);
                current.Add(node);
                stack.Push((each, current));
                current = node.Children;
            }
            else
            {
                if (spec.Type == "list") throw Fail($"list variable '{name}' must be used with {{{{#each {name}}}}}", match.Index);
                current.Add(new VarNode(name));
            }
        }
        if (stack.Count > 0) throw Fail($"{{{{#{(stack.Peek().Each ? "each" : "if")}}}}} is not closed", body.Length);
        if (pos < body.Length) current.Add(new TextNode(body[pos..]));
        return (root, used);
    }

    // ------------------------------------------------------------------ rendering
    private static string Neutralise(string value, string name)
    {
        string marker = name.ToUpperInvariant();
        return value.Replace($"{marker}>>>", $"{marker}> > >", StringComparison.Ordinal).Replace($"<<<{marker}", $"< < <{marker}", StringComparison.Ordinal);
    }

    private static bool IsNumber(object v) => v is sbyte or byte or short or ushort or int or uint or long or ulong;

    private Dictionary<string, object> Resolve(IReadOnlyDictionary<string, object?> values)
    {
        string? unknown = values.Keys.Where(k => !Variables.ContainsKey(k)).Order(StringComparer.Ordinal).FirstOrDefault();
        if (unknown is not null) throw new PromptTemplateException($"template '{Name}': unknown variable '{unknown}'");

        var resolved = new Dictionary<string, object>();
        foreach (var (name, spec) in Variables)
        {
            values.TryGetValue(name, out var value);
            if (value is null)
            {
                if (spec.Default is not null) value = spec.Default;
                else if (spec.Required) throw new PromptTemplateException($"template '{Name}': missing required variable '{name}'");
                else value = spec.Type == "list" ? new List<string>() : "";
            }

            if (spec.Type == "list")
            {
                if (value is string || value is not System.Collections.IEnumerable seq)
                    throw new PromptTemplateException($"template '{Name}': variable '{name}' must be a list of strings");
                var items = new List<string>();
                foreach (object? item in seq)
                {
                    if (item is string s) items.Add(s);
                    else if (item is not null && IsNumber(item)) items.Add(Convert.ToString(item, CultureInfo.InvariantCulture)!);
                    else throw new PromptTemplateException($"template '{Name}': variable '{name}' must be a list of strings");
                }
                value = items;
            }
            else if (spec.Type == "int")
            {
                if (value is bool || !(IsNumber(value) || (value is string t && IntRx.IsMatch(t))))
                    throw new PromptTemplateException($"template '{Name}': variable '{name}' must be an integer");
                value = Convert.ToString(Convert.ToInt64(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)!;
            }
            else
            {
                if (value is bool || !(value is string || IsNumber(value) || value is float or double or decimal))
                    throw new PromptTemplateException($"template '{Name}': variable '{name}' must be a string");
                string s = Convert.ToString(value, CultureInfo.InvariantCulture)!;
                if (spec.Enum.Count > 0 && s.Length > 0 && !spec.Enum.Contains(s))
                    throw new PromptTemplateException($"template '{Name}': variable '{name}' must be one of {string.Join(", ", spec.Enum)} (got '{s}')");
                value = s;
                if (spec.Required && s.Trim().Length == 0) throw new PromptTemplateException($"template '{Name}': required variable '{name}' is empty");
            }
            resolved[name] = value;
        }
        return resolved;
    }

    public string Render(IReadOnlyDictionary<string, object?>? values = null)
    {
        var resolved = Resolve(values ?? new Dictionary<string, object?>());
        var sb = new StringBuilder();

        void Emit(List<Node> nodes, string? self = null, string? selfUntrusted = null)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case TextNode t: sb.Append(t.Text); break;
                    case ThisNode: sb.Append(selfUntrusted is not null ? Neutralise(self!, selfUntrusted) : self); break;
                    case VarNode v:
                    {
                        string value = (string)resolved[v.Name];
                        if (Variables[v.Name].Untrusted)
                        {
                            string marker = v.Name.ToUpperInvariant();
                            sb.Append("<<<").Append(marker).Append('\n').Append(Neutralise(value, v.Name)).Append('\n').Append(marker).Append(">>>");
                        }
                        else sb.Append(value);
                        break;
                    }
                    case BlockNode { Each: false } b:
                    {
                        bool filled = resolved[b.Name] is string s ? s.Length > 0 : ((List<string>)resolved[b.Name]).Count > 0;
                        if (filled) Emit(b.Children, self, selfUntrusted);
                        break;
                    }
                    case BlockNode b:
                        foreach (string item in (List<string>)resolved[b.Name])
                            Emit(b.Children, item, Variables[b.Name].Untrusted ? b.Name : null);
                        break;
                }
            }
        }

        Emit(_body);
        return sb.ToString();
    }
}
