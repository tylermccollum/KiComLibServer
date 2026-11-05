using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KiComLibBackend.Data;
using KiComLibBackend.Models;
using KiComLibBackend.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace KiComLibBackend.Tests;

[Collection("kicomlib-db")]
public class ProjectGitTests
{
    private readonly CustomWebApplicationFactory _factory;

    public ProjectGitTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private bool Database => _factory.DatabaseAvailable;

    [Fact]
    public async Task FreshDatabase_HasCurrentSchemaVersion()
    {
        if (!Database) return;

        await using NpgsqlConnection connection = await OpenAsync();
        int version = await ScalarIntAsync(connection, "SELECT COALESCE(MAX(version), 0) FROM public.schema_version");
        Assert.Equal(DatabaseInitializer.SchemaVersion, version);
        Assert.Equal(DatabaseInitializer.SchemaVersion, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM public.schema_version"));
        int hosts = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM public.git_host WHERE is_system");
        Assert.Equal(2, hosts);
        Assert.True(await TableExistsAsync(connection, "project_job"));
        Assert.True(await TableExistsAsync(connection, "git_repository"));
        Assert.True(await TableExistsAsync(connection, "directory_connection"));
        Assert.True(await TableExistsAsync(connection, "warehouse_item"));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'warehouse_item' AND column_name = 'image_hash'"));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'part' AND column_name = 'datasheet_ids'"));
        Assert.Equal(1, await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'app_user' AND column_name = 'source'"));
    }

    [Fact]
    public async Task Anonymous_ProjectIs401_PartIs200_UnsignedWebhookIs401()
    {
        if (!Database) return;

        HttpClient anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/project")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/part")).StatusCode);

        HttpClient admin = await AdminClientAsync();
        int repositoryId = await CreateRepositoryAsync(admin, "https://gitlab.example.test/group/unsigned.git", "gitlab", "https://gitlab.example.test");
        Guid uuid = await RepositoryUuidAsync(repositoryId);
        HttpResponseMessage unsigned = await anonymous.PostAsync("/api/hooks/git/" + uuid, new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        HttpResponseMessage unknown = await anonymous.PostAsync("/api/hooks/git/" + Guid.NewGuid(), new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task ProjectAccess_GroupsAdminAndReadOnlyToken()
    {
        if (!Database) return;

        HttpClient admin = await AdminClientAsync();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        int readers = await CreateGroupAsync(admin, "readers-" + suffix, canWrite: false);
        int writers = await CreateGroupAsync(admin, "writers-" + suffix, canWrite: true);
        string readerName = "reader-" + suffix;
        string writerName = "writer-" + suffix;
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/user", new { username = readerName, password = "reader-password", groupIds = new[] { readers } })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/user", new { username = writerName, password = "writer-password", groupIds = new[] { writers } })).StatusCode);

        int repositoryId = await CreateRepositoryAsync(admin, "https://gitlab.example.test/group/access-" + suffix + ".git", "gitlab", "https://gitlab.example.test");
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/project", new
        {
            repositoryId,
            projectFile = "board/board.kicad_pro",
            name = "Access " + suffix,
            status = "development"
        });
        string createdBody = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, createdBody);
        int projectId = JsonDocument.Parse(createdBody).RootElement.GetProperty("id").GetInt32();

        HttpClient reader = await LoginAsync(readerName, "reader-password");
        HttpClient writer = await LoginAsync(writerName, "writer-password");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/project/" + projectId)).StatusCode);

        HttpResponseMessage access = await admin.PutAsJsonAsync("/api/project/" + projectId + "/access", new object[]
        {
            new { groupId = readers, canWrite = true },
            new { groupId = writers, canWrite = true }
        });
        Assert.True(access.IsSuccessStatusCode, await access.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/project/" + projectId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync("/api/project/" + projectId, new { name = "nope" })).StatusCode);

        HttpResponseMessage renamed = await writer.PutAsJsonAsync("/api/project/" + projectId, new { name = "Renamed " + suffix });
        Assert.True(renamed.IsSuccessStatusCode, await renamed.Content.ReadAsStringAsync());

        HttpResponseMessage tokenResponse = await writer.PostAsJsonAsync("/api/token", new { name = "readonly-" + suffix, canWrite = false });
        string tokenBody = await tokenResponse.Content.ReadAsStringAsync();
        Assert.True(tokenResponse.StatusCode == HttpStatusCode.Created, tokenBody);
        string token = JsonDocument.Parse(tokenBody).RootElement.GetProperty("token").GetString()!;
        HttpClient tokenClient = _factory.CreateClient();
        tokenClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await tokenClient.GetAsync("/api/project/" + projectId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tokenClient.PutAsJsonAsync("/api/project/" + projectId, new { name = "token-write" })).StatusCode);

        JsonElement detail = JsonDocument.Parse(await (await admin.GetAsync("/api/project/" + projectId)).Content.ReadAsStringAsync()).RootElement;
        Assert.True(detail.GetProperty("permissions").GetProperty("canAdmin").GetBoolean());
        Assert.True(detail.GetProperty("permissions").GetProperty("canWrite").GetBoolean());

        JsonElement users = JsonDocument.Parse(await (await admin.GetAsync("/api/user")).Content.ReadAsStringAsync()).RootElement;
        int adminId = users.EnumerateArray().First(user => user.GetProperty("username").GetString() == "admin").GetProperty("id").GetInt32();
        HttpResponseMessage owned = await admin.PutAsJsonAsync("/api/project/" + projectId, new { ownerUserId = adminId });
        string ownedBody = await owned.Content.ReadAsStringAsync();
        Assert.True(owned.IsSuccessStatusCode, ownedBody);
        JsonElement ownedDetail = JsonDocument.Parse(ownedBody).RootElement;
        Assert.Equal("admin", ownedDetail.GetProperty("ownerName").GetString());
        Assert.Equal(0, ownedDetail.GetProperty("counts").GetProperty("revisions").GetInt32());
        Assert.Equal(0, ownedDetail.GetProperty("counts").GetProperty("components").GetInt32());

        JsonElement listed = JsonDocument.Parse(await (await admin.GetAsync("/api/project")).Content.ReadAsStringAsync()).RootElement;
        JsonElement row = listed.EnumerateArray().First(item => item.GetProperty("id").GetInt32() == projectId);
        Assert.Equal("admin", row.GetProperty("ownerName").GetString());
        Assert.Equal(0, row.GetProperty("counts").GetProperty("revisions").GetInt32());
        Assert.Equal(0, row.GetProperty("counts").GetProperty("components").GetInt32());
    }

    [Fact]
    public async Task Secrets_AreEncryptedAndAbsentFromApi_AndGitCommandOmitsToken()
    {
        if (!Database) return;

        HttpClient admin = await AdminClientAsync();
        JsonElement hosts = JsonDocument.Parse(await (await admin.GetAsync("/api/git-host")).Content.ReadAsStringAsync()).RootElement;
        int hostId = hosts.EnumerateArray().First(host => host.GetProperty("name").GetString() == "GitLab.com").GetProperty("id").GetInt32();
        const string secret = "glpat-test-secret-a1b2";
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/git-credential", new
        {
            hostId,
            name = "secret-" + Guid.NewGuid().ToString("N")[..8],
            kind = "https_token",
            username = "oauth2",
            secret,
            expiresAt = DateTime.UtcNow.AddDays(7)
        });
        string body = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, body);
        Assert.DoesNotContain(secret, body);
        Assert.Contains("glpat-", body);
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("expiresSoon").GetBoolean());

        string list = await (await admin.GetAsync("/api/git-credential")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, list);

        int id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt32();
        await using NpgsqlConnection connection = await OpenAsync();
        await using NpgsqlCommand command = new("SELECT secret_protected FROM public.git_credential WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        string stored = (string)(await command.ExecuteScalarAsync())!;
        Assert.DoesNotContain(secret, stored);

        SecretProtector protector = _factory.Services.GetRequiredService<SecretProtector>();
        Assert.Equal(secret, protector.Unprotect(stored));

        GitCli git = _factory.Services.GetRequiredService<GitCli>();
        using GitInvocation invocation = git.Prepare(
            new[] { "ls-remote", "--symref", "https://gitlab.com/group/repo.git", "HEAD" },
            new GitAuth { Username = "oauth2", HttpsToken = secret, AllowHttp = false },
            TimeSpan.FromMinutes(1));
        string arguments = string.Join(" ", invocation.Arguments);
        Assert.DoesNotContain(secret, arguments);
        Assert.Contains("https://gitlab.com/group/repo.git", invocation.Arguments);
        Assert.Equal("0", invocation.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("1", invocation.Environment["GIT_CONFIG_NOSYSTEM"]);
        Assert.Equal("1", invocation.Environment["GIT_LFS_SKIP_SMUDGE"]);
        Assert.Equal("http.extraHeader", invocation.Environment["GIT_CONFIG_KEY_0"]);
        Assert.StartsWith("Authorization: Basic ", invocation.Environment["GIT_CONFIG_VALUE_0"]);
        Assert.DoesNotContain(secret, invocation.Environment["GIT_CONFIG_VALUE_0"]);
    }

    [Fact]
    public async Task Queue_ClaimDedupRetryResetAndRestorePause()
    {
        if (!Database) return;

        await using NpgsqlConnection cleanup = await OpenAsync();
        await using NpgsqlCommand cancelJobs = new(
            "UPDATE public.project_job SET status = 'cancelled', finished_at = now() WHERE status IN ('queued', 'running')",
            cleanup);
        await cancelJobs.ExecuteNonQueryAsync();

        ProjectJobQueue queue = _factory.Services.GetRequiredService<ProjectJobQueue>();
        string dedup = "fetch:test-" + Guid.NewGuid().ToString("N");
        long? first = await queue.EnqueueAsync("fetch", dedup, null, null, "{}", CancellationToken.None);
        long? second = await queue.EnqueueAsync("fetch", dedup, null, null, "{}", CancellationToken.None);
        Assert.NotNull(first);
        Assert.Null(second);

        await using NpgsqlConnection holder = await OpenAsync();
        await using NpgsqlTransaction hold = await holder.BeginTransactionAsync();
        await using NpgsqlCommand claim = new(ProjectJobQueue.ClaimSql, holder, hold);
        await using NpgsqlDataReader reader = await claim.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        long heldId = reader.GetInt64(reader.GetOrdinal("id"));
        await reader.DisposeAsync();

        long? other = await queue.EnqueueAsync("discover", "discover:test-" + Guid.NewGuid().ToString("N"), null, null, "{}", CancellationToken.None);
        Assert.NotNull(other);
        ProjectJob? skipped = await queue.ClaimAsync(CancellationToken.None);
        Assert.NotNull(skipped);
        Assert.NotEqual(heldId, skipped!.Id);
        await hold.RollbackAsync();

        ProjectJob? retryJob = await queue.ClaimAsync(CancellationToken.None);
        if (retryJob == null)
        {
            long? again = await queue.EnqueueAsync("fetch", "fetch:retry-" + Guid.NewGuid().ToString("N"), null, null, "{}", CancellationToken.None);
            retryJob = await queue.ClaimAsync(CancellationToken.None);
            Assert.Equal(again, retryJob!.Id);
        }

        await queue.RetryAsync(retryJob, "transient", CancellationToken.None);
        await using NpgsqlConnection connection = await OpenAsync();
        await using NpgsqlCommand after = new("SELECT status, EXTRACT(EPOCH FROM (run_after - now())) FROM public.project_job WHERE id = @id", connection);
        after.Parameters.AddWithValue("id", retryJob.Id);
        await using NpgsqlDataReader afterReader = await after.ExecuteReaderAsync();
        Assert.True(await afterReader.ReadAsync());
        Assert.Equal("queued", afterReader.GetString(0));
        double seconds = afterReader.GetDouble(1);
        Assert.InRange(seconds, 50, 70);
        await afterReader.DisposeAsync();

        ProjectJob third = new()
        {
            Id = retryJob.Id,
            Kind = retryJob.Kind,
            Attempts = 3
        };
        await using NpgsqlCommand forceRunning = new("UPDATE public.project_job SET status = 'running', attempts = 3 WHERE id = @id", connection);
        forceRunning.Parameters.AddWithValue("id", retryJob.Id);
        await forceRunning.ExecuteNonQueryAsync();
        await queue.RetryAsync(third, "still failing", CancellationToken.None);
        await using NpgsqlCommand failed = new("SELECT status, error FROM public.project_job WHERE id = @id", connection);
        failed.Parameters.AddWithValue("id", retryJob.Id);
        await using NpgsqlDataReader failedReader = await failed.ExecuteReaderAsync();
        Assert.True(await failedReader.ReadAsync());
        Assert.Equal("failed", failedReader.GetString(0));
        await failedReader.DisposeAsync();

        long? analyzeId = await queue.EnqueueAsync("analyze", "analyze:unknown:" + Guid.NewGuid().ToString("N"), null, null, "{}", CancellationToken.None);
        Assert.NotNull(analyzeId);
        await using NpgsqlCommand due = new("UPDATE public.project_job SET run_after = now() WHERE id = @id", connection);
        due.Parameters.AddWithValue("id", analyzeId!.Value);
        await due.ExecuteNonQueryAsync();
        ProjectJob? analyze = null;
        for (int i = 0; i < 5 && analyze?.Id != analyzeId; i++)
        {
            ProjectJob? claimed = await queue.ClaimAsync(CancellationToken.None);
            if (claimed?.Id == analyzeId)
            {
                analyze = claimed;
            }
        }

        Assert.NotNull(analyze);
        ProjectJobProcessor processor = _factory.Services.GetRequiredService<ProjectJobProcessor>();
        await processor.ExecuteAsync(analyze!, Array.Empty<IProjectJobHandler>(), CancellationToken.None);
        await using NpgsqlCommand unknown = new("SELECT status, error FROM public.project_job WHERE id = @id", connection);
        unknown.Parameters.AddWithValue("id", analyzeId.Value);
        await using NpgsqlDataReader unknownReader = await unknown.ExecuteReaderAsync();
        Assert.True(await unknownReader.ReadAsync());
        Assert.Equal("failed", unknownReader.GetString(0));
        Assert.Contains("Unknown job kind", unknownReader.GetString(1));
        await unknownReader.DisposeAsync();

        string stuckKey = "fetch:stuck-" + Guid.NewGuid().ToString("N");
        await using NpgsqlCommand insertRunning = new(
            "INSERT INTO public.project_job (kind, dedup_key, status, attempts) VALUES ('fetch', @key, 'running', 1) RETURNING id",
            connection);
        insertRunning.Parameters.AddWithValue("key", stuckKey);
        long runningId = (long)(await insertRunning.ExecuteScalarAsync())!;
        await queue.ResetStuckAsync(CancellationToken.None);
        Assert.Equal("queued", await JobStatusAsync(connection, runningId));

        string superseded = "fetch:super-" + Guid.NewGuid().ToString("N");
        await using NpgsqlCommand insertPair = new(
            """
            INSERT INTO public.project_job (kind, dedup_key, status) VALUES ('fetch', @key, 'queued') RETURNING id;
            """,
            connection);
        insertPair.Parameters.AddWithValue("key", superseded);
        await insertPair.ExecuteScalarAsync();
        await using NpgsqlCommand insertStuck = new(
            "INSERT INTO public.project_job (kind, dedup_key, status, attempts) VALUES ('fetch', @key, 'running', 1) RETURNING id",
            connection);
        insertStuck.Parameters.AddWithValue("key", superseded);
        long stuckId = (long)(await insertStuck.ExecuteScalarAsync())!;
        await queue.ResetStuckAsync(CancellationToken.None);
        Assert.Equal("cancelled", await JobStatusAsync(connection, stuckId));

        FieldInfo flag = typeof(BackupService).GetField("_restoreInProgress", BindingFlags.NonPublic | BindingFlags.Static)!;
        long? pausedId = await queue.EnqueueAsync("fetch", "fetch:pause-" + Guid.NewGuid().ToString("N"), null, null, "{}", CancellationToken.None);
        Assert.NotNull(pausedId);
        await using NpgsqlCommand makeDue = new("UPDATE public.project_job SET run_after = now(), status = 'queued' WHERE id = @id", connection);
        makeDue.Parameters.AddWithValue("id", pausedId!.Value);
        await makeDue.ExecuteNonQueryAsync();
        try
        {
            flag.SetValue(null, 1);
            Assert.Null(await queue.ClaimAsync(CancellationToken.None));
        }
        finally
        {
            flag.SetValue(null, 0);
        }
    }

    [Fact]
    public async Task Webhooks_GitLabAndGitHub_ValidInvalidPingAndDedup()
    {
        if (!Database) return;

        HttpClient admin = await AdminClientAsync();
        HttpClient anonymous = _factory.CreateClient();
        (int gitlabRepo, string gitlabSecret) = await CreateWebhookRepositoryAsync(admin, "gitlab", "https://gitlab.example.test", "https://gitlab.example.test/group/hook.git");
        (int githubRepo, string githubSecret) = await CreateWebhookRepositoryAsync(admin, "github", "https://github.com", "https://github.com/example/hook.git");

        byte[] pushBody = Encoding.UTF8.GetBytes("{\"object_kind\":\"push\",\"ref\":\"refs/heads/main\"}");
        using HttpRequestMessage valid = new(HttpMethod.Post, "/api/hooks/git/" + await RepositoryUuidAsync(gitlabRepo));
        valid.Content = new ByteArrayContent(pushBody);
        valid.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        valid.Headers.Add("X-Gitlab-Token", gitlabSecret);
        valid.Headers.Add("X-Gitlab-Event", "Push Hook");
        Assert.Equal(HttpStatusCode.Accepted, (await anonymous.SendAsync(valid)).StatusCode);

        using HttpRequestMessage invalid = new(HttpMethod.Post, "/api/hooks/git/" + await RepositoryUuidAsync(gitlabRepo));
        invalid.Content = new ByteArrayContent(pushBody);
        invalid.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        invalid.Headers.Add("X-Gitlab-Token", "wrong-token");
        invalid.Headers.Add("X-Gitlab-Event", "Push Hook");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(invalid)).StatusCode);

        Guid githubUuid = await RepositoryUuidAsync(githubRepo);
        byte[] pingBody = Encoding.UTF8.GetBytes("{\"zen\":\"pong\"}");
        using HttpRequestMessage ping = SignedGitHub(githubUuid, githubSecret, pingBody, "ping");
        Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(ping)).StatusCode);

        byte[] issueBody = Encoding.UTF8.GetBytes("{\"action\":\"opened\"}");
        using HttpRequestMessage ignored = SignedGitHub(githubUuid, githubSecret, issueBody, "issues");
        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.SendAsync(ignored)).StatusCode);

        using HttpRequestMessage badSign = SignedGitHub(githubUuid, "not-the-secret", pushBody, "push");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(badSign)).StatusCode);

        await using NpgsqlConnection connection = await OpenAsync();
        await using NpgsqlCommand running = new(
            "INSERT INTO public.project_job (kind, dedup_key, repository_id, status) VALUES ('fetch', @key, @repo, 'running')",
            connection);
        running.Parameters.AddWithValue("key", "fetch:" + gitlabRepo);
        running.Parameters.AddWithValue("repo", gitlabRepo);
        await running.ExecuteNonQueryAsync();
        Guid gitlabUuid = await RepositoryUuidAsync(gitlabRepo);
        for (int i = 0; i < 2; i++)
        {
            using HttpRequestMessage again = new(HttpMethod.Post, "/api/hooks/git/" + gitlabUuid);
            again.Content = new ByteArrayContent(pushBody);
            again.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            again.Headers.Add("X-Gitlab-Token", gitlabSecret);
            again.Headers.Add("X-Gitlab-Event", "Push Hook");
            Assert.Equal(HttpStatusCode.Accepted, (await anonymous.SendAsync(again)).StatusCode);
        }

        await using NpgsqlCommand count = new(
            "SELECT COUNT(*) FROM public.project_job WHERE dedup_key = @key AND status = 'queued'",
            connection);
        count.Parameters.AddWithValue("key", "fetch:" + gitlabRepo);
        Assert.Equal(1, Convert.ToInt32(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task GitMirror_FetchDiscoverRevisionsForcePushMovedTagAndCommitLimit()
    {
        if (!Database || !GitOnPath()) return;

        string root = Path.Combine(Path.GetTempPath(), "kicomlib-git-" + Guid.NewGuid().ToString("N"));
        try
        {
            string work = Path.Combine(root, "work");
            string bare = Path.Combine(root, "origin.git");
            Directory.CreateDirectory(work);
            Git("init -b main", work);
            WriteProject(work, "v1");
            Commit(work, "A");
            Git("init --bare \"" + bare + "\"");
            Git("remote add origin \"" + bare + "\"", work);
            Git("push -u origin HEAD", work);

            HttpClient admin = await AdminClientAsync();
            int repositoryId = await CreateRepositoryAsync(admin, ToFileUrl(bare), "generic", "https://git.local.test");
            await RunFetchAsync(repositoryId);
            HttpResponseMessage test = await admin.PostAsJsonAsync("/api/git-repository/test", new
            {
                hostId = await HostIdAsync(admin, repositoryId),
                cloneUrl = ToFileUrl(bare)
            });
            string testBody = await test.Content.ReadAsStringAsync();
            Assert.True(test.IsSuccessStatusCode, testBody);
            JsonElement tested = JsonDocument.Parse(testBody).RootElement;
            Assert.True(tested.GetProperty("ok").GetBoolean(), testBody);
            Assert.Contains("main", tested.GetProperty("branches").EnumerateArray().Select(item => item.GetString()));

            HttpResponseMessage discovered = await admin.GetAsync("/api/git-repository/" + repositoryId + "/discovered");
            string discoveredBody = await discovered.Content.ReadAsStringAsync();
            Assert.True(discovered.IsSuccessStatusCode, discoveredBody);
            Assert.Contains("hw/main/main.kicad_pro", discoveredBody);

            HttpResponseMessage project = await admin.PostAsJsonAsync("/api/project", new
            {
                repositoryId,
                projectFile = "hw/main/main.kicad_pro",
                name = "Mirror",
                status = "development"
            });
            string projectBody = await project.Content.ReadAsStringAsync();
            Assert.True(project.StatusCode == HttpStatusCode.Created, projectBody);
            int projectId = JsonDocument.Parse(projectBody).RootElement.GetProperty("id").GetInt32();
            await RunFetchAsync(repositoryId);
            Assert.Equal(1, await RevisionCountAsync(projectId));

            WriteProject(work, "B");
            Commit(work, "B");
            WriteProject(work, "C");
            Commit(work, "C");
            Git("push origin HEAD", work);
            Git("tag v1 HEAD~2", work);
            Git("push origin v1", work);
            await RunFetchAsync(repositoryId);
            Assert.Equal(3, await RevisionCountAsync(projectId));

            Git("tag -f v1 HEAD", work);
            Git("push --force origin refs/tags/v1", work);
            await RunFetchAsync(repositoryId);
            JsonElement repository = JsonDocument.Parse(await (await admin.GetAsync("/api/git-repository/" + repositoryId)).Content.ReadAsStringAsync()).RootElement;
            JsonElement moved = repository.GetProperty("movedTags");
            Assert.Contains(moved.EnumerateArray(), tag => tag.GetProperty("name").GetString() == "v1");

            Git("reset --hard HEAD~2", work);
            Git("push --force origin HEAD", work);
            Git("push origin :refs/tags/v1", work);
            await RunFetchAsync(repositoryId);
            await using NpgsqlConnection connection = await OpenAsync();
            await using NpgsqlCommand reachable = new(
                "SELECT COUNT(*) FROM public.project_revision WHERE project_id = @id AND reachable = false",
                connection);
            reachable.Parameters.AddWithValue("id", projectId);
            int unreachable = Convert.ToInt32(await reachable.ExecuteScalarAsync());
            Assert.True(unreachable >= 1);

            string limitRoot = Path.Combine(root, "limit");
            string limitWork = Path.Combine(limitRoot, "work");
            string limitBare = Path.Combine(limitRoot, "origin.git");
            Directory.CreateDirectory(limitWork);
            Git("init -b main", limitWork);
            WriteProject(limitWork, "base");
            Commit(limitWork, "base");
            Git("init --bare \"" + limitBare + "\"");
            Git("remote add origin \"" + limitBare + "\"", limitWork);
            Git("push -u origin HEAD", limitWork);
            int limitRepo = await CreateRepositoryAsync(admin, ToFileUrl(limitBare), "generic", "https://git.local.test");
            await RunFetchAsync(limitRepo);
            HttpResponseMessage limitProject = await admin.PostAsJsonAsync("/api/project", new
            {
                repositoryId = limitRepo,
                projectFile = "hw/main/main.kicad_pro",
                name = "Limit",
                status = "development"
            });
            string limitBody = await limitProject.Content.ReadAsStringAsync();
            Assert.True(limitProject.StatusCode == HttpStatusCode.Created, limitBody);
            int limitProjectId = JsonDocument.Parse(limitBody).RootElement.GetProperty("id").GetInt32();
            await RunFetchAsync(limitRepo);
            Assert.Equal(1, await RevisionCountAsync(limitProjectId));
            for (int i = 0; i < 21; i++)
            {
                WriteProject(limitWork, "c" + i);
                Commit(limitWork, "c" + i);
            }

            Git("push origin HEAD", limitWork);
            await RunFetchAsync(limitRepo);
            // The base commit plus the newest project_max_commits_per_sync commits (default 20).
            Assert.Equal(21, await RevisionCountAsync(limitProjectId));

            await RunFetchAsync(limitRepo);
            Assert.Equal(21, await RevisionCountAsync(limitProjectId));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
            catch (Exception)
            {
                // Temporary git directories are best-effort cleanup.
            }
        }
    }

    private async Task<int> HostIdAsync(HttpClient admin, int repositoryId)
    {
        JsonElement repository = JsonDocument.Parse(await (await admin.GetAsync("/api/git-repository/" + repositoryId)).Content.ReadAsStringAsync()).RootElement;
        return repository.GetProperty("hostId").GetInt32();
    }

    private async Task RunFetchAsync(int repositoryId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        FetchJobHandler handler = scope.ServiceProvider.GetRequiredService<FetchJobHandler>();
        await handler.RunAsync(new ProjectJob { Kind = "fetch", RepositoryId = repositoryId, Payload = "{}" }, CancellationToken.None);
    }

    private async Task<int> RevisionCountAsync(int projectId)
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await using NpgsqlCommand command = new("SELECT COUNT(*) FROM public.project_revision WHERE project_id = @id", connection);
        command.Parameters.AddWithValue("id", projectId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<(int Id, string Secret)> CreateWebhookRepositoryAsync(HttpClient admin, string kind, string webUrl, string cloneUrl)
    {
        int hostId = await EnsureHostAsync(admin, kind, webUrl);
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/git-repository", new
        {
            hostId,
            cloneUrl,
            syncMode = "webhook"
        });
        string body = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, body);
        JsonElement json = JsonDocument.Parse(body).RootElement;
        string? secret = json.GetProperty("webhook").GetProperty("secret").GetString();
        Assert.False(string.IsNullOrWhiteSpace(secret));
        HttpResponseMessage again = await admin.GetAsync("/api/git-repository/" + json.GetProperty("id").GetInt32());
        string listed = await again.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret!, listed);
        return (json.GetProperty("id").GetInt32(), secret!);
    }

    private async Task<int> CreateRepositoryAsync(HttpClient admin, string cloneUrl, string kind, string webUrl)
    {
        int hostId = await EnsureHostAsync(admin, kind, webUrl);
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/git-repository", new
        {
            hostId,
            cloneUrl,
            syncMode = "poll"
        });
        string body = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt32();
    }

    private async Task<int> EnsureHostAsync(HttpClient admin, string kind, string webUrl)
    {
        if (webUrl == "https://github.com")
        {
            JsonElement hosts = JsonDocument.Parse(await (await admin.GetAsync("/api/git-host")).Content.ReadAsStringAsync()).RootElement;
            return hosts.EnumerateArray().First(host => host.GetProperty("kind").GetString() == "github" && host.GetProperty("isSystem").GetBoolean()).GetProperty("id").GetInt32();
        }

        string name = kind + "-" + Guid.NewGuid().ToString("N")[..8];
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/git-host", new
        {
            kind,
            name,
            webUrl,
            sshHost = "git@git.local.test",
            allowHttp = false
        });
        string body = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt32();
    }

    private async Task<Guid> RepositoryUuidAsync(int id)
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await using NpgsqlCommand command = new("SELECT uuid FROM public.git_repository WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private static HttpRequestMessage SignedGitHub(Guid uuid, string secret, byte[] body, string eventName)
    {
        byte[] hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        HttpRequestMessage request = new(HttpMethod.Post, "/api/hooks/git/" + uuid);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-GitHub-Event", eventName);
        request.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexString(hash).ToLowerInvariant());
        return request;
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        return await LoginAsync("admin", "kicomlib");
    }

    private async Task<HttpClient> LoginAsync(string username, string password)
    {
        HttpClient client = _factory.CreateClient();
        HttpResponseMessage login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        string body = await login.Content.ReadAsStringAsync();
        Assert.True(login.IsSuccessStatusCode, body);
        TestSessions.UseBearer(client, login);
        return client;
    }

    private static async Task<int> CreateGroupAsync(HttpClient admin, string name, bool canWrite)
    {
        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/group", new
        {
            name,
            description = name,
            canRead = true,
            canWrite
        });
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt32();
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        NpgsqlConnection connection = new(_factory.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<int> ScalarIntAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string table)
    {
        await using NpgsqlCommand command = new("SELECT 1 FROM pg_catalog.pg_tables WHERE schemaname = 'public' AND tablename = @t", connection);
        command.Parameters.AddWithValue("t", table);
        return await command.ExecuteScalarAsync() != null;
    }

    private static async Task<string> JobStatusAsync(NpgsqlConnection connection, long id)
    {
        await using NpgsqlCommand command = new("SELECT status FROM public.project_job WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static bool GitOnPath()
    {
        try
        {
            ProcessStartInfo start = new("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using Process? process = Process.Start(start);
            if (process == null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void WriteProject(string work, string marker)
    {
        string directory = Path.Combine(work, "hw", "main");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "main.kicad_pro"), "(kicad_pro (version 20250114) (marker \"" + marker + "\"))\n");
    }

    private static void Commit(string work, string message)
    {
        Git("add -A", work);
        Git("-c user.email=test@example.com -c user.name=Test commit -m \"" + message + "\"", work);
    }

    private static void Git(string arguments, string? workingDirectory = null)
    {
        ProcessStartInfo start = new("git", arguments)
        {
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using Process? process = Process.Start(start);
        Assert.NotNull(process);
        string stdout = process!.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(60000);
        Assert.True(process.ExitCode == 0, arguments + "\n" + stdout + "\n" + stderr);
    }

    [Fact]
    public void CloneUrl_AllowsSshAccountAndKeepsHttpsTokenOutOfTheUrl()
    {
        GitUrlPolicy.RejectSshCredentials(new Uri("ssh://git@gitlab.host/group/repo.git"));
        Assert.Throws<GitAccessException>(() => GitUrlPolicy.RejectSshCredentials(new Uri("ssh://git:secret@gitlab.host/group/repo.git")));
        Assert.Throws<GitAccessException>(() => GitUrlPolicy.RejectUserInfo(new Uri("https://oauth2:glpat-secret@gitlab.host/group/repo.git")));

        Assert.Equal(
            "This clone URL uses HTTPS. Select an HTTPS token, or use the SSH clone URL with an SSH key.",
            GitUrlPolicy.CredentialMismatch("https://gitlab.host/group/repo.git", "ssh_key"));
        Assert.Null(GitUrlPolicy.CredentialMismatch("ssh://git@gitlab.host/group/repo.git", "ssh_key"));

        string withUser = GitUrlPolicy.WithHttpsUser("https://gitlab.host/group/repo.git", "oauth2");
        Assert.StartsWith("https://oauth2@gitlab.host/", withUser);
        Assert.Contains("/group/repo.git", withUser);
        Assert.DoesNotContain(":443", withUser);
        Assert.DoesNotContain("glpat", withUser);

        string called = GitCli.UrlForGit(
            "https://gitlab.host/group/repo.git",
            new GitAuth { Username = "oauth2", HttpsToken = "glpat-secret" });
        Assert.DoesNotContain("glpat-secret", called);
        Assert.Contains("oauth2@", called);
    }

    private static string ToFileUrl(string path)
    {
        string full = Path.GetFullPath(path).Replace('\\', '/');
        if (!full.StartsWith('/'))
        {
            full = "/" + full;
        }

        return "file://" + full;
    }
}
