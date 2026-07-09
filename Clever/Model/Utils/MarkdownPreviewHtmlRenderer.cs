using Markdig;
using System.Net;

namespace Clever.Model.Utils
{
    internal static class MarkdownPreviewHtmlRenderer
    {
        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();

        internal static string RenderHtml(string markdown, string title)
        {
            var body = Markdown.ToHtml(markdown ?? "", Pipeline);
            return @"<!doctype html>
<html>
<head>
<meta http-equiv=""X-UA-Compatible"" content=""IE=edge"" />
<meta charset=""utf-8"" />
<title>" + WebUtility.HtmlEncode(title ?? "Markdown") + @"</title>
<style>
html, body { margin:0; padding:0; background:#ffffff; color:#273043; font-family:'Segoe UI', Arial, sans-serif; font-size:17px; line-height:1.62; }
body { padding:26px 36px 48px 36px; }
h1, h2, h3, h4, h5, h6 { color:#1f2a3d; line-height:1.22; margin:1.25em 0 .55em; font-weight:700; }
h1 { font-size:36px; border-bottom:1px solid #d8deea; padding-bottom:8px; }
h2 { font-size:28px; border-bottom:1px solid #e5e9f1; padding-bottom:6px; }
h3 { font-size:23px; }
p { margin:.7em 0; }
a { color:#0074d9; text-decoration:none; }
a:hover { text-decoration:underline; }
blockquote { margin:14px 0; padding:8px 16px; border-left:4px solid #9fb2d0; color:#5a6475; background:#f5f7fb; }
pre { margin:16px 0; padding:16px 18px; overflow:auto; background:#f1f4f9; border:1px solid #dce3ef; border-radius:4px; font-size:15.5px; }
code { font-family:Consolas, 'Courier New', monospace; background:#f1f4f9; padding:2px 5px; border-radius:3px; font-size:.93em; }
pre code { padding:0; background:transparent; }
table { border-collapse:collapse; margin:14px 0; width:auto; max-width:100%; }
th, td { border:1px solid #d8deea; padding:7px 10px; vertical-align:top; }
th { background:#eef3fb; color:#1f2a3d; font-weight:700; }
tr:nth-child(even) td { background:#fbfcff; }
hr { border:0; border-top:1px solid #d8deea; margin:24px 0; }
img { max-width:100%; }
ul, ol { padding-left:28px; }
</style>
</head>
<body>" + body + @"</body>
</html>";
        }
    }
}
