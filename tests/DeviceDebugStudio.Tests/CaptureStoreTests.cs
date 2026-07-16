using DeviceDebugStudio.Core.Transports;
using DeviceDebugStudio.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace DeviceDebugStudio.Tests;

public sealed class CaptureStoreTests
{
    [Fact]
    public async Task PersistsPacketsInOrderWithoutChangingPayload()
    {
        string directory = Path.Combine(Path.GetTempPath(), "DeviceDebugStudio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            SqliteCaptureStore store = new(directory);
            await store.StartAsync("捕获测试", TransportKind.Serial);
            await store.AppendAsync(new TransportPacket(DateTimeOffset.Now, PacketDirection.Receive, [1, 2, 3], "COM1"));
            await store.AppendAsync(new TransportPacket(DateTimeOffset.Now, PacketDirection.Send, [4, 5], "COM1"));
            await store.CompleteAsync();

            Assert.NotNull(store.FilePath);
            await using (SqliteConnection connection = new($"Data Source={store.FilePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "SELECT payload FROM packets ORDER BY id";
                await using SqliteDataReader reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal([1, 2, 3], (byte[])reader[0]);
                Assert.True(await reader.ReadAsync());
                Assert.Equal([4, 5], (byte[])reader[0]);
                Assert.False(await reader.ReadAsync());
            }

            await store.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task CaptureReaderOpensExistingDatabaseInOriginalOrder()
    {
        string directory = Path.Combine(Path.GetTempPath(), "DeviceDebugStudio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using SqliteCaptureStore store = new(directory);
            await store.StartAsync("读取测试", TransportKind.TcpClient);
            DateTimeOffset firstTimestamp = DateTimeOffset.Parse("2026-07-15T10:00:00+08:00");
            await store.AppendAsync(new TransportPacket(firstTimestamp, PacketDirection.Receive, [0x10, 0x20], "127.0.0.1:8000"));
            await store.AppendAsync(new TransportPacket(firstTimestamp.AddSeconds(1), PacketDirection.Send, [0x30], "127.0.0.1:8000", "发送"));
            await store.CompleteAsync();

            CaptureOpenResult result = await new CaptureFileReader().ReadAsync(Assert.IsType<string>(store.FilePath));

            Assert.Equal(2, result.TotalPackets);
            Assert.Equal([0x10, 0x20], result.Packets[0].Data.ToArray());
            Assert.Equal(PacketDirection.Send, result.Packets[1].Direction);
            Assert.Equal("发送", result.Packets[1].Message);
            SqliteConnection.ClearAllPools();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
