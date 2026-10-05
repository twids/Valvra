namespace Valvra.Core;

[Flags]
public enum VaultPermission
{
    None = 0, Metadata = 1, ReadSecret = 2, Modify = 4, ManageAccess = 8, TestCredential = 16
}

public enum TargetKind { Group, Resource }
public enum SubjectKind { User, Group }
public enum AuditPhase { Event, Intent, Committed, Failed }

public sealed class ResourceGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = "";
    public long Revision { get; set; }
}

public sealed class VaultResource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public string Name { get; set; } = "";
    public long Revision { get; set; }
}

public sealed class AccessGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public TargetKind TargetKind { get; set; }
    public Guid TargetId { get; set; }
    public SubjectKind SubjectKind { get; set; }
    public string Provider { get; set; } = "ad";
    public string SubjectId { get; set; } = "";
    public VaultPermission Permissions { get; set; }
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string GrantedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class ResourceOwner
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public TargetKind TargetKind { get; set; }
    public Guid TargetId { get; set; }
    public string Provider { get; set; } = "ad";
    public string SubjectId { get; set; } = "";
    public SubjectKind SubjectKind { get; set; }
}

public sealed class SecretEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ResourceId { get; set; }
    public string Title { get; set; } = "";
    public int CurrentVersion { get; set; }
    public string? LdapProfileId { get; set; }
    public bool Deleted { get; set; }
    public long Revision { get; set; }
}

public sealed class SecretVersion
{
    public Guid EntryId { get; set; }
    public int Version { get; set; }
    public string EnvelopeJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
}

public sealed class SoftwareLicense
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ResourceId { get; set; }
    public string Product { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string PurchaseReference { get; set; } = "";
    public int Seats { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string EnvelopeJson { get; set; } = "";
    public int SecretVersion { get; set; }
    public long Revision { get; set; }
    public bool Deleted { get; set; }
}

public sealed class LicenseAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LicenseId { get; set; }
    public string? UserProvider { get; set; }
    public string? UserId { get; set; }
    public Guid? ResourceId { get; set; }
    public int Seats { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AuditRecord
{
    public Guid Id { get; set; }
    public Guid OperationId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string ActorProvider { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string Action { get; set; } = "";
    public Guid? TargetId { get; set; }
    public AuditPhase Phase { get; set; }
    public string Outcome { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public string DetailsJson { get; set; } = "{}";
    public bool Delivered { get; set; }
    public string? ReceiptHash { get; set; }
}
