using System.Text;

namespace ImmersiveReading.App.Appearance;

/// <summary>
/// The user's own stylesheet (Settings -> Custom CSS): one file that is applied on top of the app's styles and inside the reader.
/// The file is plain CSS the user owns; "enabled" can be switched off without losing it, which is also the way out of a stylesheet
/// that made the interface unusable (a keyboard shortcut in the app calls the same switch).
/// </summary>
public sealed class UserCssStore(string directory)
{
    public const int MaxBytes = 512 * 1024;

    private readonly Lock _lock = new();
    private string CssPath => Path.Combine(directory, "user.css");
    private string DisabledMarker => Path.Combine(directory, "user.css.disabled");

    public sealed record State(string Css, bool Enabled);

    public State Get()
    {
        lock (_lock)
        {
            string css = File.Exists(CssPath) ? File.ReadAllText(CssPath) : "";
            return new State(css, !File.Exists(DisabledMarker));
        }
    }

    public State Set(string css, bool enabled)
    {
        if (Encoding.UTF8.GetByteCount(css) > MaxBytes) throw new ArgumentException($"The stylesheet is larger than {MaxBytes / 1024} KB.");
        lock (_lock)
        {
            Directory.CreateDirectory(directory);
            string temp = CssPath + ".tmp";
            File.WriteAllText(temp, css, new UTF8Encoding(false));
            File.Move(temp, CssPath, overwrite: true);
            ApplyEnabled(enabled);
            return new State(css, enabled);
        }
    }

    public State SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(directory);
            ApplyEnabled(enabled);
        }
        return Get();
    }

    private void ApplyEnabled(bool enabled)
    {
        if (enabled) { if (File.Exists(DisabledMarker)) File.Delete(DisabledMarker); }
        else File.WriteAllText(DisabledMarker, "");
    }
}
