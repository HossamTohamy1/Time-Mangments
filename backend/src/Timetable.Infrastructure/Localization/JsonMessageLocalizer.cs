using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Timetable.Application.Abstractions;
using Timetable.Domain.Constraints;

namespace Timetable.Infrastructure.Localization;

/// <summary>
/// Localizes stable codes (errors, constraint violations, notifications, job progress) using embedded
/// messages-{lang}.json files with {param} placeholders. Bilingual parameter values (BiText) are rendered
/// in the same language.
/// </summary>
public sealed partial class JsonMessageLocalizer : IMessageLocalizer
{
    private readonly FrozenDictionary<string, FrozenDictionary<string, string>> _messages;

    public JsonMessageLocalizer()
    {
        var asm = typeof(JsonMessageLocalizer).Assembly;
        var dict = new Dictionary<string, FrozenDictionary<string, string>>();
        foreach (var lang in new[] { "en", "ar" })
        {
            var name = asm.GetManifestResourceNames().First(n => n.EndsWith($"messages-{lang}.json", StringComparison.Ordinal));
            using var s = asm.GetManifestResourceStream(name)!;
            dict[lang] = (JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? []).ToFrozenDictionary();
        }
        _messages = dict.ToFrozenDictionary();
    }

    public IReadOnlyCollection<string> Keys(string lang) => _messages[lang].Keys;

    public string Localize(string code, IReadOnlyDictionary<string, object?>? parameters = null, string? language = null)
    {
        var lang = Normalize(language ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        var template = _messages[lang].TryGetValue(code, out var t) ? t
            : _messages["en"].TryGetValue(code, out var en) ? en
            : code;
        if (parameters is null || parameters.Count == 0) return template;
        return Placeholder().Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            return parameters.TryGetValue(key, out var v) ? Render(v, lang) : m.Value;
        });
    }

    public static string Render(object? value, string lang) => value switch
    {
        null => string.Empty,
        BiText b => b.For(lang),
        decimal d => d.ToString("0.##", CultureInfo.InvariantCulture),
        double d => d.ToString("0.##", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Normalize(string lang) => lang.StartsWith("ar", StringComparison.OrdinalIgnoreCase) ? "ar" : "en";

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();
}
