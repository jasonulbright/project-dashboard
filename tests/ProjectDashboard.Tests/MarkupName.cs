using System.Globalization;
using System.Xml;

namespace ProjectDashboard.Tests;

/// <summary>
/// Composes an <c>AutomationProperties.Name</c> the way WPF does, out of shipped markup: the
/// format string and the binding paths a page declares, resolved against a real model. A name
/// assembled in markup is not covered by anything the compiler checks, and the gap it leaves —
/// a separator printed around a value that turned out to be empty — is only visible once the
/// format and the properties are put together.
/// </summary>
internal static class MarkupName
{
    /// <summary>The document of a shipped view, loaded from the working tree.</summary>
    public static XmlDocument Markup(string relativePath)
    {
        var document = new XmlDocument();
        document.LoadXml(RepoSource.Read(relativePath));
        return document;
    }

    public static XmlElement Element(XmlDocument markup, string xpath, string relativePath)
    {
        var node = markup.SelectSingleNode(xpath) as XmlElement;
        Assert.True(node is not null, $"markup shape moved; nothing matched {xpath} in {relativePath}");
        return node!;
    }

    /// <summary>The name a <c>MultiBinding</c> produces for one model.</summary>
    public static string From(XmlElement multiBinding, object model)
    {
        var values = multiBinding.ChildNodes.OfType<XmlElement>()
            .Where(b => b.LocalName == "Binding")
            .Select(b => Resolve(model, b.GetAttribute("Path")))
            .ToArray();

        return string.Format(CultureInfo.InvariantCulture, Format(multiBinding.GetAttribute("StringFormat")), values);
    }

    /// <summary>
    /// The name a single <c>{Binding …}</c> expression produces for one model — the form a name
    /// takes once its composition has moved onto the model itself.
    /// </summary>
    public static string From(string bindingExpression, object model)
    {
        var (path, format) = ParseBinding(bindingExpression);
        var value = Resolve(model, path);
        return format.Length == 0
            ? value?.ToString() ?? ""
            : string.Format(CultureInfo.InvariantCulture, format, value);
    }

    /// <summary>"{}" opens a XAML string that would otherwise be read as a markup extension.</summary>
    private static string Format(string declared) =>
        declared.StartsWith("{}", StringComparison.Ordinal) ? declared[2..] : declared;

    private static (string Path, string Format) ParseBinding(string expression)
    {
        var trimmed = expression.Trim();
        Assert.StartsWith("{Binding", trimmed);
        var body = trimmed[8..^1];

        var path = "";
        var format = "";
        foreach (var argument in SplitArguments(body))
        {
            var separator = argument.IndexOf('=');
            var name = separator < 0 ? "" : argument[..separator].Trim();
            var value = (separator < 0 ? argument : argument[(separator + 1)..]).Trim().Trim('\'');

            if (name.Length == 0 || name == "Path") path = value;
            else if (name == "StringFormat") format = Format(value);
        }

        Assert.True(path.Length > 0, $"no binding path in {expression}");
        return (path, format);
    }

    /// <summary>Splits on commas outside quotes: a StringFormat may carry commas of its own.</summary>
    private static IEnumerable<string> SplitArguments(string body)
    {
        var start = 0;
        var quoted = false;
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '\'') quoted = !quoted;
            else if (body[i] == ',' && !quoted)
            {
                yield return body[start..i];
                start = i + 1;
            }
        }
        yield return body[start..];
    }

    public static object? Resolve(object? root, string path)
    {
        foreach (var step in path.Split('.'))
        {
            if (root is null) return null;
            var property = root.GetType().GetProperty(step);
            Assert.True(property is not null, $"{root.GetType().Name} has no {step}; a name in markup binds to it");
            root = property!.GetValue(root);
        }
        return root;
    }
}
