using System.Data;
using System.Text.Json.Nodes;
using KiComLibBackend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace KiComLibBackend.Services.Reviews;

public sealed class ReviewDatabase(ApplicationDbContext context)
{
    public ApplicationDbContext Context => context;
    public async Task<T> Snapshot<T>(Func<Task<T>> action, CancellationToken ct)
    {
        if (context.Database.CurrentTransaction != null) return await action();
        await using var tx = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        T result = await action();
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<List<JsonObject>> Rows(string sql, CancellationToken ct, params (string, object?)[] parameters)
    {
        await using var command = await Command(sql, ct, parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);
        List<JsonObject> result = [];
        while (await reader.ReadAsync(ct)) result.Add(JsonNode.Parse(reader.GetString(0))!.AsObject());
        return result;
    }

    public async Task<JsonObject?> Row(string sql, CancellationToken ct, params (string, object?)[] parameters) =>
        (await Rows(sql, ct, parameters)).FirstOrDefault();

    public async Task Execute(string sql, CancellationToken ct, params (string, object?)[] parameters)
    {
        await using var command = await Command(sql, ct, parameters);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<byte[]?> Blob(string hash, CancellationToken ct)
    {
        await using var command = await Command("SELECT data FROM public.blob WHERE hash=@hash", ct, [("hash", hash)]);
        return await command.ExecuteScalarAsync(ct) as byte[];
    }

    private async Task<NpgsqlCommand> Command(string sql, CancellationToken ct, (string, object?)[] parameters)
    {
        if (context.Database.GetDbConnection().State != ConnectionState.Open) await context.Database.OpenConnectionAsync(ct);
        var command = new NpgsqlCommand(sql, (NpgsqlConnection)context.Database.GetDbConnection(), (NpgsqlTransaction?)context.Database.CurrentTransaction?.GetDbTransaction());
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}

public sealed class ReviewException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
