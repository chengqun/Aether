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
                ScheduleType INTEGER NOT NULL,
                IntervalMinutes INTEGER NOT NULL,
                ExecuteTime TEXT NOT NULL,
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
            INSERT INTO AutomationTasks (AgentId, TaskName, ScheduleType, IntervalMinutes, 
                ExecuteTime, Enabled, LastExecutionTime, LastExecutionStatus, LastExecutionResult,
                NextExecutionTime, CreatedAt, UpdatedAt)
            VALUES (@AgentId, @TaskName, @ScheduleType, @IntervalMinutes, 
                @ExecuteTime, @Enabled, @LastExecutionTime, @LastExecutionStatus, @LastExecutionResult,
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
                AgentId = @AgentId, TaskName = @TaskName, ScheduleType = @ScheduleType, 
                IntervalMinutes = @IntervalMinutes, ExecuteTime = @ExecuteTime, Enabled = @Enabled,
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
        return new AutomationTask
        {
            Id = reader.GetInt32(0),
            AgentId = reader.GetString(1),
            TaskName = reader.GetString(2),
            ScheduleType = (AutomationScheduleType)reader.GetInt32(3),
            IntervalMinutes = reader.GetInt32(4),
            ExecuteTime = TimeOnly.Parse(reader.GetString(5)),
            Enabled = reader.GetInt32(6) == 1,
            LastExecutionTime = reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7)),
            LastExecutionStatus = reader.IsDBNull(8) ? null : reader.GetString(8),
            LastExecutionResult = reader.IsDBNull(9) ? null : reader.GetString(9),
            NextExecutionTime = reader.IsDBNull(10) ? null : DateTime.Parse(reader.GetString(10)),
            CreatedAt = DateTime.Parse(reader.GetString(11)),
            UpdatedAt = DateTime.Parse(reader.GetString(12))
        };
    }

    private static void AddParameters(SqliteCommand cmd, AutomationTask task)
    {
        cmd.Parameters.AddWithValue("@AgentId", task.AgentId);
        cmd.Parameters.AddWithValue("@TaskName", task.TaskName);
        cmd.Parameters.AddWithValue("@ScheduleType", (int)task.ScheduleType);
        cmd.Parameters.AddWithValue("@IntervalMinutes", task.IntervalMinutes);
        cmd.Parameters.AddWithValue("@ExecuteTime", task.ExecuteTime.ToString("HH:mm:ss"));
        cmd.Parameters.AddWithValue("@Enabled", task.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@LastExecutionTime", (object?)task.LastExecutionTime?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@LastExecutionStatus", (object?)task.LastExecutionStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@LastExecutionResult", (object?)task.LastExecutionResult ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@NextExecutionTime", (object?)task.NextExecutionTime?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAt", task.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@UpdatedAt", task.UpdatedAt.ToString("o"));
    }
}