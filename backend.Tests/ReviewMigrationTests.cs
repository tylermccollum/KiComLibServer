using KiComLibBackend.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace KiComLibBackend.Tests;

[Collection("kicomlib-db")]
public class ReviewMigrationTests(CustomWebApplicationFactory factory)
{
    [Fact]
    public async Task PopulatedV1_MigratesOnce_PreservesData_AndRejectsFutureSchema()
    {
        await InDatabase(async (connection, options) =>
        {
            await Baseline(connection, options.ScriptDirectory);
            await Execute(connection, "INSERT INTO part(name,value) VALUES('migration-part','1k'); INSERT INTO blob(hash,size,media_type,data) VALUES(repeat('a',64),3,'test',decode('616263','hex'))");
            var before = await Snapshot(connection);
            var initializer = new DatabaseInitializer(options, NullLogger<DatabaseInitializer>.Instance);
            await initializer.InitializeAsync();
            Assert.Equal(before, await Snapshot(connection));
            Assert.Equal(2, await Scalar(connection, "SELECT max(version) FROM schema_version"));
            Assert.Equal(0L, await Scalar(connection, "SELECT count(*) FROM review_definition"));
            string instance = (string)(await Scalar(connection, "SELECT instance_id::text FROM review_server"))!;
            await initializer.InitializeAsync();
            Assert.Equal(instance, await Scalar(connection, "SELECT instance_id::text FROM review_server"));
            await Execute(connection, "INSERT INTO schema_version(version) VALUES(999)");
            await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.InitializeAsync());
            Assert.Equal(before, await Snapshot(connection));
        });
    }

    [Fact]
    public async Task FailedMigrationRollsBackSchemaAndData()
    {
        await InDatabase(async (connection, options) =>
        {
            await Baseline(connection, options.ScriptDirectory);
            string scripts = Path.Combine(Path.GetTempPath(), "review-broken-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scripts);
            try
            {
                string migration = await File.ReadAllTextAsync(Path.Combine(options.ScriptDirectory, "08_Reviews.sql"));
                await File.WriteAllTextAsync(Path.Combine(scripts, "08_Reviews.sql"), migration + "\nSELECT missing_migration_function();");
                options.ScriptDirectory = scripts;
                await Assert.ThrowsAsync<PostgresException>(() => new DatabaseInitializer(options, NullLogger<DatabaseInitializer>.Instance).InitializeAsync());
                Assert.Equal(1, await Scalar(connection, "SELECT max(version) FROM schema_version"));
                Assert.Equal(DBNull.Value, await Scalar(connection, "SELECT to_regclass('public.review_definition')::text"));
                Assert.Equal(1L, await Scalar(connection, "SELECT count(*) FROM app_user"));
            }
            finally { Directory.Delete(scripts, true); }
        });
    }

    private async Task InDatabase(Func<NpgsqlConnection, DatabaseInitializerOptions, Task> action)
    {
        Assert.True(factory.DatabaseAvailable, "PostgreSQL is required for migration tests.");
        string name = "review_migration_" + Guid.NewGuid().ToString("N");
        var cs = new NpgsqlConnectionStringBuilder(factory.ConnectionString) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(cs.ConnectionString); await admin.OpenAsync();
        await Execute(admin, $"CREATE DATABASE {name}");
        try
        {
            cs.Database = name;
            await using var connection = new NpgsqlConnection(cs.ConnectionString); await connection.OpenAsync();
            var options = new DatabaseInitializerOptions
            {
                ConnectionString = cs.ConnectionString,
                ScriptDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "backend", "Data", "Sql")),
                StartupTimeout = TimeSpan.Zero
            };
            await action(connection, options);
        }
        finally { await Execute(admin, $"DROP DATABASE {name} WITH (FORCE)"); }
    }
    private static async Task Baseline(NpgsqlConnection connection, string path)
    {
        foreach (string file in Directory.GetFiles(path, "*.sql").Where(f => string.CompareOrdinal(Path.GetFileName(f), "08_") < 0).Order())
            await Execute(connection, (await File.ReadAllTextAsync(file)).Replace("{{OWNER}}", "CURRENT_USER"));
    }
    private static async Task<string> Snapshot(NpgsqlConnection connection) => (string)(await Scalar(connection,
        "SELECT jsonb_build_object('parts',(SELECT jsonb_agg(to_jsonb(p)) FROM part p),'users',(SELECT jsonb_agg(to_jsonb(u)) FROM app_user u),'blobs',(SELECT jsonb_agg(to_jsonb(b)) FROM blob b))::text"))!;
    private static async Task<object?> Scalar(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection); return await command.ExecuteScalarAsync();
    }
    private static async Task Execute(NpgsqlConnection connection, string sql) => await Scalar(connection, sql);
}
