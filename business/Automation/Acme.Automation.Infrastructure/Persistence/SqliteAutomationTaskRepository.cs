using Acme.Automation.Domain;
using Acme.Automation.Domain.Interfaces;
using Microsoft.Data.Sqlite;

namespace Acme.Automation.Infrastructure.Persistence;

public class SqliteAutomationTaskRepository : IAutomationTaskRepository
{
    private readonly string _connectionString;

    public SqliteAutomationTaskRepository(string connectionString)
    {
        _connectionString = connectionString;
        InitializeAsync().Wait();
    }

    private async Task InitializeAsync()
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var createTable = @"
            CREATE TABLE IF NOT EXISTS AutomationTasks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AgentId TEXT NOT NULL,
                TaskName TEXT NOT NULL,
                ConversationId TEXT NOT NULL DEFAULT '',
                Prompt TEXT NOT NULL DEFAULT '',
                ScheduleType INTEGER NOT NULL,
                IntervalMinutes INTEGER NOT NULL,
                ExecuteTime TEXT NOT NULL,
                WeekdaysJson TEXT,
                Enabled INTEGER NOT NULL,
                LastExecutionTime TEXT,
                LastExecutionStatus TEXT,
                LastExecutionResult TEXT,
                NextExecutionTime TEXT,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );";

        using var cmd = new SqliteCommand(createTable, conn);
        await cmd.ExecuteNonQueryAsync();

        // 幂等迁移：旧库补列（列已存在则忽略 duplicate column 错误）。
        await EnsureColumnAsync(conn, "ConversationId", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(conn, "Prompt", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(conn, "WeekdaysJson", "TEXT");
    }

    private static async Task EnsureColumnAsync(SqliteConnection conn, string column, string definition)
    {
        try
        {
            using var cmd = new SqliteCommand(
                $"ALTER TABLE AutomationTasks ADD COLUMN {column} {definition};", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqliteException)
        {
            // 列已存在——忽略。
        }
    }

    public async Task<List<AutomationTask>> GetAllAsync(CancellationToken cancellationToken)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        using var cmd = new SqliteCommand("SELECT * FROM AutomationTasks ORDER BY Id", conn);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var tasks = new List<AutomationTask>();
        while (await reader.ReadAsync(cancellationToken))
            tasks.Add(ReadTask(reader));

        return tasks;
    }

    public async Task<AutomationTask?> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        using var cmd = new SqliteCommand("SELECT * FROM AutomationTasks WHERE Id = @Id", conn);
        cmd.Parameters.AddWithValue("@Id", id);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTask(reader) : null;
    }

    public async Task<AutomationTask> AddAsync(AutomationTask task, CancellationToken cancellationToken)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        using var cmd = new SqliteCommand(@"
            INSERT INTO AutomationTasks (AgentId, TaskName, ConversationId, Prompt, ScheduleType, IntervalMinutes,
                ExecuteTime, WeekdaysJson, Enabled, LastExecutionTime, LastExecutionStatus, LastExecutionResult,
                NextExecutionTime, CreatedAt, UpdatedAt)
            VALUES (@AgentId, @TaskName, @ConversationId, @Prompt, @ScheduleType, @IntervalMinutes,
                @ExecuteTime, @WeekdaysJson, @Enabled, @LastExecutionTime, @LastExecutionStatus, @LastExecutionResult,
                @NextExecutionTime, @CreatedAt, @UpdatedAt);
            SELECT last_insert_rowid();", conn);

        AddParameters(cmd, task);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        task.Id = result != null ? (int)(long)result : 0;
        return task;
    }

    public async Task<AutomationTask> UpdateAsync(AutomationTask task, CancellationToken cancellationToken)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        using var cmd = new SqliteCommand(@"
            UPDATE AutomationTasks SET
                AgentId = @AgentId, TaskName = @TaskName, ConversationId = @ConversationId, Prompt = @Prompt,
                ScheduleType = @ScheduleType,
                IntervalMinutes = @IntervalMinutes, ExecuteTime = @ExecuteTime, WeekdaysJson = @WeekdaysJson,
                Enabled = @Enabled,
                LastExecutionTime = @LastExecutionTime, LastExecutionStatus = @LastExecutionStatus,
                LastExecutionResult = @LastExecutionResult, NextExecutionTime = @NextExecutionTime,
                UpdatedAt = @UpdatedAt
            WHERE Id = @Id;", conn);

        AddParameters(cmd, task);
        cmd.Parameters.AddWithValue("@Id", task.Id);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return task;
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        using var cmd = new SqliteCommand("DELETE FROM AutomationTasks WHERE Id = @Id", conn);
        cmd.Parameters.AddWithValue("@Id", id);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<AutomationTask>> GetDueTasksAsync(CancellationToken cancellationToken)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var now = DateTime.UtcNow.ToString("o");
        using var cmd = new SqliteCommand(
            "SELECT * FROM AutomationTasks WHERE Enabled = 1 AND NextExecutionTime IS NOT NULL AND NextExecutionTime <= @Now", 
            conn);
        cmd.Parameters.AddWithValue("@Now", now);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var tasks = new List<AutomationTask>();
        while (await reader.ReadAsync(cancellationToken))
            tasks.Add(ReadTask(reader));

        return tasks;
    }

    private static AutomationTask ReadTask(SqliteDataReader reader)
    {
        var task = new AutomationTask
        {
            Id = reader.GetInt32(reader.GetOrdinal("Id")),
            AgentId = reader.GetString(reader.GetOrdinal("AgentId")),
            TaskName = reader.GetString(reader.GetOrdinal("TaskName")),
            ConversationId = Guid.TryParse(reader.IsDBNull(reader.GetOrdinal("ConversationId")) ? null : reader.GetString(reader.GetOrdinal("ConversationId")), out var cid) ? cid : Guid.Empty,
            Prompt = reader.IsDBNull(reader.GetOrdinal("Prompt")) ? string.Empty : reader.GetString(reader.GetOrdinal("Prompt")),
            ScheduleType = (AutomationScheduleType)reader.GetInt32(reader.GetOrdinal("ScheduleType")),
            IntervalMinutes = reader.GetInt32(reader.GetOrdinal("IntervalMinutes")),
            ExecuteTime = TimeOnly.Parse(reader.GetString(reader.GetOrdinal("ExecuteTime"))),
            WeekdaysJson = reader.IsDBNull(reader.GetOrdinal("WeekdaysJson")) ? null : reader.GetString(reader.GetOrdinal("WeekdaysJson")),
            Enabled = reader.GetInt32(reader.GetOrdinal("Enabled")) == 1,
            LastExecutionTime = reader.IsDBNull(reader.GetOrdinal("LastExecutionTime")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("LastExecutionTime"))),
            LastExecutionStatus = reader.IsDBNull(reader.GetOrdinal("LastExecutionStatus")) ? null : reader.GetString(reader.GetOrdinal("LastExecutionStatus")),
            LastExecutionResult = reader.IsDBNull(reader.GetOrdinal("LastExecutionResult")) ? null : reader.GetString(reader.GetOrdinal("LastExecutionResult")),
            NextExecutionTime = reader.IsDBNull(reader.GetOrdinal("NextExecutionTime")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("NextExecutionTime"))),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
            UpdatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("UpdatedAt")))
        };
        task.DeserializeWeekdays();
        return task;
    }

    private static void AddParameters(SqliteCommand cmd, AutomationTask task)
    {
        cmd.Parameters.AddWithValue("@AgentId", task.AgentId);
        cmd.Parameters.AddWithValue("@TaskName", task.TaskName);
        cmd.Parameters.AddWithValue("@ConversationId", task.ConversationId.ToString());
        cmd.Parameters.AddWithValue("@Prompt", task.Prompt ?? string.Empty);
        cmd.Parameters.AddWithValue("@ScheduleType", (int)task.ScheduleType);
        cmd.Parameters.AddWithValue("@IntervalMinutes", task.IntervalMinutes);
        cmd.Parameters.AddWithValue("@ExecuteTime", task.ExecuteTime.ToString("HH:mm:ss"));
        cmd.Parameters.AddWithValue("@WeekdaysJson", (object?)task.WeekdaysJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Enabled", task.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@LastExecutionTime", (object?)task.LastExecutionTime?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@LastExecutionStatus", (object?)task.LastExecutionStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@LastExecutionResult", (object?)task.LastExecutionResult ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@NextExecutionTime", (object?)task.NextExecutionTime?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAt", task.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@UpdatedAt", task.UpdatedAt.ToString("o"));
    }
}