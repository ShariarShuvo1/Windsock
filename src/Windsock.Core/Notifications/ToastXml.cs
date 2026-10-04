using System.Text;
using System.Xml;

namespace Windsock.Core.Notifications;

/// <summary>
/// The markup Windows draws a notice from.
/// </summary>
/// <remarks>
/// Written with an XML writer rather than by joining strings. A title is plain
/// text from Windsock, but a message can carry a network name or a process name
/// that came from somewhere else, and one stray ampersand or angle bracket in
/// hand-built markup is a notice Windows refuses to show.
/// </remarks>
public static class ToastXml
{
    /// <summary>The longest title and message Windows is asked to show; more is cut with an ellipsis.</summary>
    public const int LongestTitle = 120;

    /// <summary>The longest message kept.</summary>
    public const int LongestMessage = 400;

    /// <summary>Builds the markup for one notice.</summary>
    /// <param name="notice">What to say.</param>
    /// <param name="sound">Whether the person wants notices to make a sound.</param>
    public static string Build(Notice notice, bool sound)
    {
        ArgumentNullException.ThrowIfNull(notice);

        StringBuilder text = new();
        XmlWriterSettings settings = new()
        {
            OmitXmlDeclaration = true,
            Indent = false,
        };

        using (XmlWriter writer = XmlWriter.Create(text, settings))
        {
            writer.WriteStartElement("toast");
            writer.WriteAttributeString("activationType", "foreground");
            writer.WriteAttributeString("launch", NoticeLaunch.Format(notice.Opens));

            writer.WriteStartElement("visual");
            writer.WriteStartElement("binding");
            writer.WriteAttributeString("template", "ToastGeneric");
            writer.WriteElementString("text", Clip(notice.Title, LongestTitle));
            writer.WriteElementString("text", Clip(notice.Message, LongestMessage));
            writer.WriteEndElement();
            writer.WriteEndElement();

            if (!sound || notice.Quiet)
            {
                writer.WriteStartElement("audio");
                writer.WriteAttributeString("silent", "true");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        return text.ToString();
    }

    /// <summary>
    /// Shortens text to a length, ending it with an ellipsis rather than
    /// mid-word where it can.
    /// </summary>
    public static string Clip(string? value, int longest)
    {
        string text = Clean(value);

        if (text.Length <= longest)
        {
            return text;
        }

        int cut = text.LastIndexOf(' ', Math.Max(0, longest - 1));
        if (cut < longest / 2)
        {
            cut = longest - 1;
        }

        return string.Concat(text.AsSpan(0, cut).TrimEnd(), "…");
    }

    /// <summary>
    /// Drops characters XML cannot carry at all. A control character in a
    /// process name would otherwise stop the writer outright.
    /// </summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        StringBuilder clean = new(value.Length);

        foreach (char each in value)
        {
            if (XmlConvert.IsXmlChar(each) && (each >= ' ' || each is '\n' or '\t'))
            {
                clean.Append(each);
            }
        }

        return clean.ToString().Trim();
    }
}
