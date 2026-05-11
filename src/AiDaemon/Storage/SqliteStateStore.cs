using AiDaemon.Common;
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
        var schema = EmbeddedResource.Load(typeof(SqliteStateStore).Assembly, "Schema.sql");

        await using var conn = await OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(schema, cancellationToken: cancellationToken));

        await ApplyAdditiveMigrationsAsync(conn, cancellationToken);

        _logger.LogInformation("State store initialized at {Connection}", _connectionString);
    }

    /// <summary>
    /// SQLite's <c>CREATE TABLE IF NOT EXISTS</c> is no-op on an existing table even if its
    /// columns drift from the canonical schema. For DBs created before a column was added we
    /// need an explicit <c>ALTER TABLE ADD COLUMN</c>. SQLite has no <c>IF NOT EXISTS</c>
    /// modifier on ADD COLUMN, so we read <c>pragma_table_info</c> first and only add what's
    /// missing. Keep this idempotent: every block must be safe to re-run on a fresh DB.
    /// </summary>
    static async Task ApplyAdditiveMigrationsAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        var processedColumns = (await conn.QueryAsync<string>(new CommandDefinition(
            "select name from pragma_table_info('processed')",
            cancellationToken: cancellationToken))).ToHashSet(StringComparer.OrdinalIgnoreCase);

        async Task AddIfMissing(string column, string ddl)
        {
            if (processedColumns.Contains(column)) return;
            await conn.ExecuteAsync(new CommandDefinition(ddl, cancellationToken: cancellationToken));
        }

        await AddIfMissing("repo",         "alter table processed add column repo TEXT");
        await AddIfMissing("title",        "alter table processed add column title TEXT");
        await AddIfMissing("subject_type", "alter table processed add column subject_type TEXT");
    }

    public async Task<bool> IsProcessedAsync(string threadId, string commentId, CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);
        var hit = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "select 1 from processed where thread_id = $tid and comment_id = $cid limit 1",
            new { tid = threadId, cid = commentId },
            cancellationToken: cancellationToken));

        return hit is not null;
    }

    public async Task MarkProcessedAsync(string threadId, string commentId, string outcome, ProcessedContext? context, CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);
        // ON CONFLICT preserves any previously-captured display context if the new write
        // doesn't have one — i.e. a hypothetical future code path that re-marks a row with
        // null context won't blank out the repo/title columns we relied on for Retry labels.
        await conn.ExecuteAsync(new CommandDefinition(
            """
            insert into processed (thread_id, comment_id, processed_at, outcome, repo, title, subject_type)
            values ($tid, $cid, $ts, $outcome, $repo, $title, $type)
            on conflict (thread_id, comment_id) do update set
              processed_at = excluded.processed_at,
              outcome      = excluded.outcome,
              repo         = coalesce(excluded.repo,         processed.repo),
              title        = coalesce(excluded.title,        processed.title),
              subject_type = coalesce(excluded.subject_type, processed.subject_type)
            """,
            new
            {
                tid = threadId,
                cid = commentId,
                ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                outcome,
                repo = context?.Repo,
                title = context?.Title,
                type = context?.SubjectType,
            },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<ProcessedEntry>> ListRecentProcessedAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0) return Array.Empty<ProcessedEntry>();

        await using var conn = await OpenAsync(cancellationToken);
        var rows = await conn.QueryAsync<ProcessedRow>(new CommandDefinition(
            """
            select thread_id, comment_id, processed_at, outcome, repo, title, subject_type
            from processed
            order by processed_at desc
            limit $limit
            """,
            new { limit },
            cancellationToken: cancellationToken));

        return rows.Select(r => new ProcessedEntry(
            ThreadId:     r.thread_id,
            CommentId:    r.comment_id,
            ProcessedAt:  DateTimeOffset.FromUnixTimeSeconds(r.processed_at),
            Outcome:      r.outcome,
            Repo:         r.repo,
            Title:        r.title,
            SubjectType:  r.subject_type)).ToList();
    }

    public async Task<bool> UnmarkProcessedAsync(string threadId, string commentId, CancellationToken cancellationToken)
    {
        await using var conn = await OpenAsync(cancellationToken);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "delete from processed where thread_id = $tid and comment_id = $cid",
            new { tid = threadId, cid = commentId },
            cancellationToken: cancellationToken));

        return rows > 0;
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

    /// <summary>Internal row shape for the processed table — mirrors columns for Dapper hydration.</summary>
    class ProcessedRow
    {
        public string thread_id { get; set; } = "";
        public string comment_id { get; set; } = "";
        public long processed_at { get; set; }
        public string outcome { get; set; } = "";
        public string? repo { get; set; }
        public string? title { get; set; }
        public string? subject_type { get; set; }
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
