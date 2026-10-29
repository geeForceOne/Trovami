using System.Net;

namespace Trovami.Core.Email;

// The frame every email shares: a white sheet with the name in a dark teal band. Inline styles only,
// since mail clients drop stylesheets; always light, since many clients force it anyway.
public static class EmailLayout
{
    public const string Ink = "#17212c";
    public const string Pencil = "#5b6776";
    public const string Rule = "#d9dfe6";
    public const string Brand = "#123f3a";
    public const string Accent = "#e9a23b";

    public static string Html(string title, string body, int maxWidth = 560) =>
        $"<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>{Enc(title)}</title></head>" +
        $"<body style=\"margin:0;padding:24px 12px;background:#eef1f5;font-family:'IBM Plex Sans',Segoe UI,Helvetica,Arial,sans-serif;color:{Ink};font-size:15px;line-height:1.5\">" +
        $"<div style=\"max-width:{maxWidth}px;margin:0 auto;background:#ffffff;border-radius:8px;overflow:hidden;border:1px solid {Rule}\">" +
        $"<div style=\"background:{Brand};color:#ffffff;padding:14px 24px;font-weight:600;font-size:16px;letter-spacing:.01em\">" +
        $"<span style=\"display:inline-block;width:10px;height:10px;border-radius:50%;background:{Accent};margin-right:8px\"></span>{Enc(Core.AppInfo.Name)}</div>" +
        $"<div style=\"padding:24px\">{body}</div></div></body></html>";

    public static string Paragraph(string text, bool muted = false, int marginBottom = 16) =>
        $"<p style=\"margin:0 0 {marginBottom}px;{(muted ? $"font-size:13px;color:{Pencil}" : "")}\">{Enc(text)}</p>";

    public static string Button(string label, string link) =>
        $"<p style=\"margin:0 0 24px\"><a href=\"{Enc(link)}\" style=\"display:inline-block;background:{Brand};color:#ffffff;" +
        $"text-decoration:none;padding:12px 20px;border-radius:6px;font-weight:600\">{Enc(label)}</a></p>";

    public static string Enc(string s) => WebUtility.HtmlEncode(s);
}
