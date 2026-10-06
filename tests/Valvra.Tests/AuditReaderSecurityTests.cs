using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json.Nodes;
using Valvra.Core;
using Valvra.Infrastructure.Auditing;
using Xunit;

namespace Valvra.Tests;

public sealed class AuditReaderSecurityTests
{
    private static readonly Guid Installation = Guid.NewGuid();
    private static readonly Guid Resource = Guid.NewGuid();
    private static readonly Guid Group = Guid.NewGuid();
    private static readonly DateTimeOffset Time = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
    private static AuditEvent Event(string name = "Resource", int minute = 0) => new(Guid.NewGuid(), Guid.NewGuid(), Time.AddMinutes(minute),
        "ad", "user", "Secret.Reveal", Resource, AuditPhase.Event, "ReleaseAuthorized", "test", InstallationId: Installation,
        Format: 3, Scope: new(Resource, name, Group, [new(Group, "Group")]));

    private static DataTable Table()
    {
        var table = new DataTable();
        table.Columns.Add("EventJson", typeof(string)); table.Columns.Add("PayloadHash", typeof(string));
        table.Columns.Add("SigningKeyId", typeof(string)); table.Columns.Add("Signature", typeof(byte[]));
        table.Columns.Add("Timestamp", typeof(DateTime)); table.Columns.Add("EventId", typeof(Guid));
        return table;
    }
    private static void Add(DataTable table, SignedAuditEvent signed, string? json = null, DateTimeOffset? projectedTime = null) =>
        table.Rows.Add(json ?? Encoding.UTF8.GetString(signed.Payload), signed.PayloadHash, signed.SigningKeyId, signed.Signature,
            (projectedTime ?? signed.Event.Timestamp).UtcDateTime, signed.Event.Id);
    private static AuditReader Reader(TestAuditSigner signer, int maximum = 100000, long bytes = 64 * 1024 * 1024) =>
        new(new() { InstallationId = Installation, MaximumReadEvents = maximum, MaximumReadBytes = bytes }, signer);

    // DataTableReader.GetChars requires a char[] column even though GetString requires
    // a string column. Adapt only that incompatibility to the providers' text-reader contract.
    private sealed class TextReader(DbDataReader inner) : DbDataReader
    {
        public override long GetChars(int ordinal, long offset, char[]? buffer, int bufferOffset, int length)
        {
            var value = inner.GetString(ordinal);
            if (buffer is null) return value.Length;
            var copied = Math.Min(length, value.Length - checked((int)offset));
            value.CopyTo((int)offset, buffer, bufferOffset, copied); return copied;
        }
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override bool Read() => inner.Read();
        public override bool NextResult() => inner.NextResult();
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override long GetBytes(int ordinal, long offset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, offset, buffer, bufferOffset, length);
        public override System.Collections.IEnumerator GetEnumerator() => ((System.Collections.IEnumerable)inner).GetEnumerator();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    [Fact]
    public async Task InvalidRepresentativeCannotHideGenuineHistoryOrLatestSignedName()
    {
        using var signer = new TestAuditSigner(); using var table = Table();
        var old = signer.Sign(Event("Old")); var latest = signer.Sign(Event("New", 1));
        Add(table, old, projectedTime: Time.AddYears(10)); Add(table, latest, projectedTime: Time.AddYears(-10));
        var forged = JsonNode.Parse(Encoding.UTF8.GetString(latest.Payload))!;
        forged["Id"] = Guid.NewGuid().ToString(); // Same exact Scope, invalid signature.
        Add(table, latest, forged.ToJsonString(), Time.AddYears(20));
        using var data = new StringColumnReader(table.CreateDataReader()); var rows = await Reader(signer).ReadRowsAsync(data, default);
        var targets = AuditReader.BuildTargets(rows);
        Assert.Equal("New", Assert.Single(targets.Resources).Name); Assert.Single(targets.Groups);
        Assert.Equal(1, targets.InvalidEventCount); Assert.Equal(2, rows.Count(x => x.SignatureValid));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"Scope\":17}")]
    [InlineData("{\"Format\":3,\"Scope\":{\"GroupPath\":null}}")]
    [InlineData("{\"Format\":3,\"Scope\":{\"GroupPath\":{}}}")]
    public async Task CorruptPayloadIsMarkedWithoutBlockingValidRows(string malformed)
    {
        using var signer = new TestAuditSigner(); using var table = Table(); var signed = signer.Sign(Event());
        Add(table, signed, malformed); Add(table, signed);
        using var data = new StringColumnReader(table.CreateDataReader()); var rows = await Reader(signer).ReadRowsAsync(data, default);
        var invalid = Assert.Single(rows, x => !x.SignatureValid);
        Assert.Equal("Audit.InvalidPayload", invalid.Event.Action); Assert.Null(invalid.Event.Scope);
        Assert.Equal(signed.Event.Id, invalid.Event.Id); Assert.Single(rows, x => x.SignatureValid);
        Assert.Equal(1, AuditReader.BuildTargets(rows).InvalidEventCount);
        Assert.Contains(AuditReader.SelectPage(rows, new(null, null, "different-actor", null, null, ResourceId: Guid.NewGuid())), x => !x.SignatureValid);
    }

    [Fact]
    public async Task FilteringSortingAndPagingUseVerifiedPayloadBeforePagination()
    {
        using var signer = new TestAuditSigner(); using var table = Table();
        for (var minute = 0; minute < 205; minute++)
            Add(table, signer.Sign(Event(minute: minute)), projectedTime: Time.AddMinutes(205 - minute));
        Add(table, signer.Sign(Event(minute: 300) with { ActorId = "other" }));
        using var data = new StringColumnReader(table.CreateDataReader()); var rows = await Reader(signer).ReadRowsAsync(data, default);
        var query = new AuditQuery(Time, null, "user", "Secret.Reveal", Resource, ResourceId: Resource, GroupId: Group);
        var first = AuditReader.SelectPage(rows, query); var next = AuditReader.SelectPage(rows, query with { Offset = 200 });
        Assert.Equal(200, first.Count); Assert.Equal(5, next.Count);
        Assert.Equal(Time.AddMinutes(204), first[0].Event.Timestamp); Assert.Equal(Time, next[^1].Event.Timestamp);
        Assert.Empty(AuditReader.SelectPage(rows, query with { GroupId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task ScanLimitsRejectPartialResultsAndCancellationIsNotHidden()
    {
        using var signer = new TestAuditSigner(); using var table = Table(); var signed = signer.Sign(Event());
        Add(table, signed); Add(table, signed);
        using var byCount = new StringColumnReader(table.CreateDataReader());
        await Assert.ThrowsAsync<VaultUnavailableException>(() => Reader(signer, maximum: 1).ReadRowsAsync(byCount, default));
        using var bySize = new StringColumnReader(table.CreateDataReader());
        await Assert.ThrowsAsync<VaultUnavailableException>(() => Reader(signer, bytes: 1).ReadRowsAsync(bySize, default));
        using var canceled = new StringColumnReader(table.CreateDataReader()); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader(signer).ReadRowsAsync(canceled, cancellation.Token));
    }

    [Fact]
    public async Task OversizedPayloadIsMarkedWithoutParsingAndRealReaderRequiresAuditor()
    {
        using var signer = new TestAuditSigner(); using var table = Table(); var signed = signer.Sign(Event());
        Add(table, signed, new string('x', 262145)); Add(table, signed);
        var reader = Reader(signer);
        using var data = new TextReader(table.CreateDataReader()); var rows = await reader.ReadRowsAsync(data, default);
        Assert.Equal(1, AuditReader.BuildTargets(rows).InvalidEventCount); Assert.Single(rows, x => x.SignatureValid);
        foreach (var actor in new[] {
            new Actor("ad", "user", "User", new HashSet<string>(), true, true, false),
            new Actor("ad", "user", "User", new HashSet<string>(), false, true, true) })
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => reader.ReadTargetsAsync(actor, default));
            await Assert.ThrowsAsync<AccessDeniedException>(() => reader.ReadAsync(actor, new(null, null, null, null, null), default));
        }
    }
}
