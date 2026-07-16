using System.Text.RegularExpressions;
using System.Threading.Channels;
using DeviceDebugStudio.Core.Sessions;
using DeviceDebugStudio.Core.Transports;
using Microsoft.Data.Sqlite;

namespace DeviceDebugStudio.Infrastructure.Persistence;

public sealed partial class SqliteCaptureStore(string? captureDirectory = null) : ICaptureStore
{
    private readonly string _captureDirectory = captureDirectory ?? AppPaths.CaptureDirectory;
    private Channel<TransportPacket>? _channel;
    private Task? _writerTask;
    private string? _sessionName;
    private TransportKind _transportKind;
    private int _started;

    public string? FilePath { get; private set; }

    public Task StartAsync(string sessionName, TransportKind transportKind, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("捕获存储已经启动。 ");
        }

        Directory.CreateDirectory(_captureDirectory);
        _sessionName = sessionName;
        _transportKind = transportKind;
        string safeName = InvalidFileNameRegex().Replace(sessionName, "_").Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "Session";
        }

        FilePath = Path.Combine(_captureDirectory, $"{DateTime.Now:yyyyMMdd_HHmmss}_{safeName}.db");
        _channel = Channel.CreateUnbounded<TransportPacket>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _writerTask = Task.Run(() => WriterLoopAsync(FilePath, _channel.Reader), CancellationToken.None);
        return Task.CompletedTask;
    }

    public ValueTask AppendAsync(TransportPacket packet, CancellationToken cancellationToken = default)
    {
        Channel<TransportPacket> channel = _channel ?? throw new InvalidOperationException("捕获存储尚未启动。 ");
        return channel.Writer.WriteAsync(packet, cancellationToken);
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        Channel<TransportPacket>? channel = _channel;
        if (channel is null)
        {
            return;
        }

        channel.Writer.TryComplete();
        if (_writerTask is not null)
        {
            await _writerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        _channel = null;
        _writerTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        await CompleteAsync().ConfigureAwait(false);
    }

    private async Task WriterLoopAsync(string path, ChannelReader<TransportPacket> reader)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        await using SqliteConnection connection = new(builder.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        await InitializeSchemaAsync(connection).ConfigureAwait(false);

        await using (SqliteCommand session = connection.CreateCommand())
        {
            session.CommandText = "INSERT INTO sessions(name, transport_kind, started_at) VALUES ($name, $kind, $started);";
            session.Parameters.AddWithValue("$name", _sessionName ?? "Session");
            session.Parameters.AddWithValue("$kind", _transportKind.ToString());
            session.Parameters.AddWithValue("$started", DateTimeOffset.Now.ToString("O"));
            await session.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        List<TransportPacket> batch = new(256);
        await foreach (TransportPacket packet in reader.ReadAllAsync().ConfigureAwait(false))
        {
            batch.Add(packet);
            while (batch.Count < 256 && reader.TryRead(out TransportPacket? buffered))
            {
                batch.Add(buffered);
            }

            await WriteBatchAsync(connection, batch).ConfigureAwait(false);
            batch.Clear();
        }
    }

    private static async Task InitializeSchemaAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS sessions(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                transport_kind TEXT NOT NULL,
                started_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS packets(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                direction INTEGER NOT NULL,
                endpoint TEXT NOT NULL,
                payload BLOB NOT NULL,
                message TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_packets_timestamp ON packets(timestamp);
            """;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task WriteBatchAsync(SqliteConnection connection, IReadOnlyList<TransportPacket> batch)
    {
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO packets(timestamp, direction, endpoint, payload, message) VALUES ($time, $direction, $endpoint, $payload, $message);";
        SqliteParameter time = command.Parameters.Add("$time", SqliteType.Text);
        SqliteParameter direction = command.Parameters.Add("$direction", SqliteType.Integer);
        SqliteParameter endpoint = command.Parameters.Add("$endpoint", SqliteType.Text);
        SqliteParameter payload = command.Parameters.Add("$payload", SqliteType.Blob);
        SqliteParameter message = command.Parameters.Add("$message", SqliteType.Text);
        foreach (TransportPacket packet in batch)
        {
            time.Value = packet.Timestamp.ToString("O");
            direction.Value = (int)packet.Direction;
            endpoint.Value = packet.Endpoint;
            payload.Value = packet.Data;
            message.Value = (object?)packet.Message ?? DBNull.Value;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
    }

    [GeneratedRegex("[\\\\/:*?\"<>|]+")]
    private static partial Regex InvalidFileNameRegex();
}
