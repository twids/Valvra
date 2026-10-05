using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Valvra.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Timestamp = table.Column<long>(type: "bigint", nullable: false),
                    ActorProvider = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Phase = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    DetailsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: false),
                    Delivered = table.Column<bool>(type: "bit", nullable: false),
                    ReceiptHash = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Audit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetKind = table.Column<int>(type: "int", nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectKind = table.Column<int>(type: "int", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    SubjectId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Permissions = table.Column<int>(type: "int", nullable: false),
                    StartsAt = table.Column<long>(type: "bigint", nullable: true),
                    ExpiresAt = table.Column<long>(type: "bigint", nullable: true),
                    GrantedBy = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Grants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Groups_Groups_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Owners",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetKind = table.Column<int>(type: "int", nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    SubjectId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    SubjectKind = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Owners", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Resources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Resources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Resources_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Licenses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Product = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Vendor = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    PurchaseReference = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Seats = table.Column<int>(type: "int", nullable: false),
                    ExpiresAt = table.Column<long>(type: "bigint", nullable: true),
                    EnvelopeJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SecretVersion = table.Column<int>(type: "int", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Deleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Licenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Licenses_Resources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "Resources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Secrets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    CurrentVersion = table.Column<int>(type: "int", nullable: false),
                    LdapProfileId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Deleted = table.Column<bool>(type: "bit", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Secrets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Secrets_Resources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "Resources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserProvider = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    UserId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Seats = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Assignments_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Assignments_Resources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "Resources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SecretVersions",
                columns: table => new
                {
                    EntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    EnvelopeJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecretVersions", x => new { x.EntryId, x.Version });
                    table.ForeignKey(
                        name: "FK_SecretVersions_Secrets_EntryId",
                        column: x => x.EntryId,
                        principalTable: "Secrets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_LicenseId",
                table: "Assignments",
                column: "LicenseId");

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_ResourceId",
                table: "Assignments",
                column: "ResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_Audit_Delivered_Timestamp",
                table: "Audit",
                columns: new[] { "Delivered", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Audit_OperationId",
                table: "Audit",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_Audit_Timestamp",
                table: "Audit",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_Grants_TargetKind_TargetId",
                table: "Grants",
                columns: new[] { "TargetKind", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_Groups_ParentId",
                table: "Groups",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_ResourceId",
                table: "Licenses",
                column: "ResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_Owners_TargetKind_TargetId",
                table: "Owners",
                columns: new[] { "TargetKind", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_Resources_GroupId",
                table: "Resources",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Secrets_ResourceId",
                table: "Secrets",
                column: "ResourceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Assignments");

            migrationBuilder.DropTable(
                name: "Audit");

            migrationBuilder.DropTable(
                name: "Grants");

            migrationBuilder.DropTable(
                name: "Owners");

            migrationBuilder.DropTable(
                name: "SecretVersions");

            migrationBuilder.DropTable(
                name: "Licenses");

            migrationBuilder.DropTable(
                name: "Secrets");

            migrationBuilder.DropTable(
                name: "Resources");

            migrationBuilder.DropTable(
                name: "Groups");
        }
    }
}
