using System.DirectoryServices.Protocols;
using Valvra.Core;
using Valvra.Infrastructure.Auditing;
using Valvra.Web;
using Xunit;

namespace Valvra.Tests;
public sealed class AuditPresentationTests
{
    private sealed class Directory : IDirectoryProvider
    {
        public string ProviderId=>"ad";public int Calls;public Exception? Failure;public bool Mismatch;
        public Task<Actor> ResolveAsync(string id,CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<DirectorySubject>> SearchAsync(string query,SubjectKind kind,CancellationToken ct)=>throw new NotSupportedException();
        public Task<DirectorySubject?> FindAsync(string id,SubjectKind kind,CancellationToken ct){Calls++;ct.ThrowIfCancellationRequested();if(Failure is not null)throw Failure;return Task.FromResult<DirectorySubject?>(new("ad",Mismatch?"wrong":id,"Example Person",kind));}
    }
    private static readonly Actor Auditor=new("ad","user","Auditor",new HashSet<string>(),true,false,true);
    private static readonly AuditTargetOptions Targets=new([],[],Actors:[new("ad","recorded"),new("other","recorded")]);
    [Fact] public async Task NamesOnlyResolveRecordedActorsInTheirProviderAndRetainIds(){var directory=new Directory();var result=await AuditPresentation.NamesAsync(Auditor,Targets,directory,default);Assert.Equal(1,directory.Calls);Assert.Equal("Example Person",result.Actors![0].Name);Assert.Equal("recorded",result.Actors[0].Id);Assert.Null(result.Actors[1].Name);}
    [Fact] public async Task NonAuditorCannotTriggerLookup(){var directory=new Directory();await Assert.ThrowsAsync<AccessDeniedException>(()=>AuditPresentation.NamesAsync(Auditor with{IsAuditor=false},Targets,directory,default));Assert.Equal(0,directory.Calls);}
    [Fact] public async Task MismatchedIdentityFailsClosed(){await Assert.ThrowsAsync<AccessDeniedException>(()=>AuditPresentation.NamesAsync(Auditor,Targets,new Directory{Mismatch=true},default));}
    [Fact] public async Task DirectoryFailurePreservesRecordedActors(){var result=await AuditPresentation.NamesAsync(Auditor,Targets,new Directory{Failure=new LdapException()},default);Assert.Equal(Targets.Actors,result.Actors);}
    [Fact] public async Task CancellationPropagates(){using var cts=new CancellationTokenSource();cts.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>AuditPresentation.NamesAsync(Auditor,Targets,new Directory(),cts.Token));}
}
