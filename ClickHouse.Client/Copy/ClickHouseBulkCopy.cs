﻿using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClickHouse.Client.ADO;
using ClickHouse.Client.ADO.Readers;
using ClickHouse.Client.Formats;
using ClickHouse.Client.Properties;
using ClickHouse.Client.Types;
using ClickHouse.Client.Utility;

namespace ClickHouse.Client.Copy
{
    public class ClickHouseBulkCopy : IDisposable
    {
        private readonly ClickHouseConnection connection;
        private long rowsWritten = 0;

        public ClickHouseBulkCopy(ClickHouseConnection connection)
        {
            this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        public int BatchSize { get; set; } = 50000;

        public int MaxDegreeOfParallelism { get; set; } = 4;

        public string DestinationTableName { get; set; }

        public long RowsWritten => rowsWritten;

        public Task WriteToServerAsync(IDataReader reader) => WriteToServerAsync(reader, CancellationToken.None);

        public Task WriteToServerAsync(IDataReader reader, CancellationToken token)
        {
            if (reader is null)
                throw new ArgumentNullException(nameof(reader));

            return WriteToServerAsync(AsEnumerable(reader), token);
        }

        public Task WriteToServerAsync(IEnumerable<object[]> rows) => WriteToServerAsync(rows, CancellationToken.None);

        public async Task WriteToServerAsync(IEnumerable<object[]> rows, CancellationToken token)
        {
            if (rows is null)
                throw new ArgumentNullException(nameof(rows));
            if (string.IsNullOrWhiteSpace(DestinationTableName))
                throw new InvalidOperationException(Resources.DestinationTableNotSetMessage);

            var tableColumns = await GetTargetTableSchemaAsync(token);

            var tasks = new Task[MaxDegreeOfParallelism];
            for (int i = 0; i < tasks.Length; i++)
                tasks[i] = Task.CompletedTask;
           
            foreach (var batch in rows.Batch(BatchSize))
            {
                while (true)
                {
                    var completedTaskIndex = Array.FindIndex(tasks, t => t == null || t.Status == TaskStatus.RanToCompletion);
                    if (completedTaskIndex >= 0)
                    {
                        var task = PushBatch(batch, tableColumns, token);
                        tasks[completedTaskIndex] = task;
                        break;
                    }
                    else
                    {
                        await Task.WhenAny(tasks);
                    }
                }
            }
            await Task.WhenAll(tasks);
        }

        private static IEnumerable<object[]> AsEnumerable(IDataReader reader)
        {
            while (reader.Read())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                yield return values;
            }
        }

        private async Task PushBatch(ICollection<object[]> rows, ClickHouseType[] columnTypes, CancellationToken token)
        {
            using var stream = new MemoryStream() { Capacity = 256 * 1024 };
            using var writer = new ExtendedBinaryWriter(stream);
            using var streamer = new BinaryStreamWriter(writer);
            foreach (var row in rows)
            {
                for (var i = 0; i < row.Length; i++)
                {
                    streamer.WriteValue(row[i], columnTypes[i]);
                }
            }
            stream.Seek(0, SeekOrigin.Begin);

            var query = $"INSERT INTO {DestinationTableName} FORMAT RowBinary";
            var result = await connection.PostDataAsync(query, stream, token).ConfigureAwait(false);
            Interlocked.Add(ref rowsWritten, rows.Count);
        }

        private async Task<ClickHouseType[]> GetTargetTableSchemaAsync(CancellationToken token)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {DestinationTableName}";
            using var reader = (ClickHouseDataReader)await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, token).ConfigureAwait(false);
            return Enumerable.Range(0, reader.FieldCount).Select(reader.GetClickHouseType).ToArray();
        }

        private bool disposed = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposed && disposing)
            {
                connection?.Dispose();
                disposed = true;
            }
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
