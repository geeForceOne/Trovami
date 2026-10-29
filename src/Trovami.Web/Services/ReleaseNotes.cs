using Trovami.Core;
using Markdig;

namespace Trovami.Web.Services;

// The release notes page's content: the repository's CHANGELOG.md, embedded into the app at build
// time (see the .csproj) so a running instance shows the notes up to its own version, with no
// network access. Rendered once; raw HTML in the file is escaped, not passed through.
public static class ReleaseNotes
{
    private static readonly Lazy<string> RenderedHtml = new(Render);

    // Written in CHANGELOG.md in place of a version number; the page shows the running version and
    // its build date there instead, e.g. "1.3.0 - 2026-10-25".
    private const string CurrentVersionHeading = "## Current version";

    public static string Html => RenderedHtml.Value;

    private static string RunningHeading() =>
        "## " + AppInfo.Version + (AppInfo.BuildDate is { } date ? " - " + date : "");

    private static string Render()
    {
        using var stream = typeof(ReleaseNotes).Assembly.GetManifestResourceStream("CHANGELOG.md");
        if (stream is null)
            return "<p>No release notes are included in this build.</p>";
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd().Replace(CurrentVersionHeading, RunningHeading());
        // The file's own "# Release notes" title; the page has its heading already.
        if (text.StartsWith("# "))
            text = text[(text.IndexOf('\n') + 1)..];
        var pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();
        return Markdown.ToHtml(text, pipeline);
    }
}
