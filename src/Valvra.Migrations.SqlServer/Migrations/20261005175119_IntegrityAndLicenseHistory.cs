using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Valvra.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class IntegrityAndLicenseHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EventJson",
                table: "Audit",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "InstallationId",
                table: "Audit",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "PayloadHash",
                table: "Audit",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<byte[]>(
                name: "Signature",
                table: "Audit",
                type: "varbinary(max)",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<string>(
                name: "SigningKeyId",
                table: "Audit",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "LicenseVersions",
                columns: table => new
                {
                    LicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    EnvelopeJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LicenseVersions", x => new { x.LicenseId, x.Version });
                    table.ForeignKey(
                        name: "FK_LicenseVersions_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LicenseVersions");

            migrationBuilder.DropColumn(
                name: "EventJson",
                table: "Audit");

            migrationBuilder.DropColumn(
                name: "InstallationId",
                table: "Audit");

            migrationBuilder.DropColumn(
                name: "PayloadHash",
                table: "Audit");

            migrationBuilder.DropColumn(
                name: "Signature",
                table: "Audit");

            migrationBuilder.DropColumn(
                name: "SigningKeyId",
                table: "Audit");
        }
    }
}
