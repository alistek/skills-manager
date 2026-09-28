using System.Text;
using System.Text.Json.Nodes;
using Devolutions.AgentSkills;

namespace Skills.Tests;

/// Notion sources through the public API, with a fake Notion CLI and fake
/// skill downloads, in a sandbox home.
public sealed class NotionSourceTests : IDisposable
{
    private const string DatabaseId = "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d";
    private const string DataSourceId = "2b3c4d5e-6f7a-4b8c-9d0e-1f2a3b4c5d6e";
    private const string ProofreaderId = "3c4d5e6f-7a8b-4c9d-8e0f-2a3b4c5d6e7f";
    private const string ToneId = "4d5e6f7a-8b9c-4d0e-9f1a-3b4c5d6e7f80";
    private const string TrashedId = "5e6f7a8b-9c0d-4e1f-8a2b-4c5d6e7f8091";
    private const string DatabaseUrl = "https://app.notion.com/p/1a2b3c4d5e6f4a7b8c9d0e1f2a3b4c5d?v=6f7a8b9c0d1e4f2a8b3c4d5e6f7a8b9c&source=copy_link";
    private const string CanonicalDatabase = "https://app.notion.com/p/1a2b3c4d5e6f4a7b8c9d0e1f2a3b4c5d";

    private readonly TempDir _root = new();
    private readonly string _home;
    private readonly FakeNotion _notion = new();

    public NotionSourceTests()
    {
        _home = _root.Join("home");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(NodePath.Join(_home, ".claude"));
        _notion.Pages[ProofreaderId] = ("Proofreader / copyeditor", "Proofreads marketing copy.", "v1");
        _notion.Pages[ToneId] = ("Tone reviewer", "Reviews brand voice.", "v1");
    }

    public void Dispose() => _root.Dispose();

    private SkillsManager Manager() => new(new SkillsManagerOptions
    {
        HomeDirectory = _home,
        ProjectDirectory = _root.Path,
        NotionRunner = _notion.Run,
        NotionDownloader = url => _notion.Download(url, _root),
    });

    private JsonNode? LockEntry(string name) =>
        Json.Parse(File.ReadAllText(NodePath.Join(_home, ".agents", ".skill-lock.json")))?["skills"]?[name];

    /// Answers `ntn` the way the Notion CLI 0.23 does, for one skills database.
    private sealed class FakeNotion
    {
        public readonly Dictionary<string, (string Title, string Description, string Version)> Pages = [];
        public readonly List<string> Calls = [];
        public bool SignedIn = true;

        private static ProcOutput Out(int status, string stdout, string stderr = "") =>
            new(status, Encoding.UTF8.GetBytes(stdout), Encoding.UTF8.GetBytes(stderr));

        // As the CLI prints API errors: the API's message, no code
        private static ProcOutput Failed(string message) =>
            Out(1, "", $"error: Public API request failed: {message}\n");

        private static ProcOutput NotFound(string what) =>
            Failed($"Could not find {what} with ID: 0000. Make sure the relevant pages and databases are shared with your integration.");

        private static JsonObject Page(string id, string title, string description) => new()
        {
            ["object"] = "page",
            ["id"] = id,
            ["in_trash"] = false,
            ["parent"] = new JsonObject { ["type"] = "data_source_id", ["data_source_id"] = DataSourceId, ["database_id"] = DatabaseId },
            ["properties"] = new JsonObject
            {
                ["Name"] = new JsonObject { ["type"] = "title", ["title"] = new JsonArray(new JsonObject { ["plain_text"] = title }) },
                ["Description"] = new JsonObject { ["type"] = "rich_text", ["rich_text"] = new JsonArray(new JsonObject { ["plain_text"] = description }) },
            },
        };

        public ProcOutput Run(IReadOnlyList<string> args)
        {
            lock (Calls) Calls.Add(string.Join(' ', args));
            if (args is ["--version"]) return Out(0, "ntn 0.23.10\n");
            if (!SignedIn) return Out(4, "", "error: No workspace selected.\n  hint: Run `ntn login` first, or set NOTION_WORKSPACE_ID.\n");
            if (args is ["whoami", ..]) return Out(0, """{"object":"user","type":"bot","bot":{"workspace_name":"Contoso"}}""");
            if (args is not ["api", var path, ..]) return Out(2, "", "error: unexpected command");

            if (path == $"v1/databases/{DatabaseId}")
                return Out(0, new JsonObject { ["object"] = "database", ["id"] = DatabaseId, ["data_sources"] = new JsonArray(new JsonObject { ["id"] = DataSourceId }) }.ToJsonString());
            if (path.StartsWith("v1/databases/", StringComparison.Ordinal)) return Failed("Provided ID 0000 is a page, not a database. Use the retrieve page API instead.");
            if (path == $"v1/data_sources/{DataSourceId}/query")
            {
                var results = new JsonArray();
                foreach (var (id, p) in Pages) results.Add(Page(id, p.Title, p.Description));
                var trashed = Page(TrashedId, "Old reviewer", "Gone");
                trashed["in_trash"] = true;
                results.Add(trashed);
                return Out(0, new JsonObject { ["object"] = "list", ["results"] = results, ["has_more"] = false, ["next_cursor"] = null }.ToJsonString());
            }
            if (path.StartsWith("v1/pages/", StringComparison.Ordinal) && Pages.TryGetValue(path[9..], out var page))
                return Out(0, Page(path[9..], page.Title, page.Description).ToJsonString());
            if (path.StartsWith("v1/ai/skills/", StringComparison.Ordinal))
            {
                var id = path[13..];
                if (!Pages.TryGetValue(id, out var skill)) return Failed("No skill with that ID is shared with the connection, or the ID no longer exists.");
                return Out(0, new JsonObject { ["id"] = id, ["version_id"] = skill.Version, ["url"] = $"https://files.example/{id}/{skill.Version}/skill.tar.gz" }.ToJsonString());
            }
            return NotFound("page");
        }

        public DownloadedSource Download(string url, TempDir root)
        {
            var id = url.Split('/')[3];
            var (title, description, version) = Pages[id];
            var name = NotionFeed.Slug(title);
            var temp = root.Join("downloads", Guid.NewGuid().ToString("N"));
            var dir = NodePath.Join(temp, "extract", name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(NodePath.Join(dir, "SKILL.md"), $"---\nname: |-\n  {name}\ndescription: |-\n  {description}\nnotion_page_id: {id}\n---\n\n# {title} ({version})\n");
            return new DownloadedSource(dir, temp, DownloadKind.Archive);
        }
    }

    [Theory]
    [InlineData("Proofreader / copyeditor", "proofreader-copyeditor")]
    [InlineData("Cliché Finder", "clich-finder")]
    [InlineData("What's New release notes", "whats-new-release-notes")]
    [InlineData("What’s New blog post", "whats-new-blog-post")]
    [InlineData("QA - Release Checklist", "qa-release-checklist")]
    [InlineData("  --Tone reviewer!  ", "tone-reviewer")]
    public void SlugsMatchTheNamesNotionGivesSkills(string title, string name) => Assert.Equal(name, NotionFeed.Slug(title));

    [Theory]
    [InlineData(DatabaseUrl, DatabaseId)]
    [InlineData("https://www.notion.so/contoso/Team-skills-1a2b3c4d5e6f4a7b8c9d0e1f2a3b4c5d?v=1", DatabaseId)]
    [InlineData("https://notion.so/1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d", DatabaseId)]
    [InlineData("https://contoso.notion.site/Tone-reviewer-4d5e6f7a8b9c4d0e9f1a3b4c5d6e7f80", ToneId)]
    [InlineData("http://app.notion.com/p/1a2b3c4d5e6f4a7b8c9d0e1f2a3b4c5d", null)]
    [InlineData("https://user@app.notion.com/p/1a2b3c4d5e6f4a7b8c9d0e1f2a3b4c5d", null)]
    [InlineData("https://notion.example.com/p/1a2b3c4d5e6f4a7b8c9d0e1f2a3b4c5d", null)]
    [InlineData("https://app.notion.com/p/not-an-id", null)]
    public void NotionAddressesNameAPageOrDatabase(string address, string? id) => Assert.Equal(id, NotionFeed.ParseId(address));

    [Fact]
    public void ListsTheSkillsOfADatabaseWithoutDownloadingThem()
    {
        var available = Manager().GetAvailableSkills(DatabaseUrl);

        Assert.Equal(["proofreader-copyeditor", "tone-reviewer"], available.Select(s => s.Name).Order());
        Assert.Equal("Reviews brand voice.", available.Single(s => s.Name == "tone-reviewer").Description);
        Assert.DoesNotContain(_notion.Calls, c => c.Contains("v1/ai/skills", StringComparison.Ordinal));
        Assert.All(_notion.Calls, c => Assert.EndsWith("--notion-version 2026-03-11", c));
    }

    [Fact]
    public void InstallsASkillAndTracksItsNotionVersion()
    {
        var m = Manager();
        // A page title works as well as the skill name
        var result = m.Install(new SkillInstallRequest { Source = DatabaseUrl, Skills = ["Proofreader / copyeditor"], Agents = ["claude-code"] });

        var outcome = Assert.Single(result.Skills);
        Assert.True(result.Succeeded, outcome.Error);
        Assert.Equal("proofreader-copyeditor", outcome.Name);
        Assert.Equal("v1", outcome.Hash);
        Assert.True(File.Exists(NodePath.Join(_home, ".claude", "skills", "proofreader-copyeditor", "SKILL.md")));
        // Only the requested skill was downloaded
        Assert.DoesNotContain(_notion.Calls, c => c.Contains(ToneId, StringComparison.Ordinal));

        var entry = LockEntry("proofreader-copyeditor");
        Assert.Equal(CanonicalDatabase, Json.Str(entry, "source"));
        Assert.Equal("notion", Json.Str(entry, "sourceType"));
        Assert.Equal("https://app.notion.com/p/3c4d5e6f7a8b4c9d8e0f2a3b4c5d6e7f", Json.Str(entry, "sourceUrl"));
        Assert.Equal(ProofreaderId, Json.Str(entry, "notionPageId"));
        Assert.Equal("v1", Json.Str(entry, "notionVersionId"));
        // What keeps the reference CLI's update checks away from it
        Assert.Equal("", Json.Str(entry, "skillFolderHash"));
        Assert.Null(Json.Get(entry, "skillPath"));

        var installed = Assert.Single(m.GetInstalledSkills(SkillScope.Global));
        Assert.Equal("notion", installed.SourceType);
        Assert.Equal(CanonicalDatabase, installed.Source);
        Assert.Equal("v1", installed.Hash);
        Assert.True(installed.IsTracked);
    }

    [Fact]
    public void FindsAndAppliesAnUpdateWhenTheSkillChanges()
    {
        var m = Manager();
        Assert.True(m.Install(new SkillInstallRequest { Source = DatabaseUrl, Skills = ["tone-reviewer"], Agents = ["claude-code"] }).Succeeded);
        Assert.Empty(m.CheckForUpdates(SkillScope.Global).Updates);

        _notion.Pages[ToneId] = ("Tone reviewer", "Reviews brand voice.", "v2");
        var update = Assert.Single(m.CheckForUpdates(SkillScope.Global).Updates);
        Assert.Equal("tone-reviewer", update.Name);
        Assert.Equal(CanonicalDatabase, update.Source);
        Assert.Equal("v1", update.CurrentHash);
        Assert.Equal("v2", update.LatestHash);

        var applied = m.Update([update]);
        Assert.True(applied.Succeeded, applied.Skills.FirstOrDefault()?.Error);
        var entry = LockEntry("tone-reviewer");
        Assert.Equal("v2", Json.Str(entry, "notionVersionId"));
        // Updated from the page, still recorded under its database
        Assert.Equal(CanonicalDatabase, Json.Str(entry, "source"));
        Assert.Contains("(v2)", File.ReadAllText(NodePath.Join(_home, ".claude", "skills", "tone-reviewer", "SKILL.md")));
        Assert.Empty(m.CheckForUpdates(SkillScope.Global).Updates);
    }

    [Fact]
    public void ReportsSkillsDeletedInNotion()
    {
        var m = Manager();
        Assert.True(m.Install(new SkillInstallRequest { Source = DatabaseUrl, Skills = ["tone-reviewer"], Agents = ["claude-code"] }).Succeeded);
        _notion.Pages.Remove(ToneId);

        var check = m.CheckForUpdates(SkillScope.Global);
        Assert.Empty(check.Updates);
        Assert.Equal("Deleted upstream", Assert.Single(check.Unchecked).Reason);
    }

    [Fact]
    public void SignedOutChecksStopAtTheFirstSkill()
    {
        var m = Manager();
        Assert.True(m.Install(new SkillInstallRequest { Source = DatabaseUrl, Agents = ["claude-code"] }).Succeeded);
        _notion.SignedIn = false;
        _notion.Calls.Clear();

        var check = m.CheckForUpdates(SkillScope.Global);
        Assert.Equal(2, check.Unchecked.Count);
        Assert.All(check.Unchecked, u => Assert.Equal("Sign in to Notion to use Notion sources.", u.Reason));
        Assert.Single(_notion.Calls);
    }

    [Fact]
    public void SignedOutListingSaysSo()
    {
        _notion.SignedIn = false;
        var e = Assert.Throws<SkillsException>(() => Manager().GetAvailableSkills(DatabaseUrl));
        Assert.Equal(SkillsFailure.NotionSignedOut, e.Failure);
    }

    [Fact]
    public void AMissingCliIsReported()
    {
        var m = new SkillsManager(new SkillsManagerOptions { HomeDirectory = _home, NotionCliPath = _root.Join("no-such-ntn.exe") });
        Assert.Equal(NotionCliState.NotInstalled, m.GetNotionStatus().State);
        var e = Assert.Throws<SkillsException>(() => m.GetAvailableSkills(DatabaseUrl));
        Assert.Equal(SkillsFailure.NotionCliMissing, e.Failure);
    }

    [Fact]
    public void StatusReportsTheWorkspaceOrTheNeedToSignIn()
    {
        var status = Manager().GetNotionStatus();
        Assert.Equal(new NotionStatus(NotionCliState.SignedIn, "0.23.10", "Contoso", null), status);

        _notion.SignedIn = false;
        Assert.Equal(NotionCliState.SignedOut, Manager().GetNotionStatus().State);
    }

    [Fact]
    public void SignInReadsTheAddressAndCodeTheCliPrints()
    {
        const string printed = "Open this URL in your browser to log in:\n\n  https://app.notion.com/workers/cli-login?verificationCode=K7Q-2MX\n\n"
            + "Confirm that this verification code matches what you see in the browser:\n\n  K7Q-2MX\n\nThen run this command to complete login:\n\n  ntn login poll\n";
        var signIn = NotionCli.ParseSignIn(printed);
        Assert.NotNull(signIn);
        Assert.Equal("https://app.notion.com/workers/cli-login?verificationCode=K7Q-2MX", signIn.Url.AbsoluteUri);
        Assert.Equal("K7Q-2MX", signIn.VerificationCode);

        // Only a Notion page is opened in the user's browser
        Assert.Null(NotionCli.ParseSignIn(printed.Replace("app.notion.com", "notion.example.com")));
    }

    [Theory]
    [InlineData(4, "error: No workspace selected.\n  hint: Run `ntn login` first, or set NOTION_WORKSPACE_ID.\n", "SignedOut")]
    [InlineData(4, "error: Public API request failed: API token is invalid.\n  hint: Set NOTION_API_TOKEN, or run `ntn login` to reuse a saved workspace token.\n", "SignedOut")]
    [InlineData(1, "error: Public API request failed: Could not find database with ID: 1a2b3c4d. Make sure the relevant pages and databases are shared with your integration.\n", "NotFound")]
    [InlineData(1, "error: Public API request failed: No skill with that ID is shared with the connection, or the ID no longer exists.\n", "NotFound")]
    [InlineData(1, "error: Public API request failed: Provided ID 3c4d5e6f is a page, not a database.\n", "Invalid")]
    [InlineData(1, "error: Public API request failed: Insufficient permissions for this endpoint.\n", "Forbidden")]
    [InlineData(1, "error: Public API request failed: You have been rate limited. Please try again in a few minutes.\n", "Other")]
    public void FailuresAreReadFromWhatTheCliPrints(int exitCode, string stderr, string expected)
    {
        var failure = NotionCli.Failure(new ProcOutput(exitCode, [], Encoding.UTF8.GetBytes(stderr)), "the Notion database");
        Assert.Equal(expected, failure.Failure.ToString());
        Assert.DoesNotContain("Public API request failed", failure.Message);
    }

    [Fact]
    public void OnWindowsTheCliGetsACacheDirectoryWhenItCannotFindOne()
    {
        const string local = @"C:\Users\someone\AppData\Local";
        static string? None(string _) => null;

        Assert.Equal(local, NotionCli.CacheDirectory(true, None, local));
        // What ntn would use itself
        Assert.Null(NotionCli.CacheDirectory(true, name => name == "HOME" ? @"C:\Users\someone" : null, local));
        Assert.Null(NotionCli.CacheDirectory(true, name => name == "NOTION_HOME" ? @"D:\notion" : null, local));
        Assert.Null(NotionCli.CacheDirectory(true, name => name == "XDG_CACHE_HOME" ? @"D:\cache" : null, local));
        Assert.Null(NotionCli.CacheDirectory(false, None, "/home/someone/.local/share"));
    }

    [Fact]
    public void NotionPacksStayWithTheCli()
    {
        var e = Assert.Throws<SkillsException>(() => Manager().Install(new SkillInstallRequest { Source = "notion" }));
        Assert.Contains("skills add notion", e.Message);
    }
}
