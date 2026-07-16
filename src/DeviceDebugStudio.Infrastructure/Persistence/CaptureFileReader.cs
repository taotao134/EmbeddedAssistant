using DeviceDebugStudio.Core.Transports;
using Microsoft.Data.Sqlite;

namespace DeviceDebugStudio.Infrastructure.Persistence;

public sealed record CaptureOpenResult(long TotalPackets, IReadOnlyList<TransportPacket> Packets);

public sealed class CaptureFileReader
{
    public async Task<CaptureOpenResult> ReadAsync(string path, int limit = 100_000, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到捕获数据库。", path);
        }

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        await using SqliteConnection connection = new(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        long total;
        await using (SqliteCommand countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = "SELECT COUNT(*) FROM packets";
            total = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT timestamp, direction, endpoint, payload, message
            FROM (
                SELECT id, timestamp, direction, endpoint, payload, message
                FROM packets
                ORDER BY id DESC
                LIMIT $limit
            )
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500_000));
        List<TransportPacket> packets = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            packets.Add(new TransportPacket(
                DateTimeOffset.Parse(reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind),
                (PacketDirection)reader.GetInt32(1),
                (byte[])reader[3],
                reader.GetString(2),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return new CaptureOpenResult(total, packets);
    }
}
