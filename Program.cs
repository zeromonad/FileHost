using Microsoft.AspNetCore.StaticFiles;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// No-op when run interactively (dotnet run / debugging); wires up ServiceBase + Event Log
// logging when launched by the Service Control Manager.
builder.Host.UseWindowsService();

// Folder to serve files from — required, no default, fail fast if missing.
var fileRootSetting = builder.Configuration["FileHosting:FolderPath"]
    ?? throw new InvalidOperationException("FileHosting:FolderPath is not configured.");

// Relative paths resolve against the app's own directory (not the ambient working
// directory, which varies depending on how the process was launched — sc.exe, NSSM,
// or an interactive `dotnet run`) so "..\Files" reliably means "next to the exe".
var fileRoot = Path.IsPathRooted(fileRootSetting)
    ? Path.GetFullPath(fileRootSetting)
    : Path.GetFullPath(fileRootSetting, AppContext.BaseDirectory);
if (!Directory.Exists(fileRoot))
{
    throw new DirectoryNotFoundException($"FileHosting:FolderPath does not exist: {fileRoot}");
}

// Trailing separator so the StartsWith containment check below can't be fooled by a
// sibling folder that merely shares the same prefix (e.g. C:\Files vs C:\Files-Other).
var fileRootPrefix = fileRoot.EndsWith(Path.DirectorySeparatorChar)
    ? fileRoot
    : fileRoot + Path.DirectorySeparatorChar;

var app = builder.Build();

var contentTypeProvider = new FileExtensionContentTypeProvider();

app.Logger.LogInformation("Serving files from {FileRoot}", fileRoot);

// Belt-and-suspenders containment check in case of Combine/GetFullPath quirks.
bool IsWithinRoot(string fullPath) =>
    fullPath.StartsWith(fileRootPrefix, StringComparison.OrdinalIgnoreCase);

IResult ServeFile(string fullPath, string downloadName)
{
    if (!contentTypeProvider.TryGetContentType(fullPath, out var contentType))
    {
        contentType = "application/octet-stream";
    }

    // fileDownloadName is what makes ASP.NET Core set Content-Disposition: attachment.
    // enableRangeProcessing gives resumable/partial downloads for free.
    return Results.File(fullPath, contentType, fileDownloadName: downloadName, enableRangeProcessing: true);
}

// Parses "Authorization: Basic base64(user:pass)". The username is accepted but discarded —
// only the folder-name-as-password matters here. Splits on the first ':' only so passwords
// containing ':' still work.
bool TryGetBasicAuthPassword(HttpContext context, out string password)
{
    password = "";

    var raw = context.Request.Headers["Authorization"].ToString();
    if (string.IsNullOrEmpty(raw)
        || !AuthenticationHeaderValue.TryParse(raw, out var header)
        || !string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
        || string.IsNullOrEmpty(header.Parameter))
    {
        return false;
    }

    byte[] decodedBytes;
    try
    {
        decodedBytes = Convert.FromBase64String(header.Parameter);
    }
    catch (FormatException)
    {
        return false;
    }

    var decoded = Encoding.UTF8.GetString(decodedBytes);
    var separatorIndex = decoded.IndexOf(':');
    if (separatorIndex < 0)
    {
        return false;
    }

    password = decoded[(separatorIndex + 1)..]; // username portion is intentionally discarded
    return true;
}

// Timing-safe compare — this is now an actual password check, not just an obscure-URL scheme.
bool PasswordMatches(string suppliedPassword, string expectedPassword) =>
    CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(suppliedPassword),
        Encoding.UTF8.GetBytes(expectedPassword));

IResult ChallengeBasicAuth(HttpContext context)
{
    context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"FileHost\"";
    return Results.Unauthorized();
}

// Served for "/" and for any filename that doesn't resolve to a real file — a Windows 95
// "Run..." dialog pastiche instead of a blank 404. Markup/CSS/JS lives in index.html,
// compiled into the DLL as an embedded resource (see FileHost.csproj) so it ships with the
// assembly and can't go missing from a deployment. Two tokens in that file are substituted
// server-side:
//   %%LOGO_DATA_URI%%               — fixed asset, substituted once at startup below, into an
//                                      <img src> attribute. Safe there (unlike the token below)
//                                      because it's built from a local embedded file, never
//                                      from request input, and a base64 data URI can't contain
//                                      a quote or angle bracket to break out of the attribute.
//   %%ATTEMPTED_FILE_NAME_JSON%%    — varies per request, substituted in RenderNotFoundPage,
//                                      always inside a <script> block only — never into markup
//                                      or an attribute — using JsonSerializer.Serialize so it
//                                      arrives as a properly quoted/escaped JS literal.
string ReadEmbeddedNotFoundPageTemplate()
{
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html")
        ?? throw new InvalidOperationException("Embedded resource index.html not found.");
    using var reader = new StreamReader(stream, Encoding.UTF8);
    return reader.ReadToEnd();
}

string ReadEmbeddedLogoDataUri()
{
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png")
        ?? throw new InvalidOperationException("Embedded resource logo.png not found.");
    using var memoryStream = new MemoryStream();
    stream.CopyTo(memoryStream);
    return "data:image/png;base64," + Convert.ToBase64String(memoryStream.ToArray());
}

// Built once at startup — only the per-request token is left to fill in on each call below.
var notFoundHtmlTemplate = ReadEmbeddedNotFoundPageTemplate().Replace(
    "%%LOGO_DATA_URI%%",
    ReadEmbeddedLogoDataUri());

// attemptedFileName is null for "/" and other routes with nothing specific to look up (the page
// just shows the plain Run dialog); when set (a real filename lookup that came back empty), the
// page also pops the Win95 "Cannot find the file" error box on load. Never written into markup —
// only into a JS literal — so arbitrary characters in a mistyped filename can't inject anything.
IResult RenderNotFoundPage(string? attemptedFileName = null)
{
    var html = notFoundHtmlTemplate.Replace(
        "%%ATTEMPTED_FILE_NAME_JSON%%",
        attemptedFileName is null ? "null" : JsonSerializer.Serialize(attemptedFileName));
    return Results.Content(html, "text/html", Encoding.UTF8, statusCode: StatusCodes.Status404NotFound);
}

// Route pattern has one segment and no wildcard, so it only ever matches a single path
// component — "/a/b" won't hit this route at all. fileName itself can never contain a
// path separator (see the GetFileName check below), so subfolders can only ever be reached
// via the server-side scan below, never directly from the URL.
app.MapGet("/{fileName}", (string fileName, HttpContext context) =>
{
    // Path.GetFileName strips any directory component that slipped through; if it changes
    // the value (e.g. fileName was "..") treat it as invalid rather than trying to fix it up.
    var safeName = Path.GetFileName(fileName);
    if (string.IsNullOrWhiteSpace(safeName) || safeName != fileName)
    {
        return RenderNotFoundPage(fileName);
    }

    // 1. Flat files directly in fileRoot: unchanged, zero-auth behavior. Checked first, so a
    //    name that happens to exist both flat and in a subfolder is always served unprotected
    //    from the flat copy.
    var flatPath = Path.GetFullPath(Path.Combine(fileRoot, safeName));
    if (IsWithinRoot(flatPath) && File.Exists(flatPath))
    {
        return ServeFile(flatPath, safeName);
    }

    // 2. One level of subfolders: <fileRoot>\<password>\<file>. Scanned fresh every request —
    //    no caching/indexing, matching the "drop a file in, it's live immediately" behavior.
    foreach (var subfolder in Directory.GetDirectories(fileRoot))
    {
        var candidatePath = Path.GetFullPath(Path.Combine(subfolder, safeName));

        // Same containment check as the flat path above; GetDirectories should never escape
        // fileRoot, but this keeps the same defense-in-depth as the rest of this file.
        if (!IsWithinRoot(candidatePath) || !File.Exists(candidatePath))
        {
            continue;
        }

        // Path.GetFileName(subfolder) is never empty: GetDirectories only returns real,
        // existing directories.
        var subfolderName = Path.GetFileName(subfolder);

        if (!TryGetBasicAuthPassword(context, out var suppliedPassword)
            || !PasswordMatches(suppliedPassword, subfolderName))
        {
            return ChallengeBasicAuth(context);
        }

        return ServeFile(candidatePath, safeName);

        // If the same filename exists in more than one password subfolder, whichever
        // subfolder Directory.GetDirectories() yields first wins (this loop returns on the
        // first match, whether auth succeeds or fails against it). That enumeration order is
        // OS/filesystem-dependent, not guaranteed alphabetical — don't duplicate a filename
        // across subfolders if this matters to you.
    }

    return RenderNotFoundPage(safeName);
});

// Simple liveness check — hits "/status" which the filename route can't match on its own (empty segment).
app.MapGet("/status", () => Results.Ok("File host is running."));

// Catches everything no other endpoint matched: bare "/" (zero segments) and any multi-segment
// path (e.g. "/a/b"), neither of which "/{fileName}" can match. Endpoint routing always ranks
// literal routes ("/status") and parameterized routes ("/{fileName}") above a fallback, and a
// fallback is only ever tried last regardless of where it's registered — so this can't shadow
// either of those.
app.MapFallback(() => RenderNotFoundPage());

app.Run();
