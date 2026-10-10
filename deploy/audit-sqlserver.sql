-- Run using a provisioning/DBA identity in a SEPARATE audit database.
-- Replace the example principals with identities provisioned by your DBA.
CREATE TABLE dbo.AuditEvents (
    EventId uniqueidentifier NOT NULL,
    PayloadHash char(64) NOT NULL,
    Timestamp datetime2(7) NOT NULL,
    ActorId nvarchar(512) NOT NULL,
    Action nvarchar(512) NOT NULL,
    TargetId uniqueidentifier NULL,
    EventJson nvarchar(max) NOT NULL,
    SigningKeyId varchar(128) NOT NULL,
    Signature varbinary(1024) NOT NULL,
    CONSTRAINT PK_AuditEvents PRIMARY KEY (EventId, PayloadHash)
);
CREATE INDEX IX_AuditEvents_Timestamp ON dbo.AuditEvents(Timestamp DESC);
CREATE INDEX IX_AuditEvents_Actor ON dbo.AuditEvents(ActorId, Timestamp DESC);
CREATE INDEX IX_AuditEvents_Target ON dbo.AuditEvents(TargetId, Timestamp DESC);
GO
CREATE ROLE valvra_audit_runtime;
GRANT SELECT, INSERT ON OBJECT::dbo.AuditEvents TO valvra_audit_runtime;
DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP ON OBJECT::dbo.AuditEvents TO valvra_audit_runtime;
DENY ALTER, TAKE OWNERSHIP ON SCHEMA::dbo TO valvra_audit_runtime;
-- Do not grant CONTROL: DENY CONTROL would also block required SELECT/INSERT.
-- Optional separate identities. Do not combine these roles with the runtime role:
-- their DENY SELECT/INSERT permissions would override its grants.
CREATE ROLE valvra_audit_writer;
GRANT INSERT ON OBJECT::dbo.AuditEvents TO valvra_audit_writer;
DENY SELECT, UPDATE, DELETE ON OBJECT::dbo.AuditEvents TO valvra_audit_writer;
CREATE ROLE valvra_audit_reader;
GRANT SELECT ON OBJECT::dbo.AuditEvents TO valvra_audit_reader;
DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.AuditEvents TO valvra_audit_reader;
GO
-- Example, after users have been created:
-- ALTER ROLE valvra_audit_runtime ADD MEMBER [EXAMPLE\svcValvra$];
-- ALTER ROLE valvra_audit_writer ADD MEMBER [ValvraAuditWriter];
-- ALTER ROLE valvra_audit_reader ADD MEMBER [ValvraAuditReader];
-- Neither runtime identity may be db_owner, sysadmin, dbo, or own this table.
