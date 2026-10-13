using Valvra.Core;
using System.DirectoryServices.Protocols;
using Valvra.Infrastructure.Authorization;
using Valvra.Infrastructure.Services;
using Xunit;

namespace Valvra.Tests;

public sealed class AccessPresentationTests
{
    private sealed class Directory : IDirectoryProvider
    {
        public string ProviderId => "ad";
        public int Calls { get; private set; }
        public bool Unavailable { get; set; }
        public bool Mismatch { get; set; }
        public Exception? Failure { get; set; }
        public Task<Actor> ResolveAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DirectorySubject>> SearchAsync(string query, SubjectKind kind, CancellationToken ct) => throw new NotSupportedException();
        public Task<DirectorySubject?> FindAsync(string id, SubjectKind kind, CancellationToken ct)
        {
            Calls++; ct.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            if (Unavailable) throw new VaultUnavailableException("Directory unavailable");
            return Task.FromResult<DirectorySubject?>(new("ad", Mismatch ? "different-id" : id, "Example Person", kind));
        }
    }
    private static VaultService Service(VaultServiceTests.Fixture f, IDirectoryProvider directory) => new(
        new VaultOperations(f.Db, new AccessService(f.Db, f.Clock), f.Audit, f.Cipher, f.Clock, directory, f.Integrity), f.Integrity);

    [Fact]
    public async Task NamesAreDisplayOnlyAndDuplicateIdentitiesAreResolvedOnce()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        await f.Vault.AddOwnerAsync(f.User, new ResourceOwner { TargetKind=TargetKind.Resource, TargetId=f.ResourceId,
            Provider="ad", SubjectId="user", SubjectKind=SubjectKind.User }, "test", default);
        var directory = new Directory();
        var view = await Service(f, directory).AccessAsync(f.User, TargetKind.Resource, f.ResourceId, default);
        var subject = Assert.Single(view.Subjects!);
        Assert.Equal("Example Person", subject.Name); Assert.Equal("user", subject.Id);
        Assert.Equal("user", Assert.Single(view.Grants).SubjectId); Assert.Equal(1, directory.Calls);
        Assert.Equal(VaultPermission.Metadata | VaultPermission.ReadSecret | VaultPermission.Modify, view.EffectivePermissions);
        Assert.Equal(0, f.Cipher.Decryptions);
    }
    [Fact]
    public async Task UnauthorizedCallDoesNotResolveNames()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync(); var directory = new Directory();
        var ordinary = f.User with { IsAccessAdministrator=false, SubjectId="outsider" };
        await Assert.ThrowsAsync<AccessDeniedException>(() => Service(f, directory).AccessAsync(ordinary, TargetKind.Resource, f.ResourceId, default));
        Assert.Equal(0, directory.Calls);
    }
    [Fact]
    public async Task DirectoryOutageKeepsOriginalAssignmentsAvailable()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync(); var directory = new Directory { Unavailable=true };
        var view = await Service(f, directory).AccessAsync(f.User, TargetKind.Resource, f.ResourceId, default);
        Assert.Empty(view.Subjects!); Assert.Equal("user", Assert.Single(view.Grants).SubjectId);
    }
    [Fact]
    public async Task MismatchedResolvedIdentityFailsClosed()
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync(); var directory = new Directory { Mismatch=true };
        await Assert.ThrowsAsync<AccessDeniedException>(() => Service(f, directory).AccessAsync(f.User, TargetKind.Resource, f.ResourceId, default));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeLdapTransportFailuresKeepAssignmentsAvailable(bool bindFailure)
    {
        await using var f = await VaultServiceTests.Fixture.CreateAsync();
        var directory = new Directory { Failure=bindFailure ? new LdapException() : new DirectoryOperationException() };
        var view = await Service(f, directory).AccessAsync(f.User, TargetKind.Resource, f.ResourceId, default);
        Assert.Empty(view.Subjects!); Assert.Equal("user", Assert.Single(view.Grants).SubjectId);
    }
}
