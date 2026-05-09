using System.Reflection;
using AiDaemon.Configuration;
using AiDaemon.Models;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiDaemon.Storage;

public class SqliteStateStore : IStateStore
{
    readonly string _connectionString;
    readonly ILogger<SqliteStateStore> _logger;

    public SqliteStateStore(IOptions<DaemonOptions> options, ILogger<SqliteStateStore> logger)
        : this(BuildDefaultConnectionString(options.Value.DataDir), logger)
    {
    }

    public SqliteStateStore(string connectionString, ILogger<SqliteStateStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    static string BuildDefaultConnectionString(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, "state.db");
        return new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ConnectionString;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var schema = LoadEmbeddedSchema();

        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(schema, cancellationToken: cancellationToken));

        _logger.LogInformation("State store initialized at {Connection}", _connectionString);
    }

    public async Task<bool> IsProcessedAsync(string threadId, string commentId, CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);
        var count = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "select count(*) from processed where thread_id = $tid and comment_id = $cid",
            new { tid = threadId, cid = commentId },
            cancellationToken: cancellationToken));

        return count > 0;
    }

    public async Task MarkProcessedAsync(string threadId, string commentId, string outcome, CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into processed (thread_id, comment_id, processed_at, outcome)
            values ($tid, $cid, $ts, $outcome)
            on conflict (thread_id, comment_id) do update set
              processed_at = excluded.processed_at,
              outcome      = excluded.outcome
            """,
            new
            {
                tid = threadId,
                cid = commentId,
                ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                outcome,
            },
            cancellationToken: cancellationToken));
    }

    public async Task<int> PruneProcessedAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "delete from processed where processed_at < $cutoff",
            new { cutoff = cutoff.ToUnixTimeSeconds() },
            cancellationToken: cancellationToken));

        return rows;
    }

    public async Task<BranchState?> GetBranchStateAsync(string branch, CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);
        var row = await conn.QuerySingleOrDefaultAsync<BranchRow>(new CommandDefinition(
            "select * from branches where branch = $branch",
            new { branch },
            cancellationToken: cancellationToken));

        return row?.ToState();
    }

    public async Task UpsertBranchStateAsync(BranchState state, CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into branches (
              branch, session_id, worktree, mode,
              rc_pid, rc_claude_pid, rc_claude_start, rc_bridge_id, rc_url,
              last_event_at, pr_number, issue_number)
            values (
              $branch, $sessionId, $worktree, $mode,
              $rcPid, $rcClaudePid, $rcClaudeStart, $rcBridgeId, $rcUrl,
              $lastEventAt, $prNumber, $issueNumber)
            on conflict (branch) do update set
              session_id      = excluded.session_id,
              worktree        = excluded.worktree,
              mode            = excluded.mode,
              rc_pid          = excluded.rc_pid,
              rc_claude_pid   = excluded.rc_claude_pid,
              rc_claude_start = excluded.rc_claude_start,
              rc_bridge_id    = excluded.rc_bridge_id,
              rc_url          = excluded.rc_url,
              last_event_at   = excluded.last_event_at,
              pr_number       = excluded.pr_number,
              issue_number    = excluded.issue_number
            """,
            new
            {
                branch = state.Branch,
                sessionId = state.SessionId,
                worktree = state.Worktree,
                mode = state.Mode.ToString(),
                rcPid = state.RcPid,
                rcClaudePid = state.RcClaudePid,
                rcClaudeStart = state.RcClaudeStart,
                rcBridgeId = state.RcBridgeId,
                rcUrl = state.RcUrl,
                lastEventAt = state.LastEventAt,
                prNumber = state.PrNumber,
                issueNumber = state.IssueNumber,
            },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<BranchState>> ListActiveBranchesAsync(CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);
        var rows = await conn.QueryAsync<BranchRow>(new CommandDefinition(
            "select * from branches where mode = 'RcActive'",
            cancellationToken: cancellationToken));

        return rows.Select(r => r.ToState()).ToList();
    }

    public async Task<int> IncrementRateLimitAsync(string threadId, DateOnly day, CancellationToken cancellationToken)
    {
        var dayKey = day.ToString("yyyy-MM-dd");

        await using var conn = await OpenAsync(cancellationToken);
        var newCount = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            insert into rate_limits (thread_id, day, count)
            values ($tid, $day, 1)
            on conflict (thread_id, day) do update set count = count + 1
            returning count
            """,
            new { tid = threadId, day = dayKey },
            cancellationToken: cancellationToken));

        return (int)newCount;
    }

    public async Task<string?> GetKvAsync(string key, CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "select value from kv where key = $key",
            new { key },
            cancellationToken: cancellationToken));
    }

    public async Task SetKvAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into kv (key, value) values ($key, $value)
            on conflict (key) do update set value = excluded.value
            """,
            new { key, value },
            cancellationToken: cancellationToken));
    }

    public async Task<int> GetRateLimitAsync(string threadId, DateOnly day, CancellationToken cancellationToken)
    {
        var dayKey = day.ToString("yyyy-MM-dd");

        await using var conn = await OpenAsync(cancellationToken);
        var count = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "select count from rate_limits where thread_id = $tid and day = $day",
            new { tid = threadId, day = dayKey },
            cancellationToken: cancellationToken));

        return (int)(count ?? 0);
    }

    async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await conn.ExecuteAsync(new CommandDefinition(
            "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;",
            cancellationToken: cancellationToken));

        return conn;
    }

    static string LoadEmbeddedSchema()
    {
        var asm = typeof(SqliteStateStore).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("Schema.sql", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "Schema.sql not found as embedded resource. Check AiDaemon.csproj <EmbeddedResource Include=\"Storage\\Schema.sql\" />.");

        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Failed to open embedded resource {name}");
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    /// <summary>Internal row shape — mirrors the table for Dapper hydration.</summary>
    class BranchRow
    {
        public string branch { get; set; } = "";
        public string session_id { get; set; } = "";
        public string worktree { get; set; } = "";
        public string mode { get; set; } = "";
        public long? rc_pid { get; set; }
        public long? rc_claude_pid { get; set; }
        public long? rc_claude_start { get; set; }
        public string? rc_bridge_id { get; set; }
        public string? rc_url { get; set; }
        public long last_event_at { get; set; }
        public long? pr_number { get; set; }
        public long? issue_number { get; set; }

        public BranchState ToState() => new()
        {
            Branch = branch,
            SessionId = session_id,
            Worktree = worktree,
            Mode = Enum.TryParse<BranchMode>(mode, out var m) ? m : BranchMode.Idle,
            RcPid = rc_pid is null ? null : (int)rc_pid,
            RcClaudePid = rc_claude_pid is null ? null : (int)rc_claude_pid,
            RcClaudeStart = rc_claude_start,
            RcBridgeId = rc_bridge_id,
            RcUrl = rc_url,
            LastEventAt = last_event_at,
            PrNumber = pr_number is null ? null : (int)pr_number,
            IssueNumber = issue_number is null ? null : (int)issue_number,
        };
    }
}
