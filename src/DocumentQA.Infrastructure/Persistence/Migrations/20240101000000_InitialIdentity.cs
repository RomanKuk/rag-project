using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DocumentQA.Infrastructure.Persistence.Migrations;

[Migration("20240101000000_InitialIdentity")]
public partial class InitialIdentity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Tenants",
            columns: table => new
            {
                Id        = table.Column<Guid>(nullable: false),
                Name      = table.Column<string>(maxLength: 200, nullable: false),
                Slug      = table.Column<string>(maxLength: 100, nullable: false),
                IsActive  = table.Column<bool>(nullable: false, defaultValue: true),
                CreatedAt = table.Column<DateTime>(nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_Tenants", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_Tenants_Slug",
            table: "Tenants",
            column: "Slug",
            unique: true);

        migrationBuilder.CreateTable(
            name: "Users",
            columns: table => new
            {
                Id           = table.Column<Guid>(nullable: false),
                TenantId     = table.Column<Guid>(nullable: true),
                Email        = table.Column<string>(maxLength: 320, nullable: false),
                PasswordHash = table.Column<string>(nullable: false),
                Role         = table.Column<string>(nullable: false),
                DisplayName  = table.Column<string>(maxLength: 200, nullable: false),
                IsActive     = table.Column<bool>(nullable: false, defaultValue: true),
                CreatedAt    = table.Column<DateTime>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Users", x => x.Id);
                table.ForeignKey(
                    name: "FK_Users_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Users_Email",
            table: "Users",
            column: "Email",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Users_TenantId",
            table: "Users",
            column: "TenantId");

        migrationBuilder.CreateTable(
            name: "UsageLogs",
            columns: table => new
            {
                Id           = table.Column<long>(nullable: false)
                                   .Annotation("Npgsql:ValueGenerationStrategy",
                                       NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                RequestId    = table.Column<string>(nullable: false),
                ApiKey       = table.Column<string>(nullable: false),
                TenantId     = table.Column<string>(maxLength: 100, nullable: false, defaultValue: "public"),
                UserId       = table.Column<Guid>(nullable: true),
                Model        = table.Column<string>(nullable: false),
                InputTokens  = table.Column<int>(nullable: false),
                OutputTokens = table.Column<int>(nullable: false),
                CostUsd      = table.Column<decimal>(nullable: false),
                LatencyMs    = table.Column<int>(nullable: false),
                TtftMs       = table.Column<int>(nullable: true),
                CacheHit     = table.Column<bool>(nullable: false),
                FallbackUsed = table.Column<bool>(nullable: false),
                CreatedAt    = table.Column<DateTime>(nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_UsageLogs", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_UsageLogs_TenantId",
            table: "UsageLogs",
            column: "TenantId");

        migrationBuilder.CreateIndex(
            name: "IX_UsageLogs_CreatedAt",
            table: "UsageLogs",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_UsageLogs_TenantId_CreatedAt",
            table: "UsageLogs",
            columns: new[] { "TenantId", "CreatedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "UsageLogs");
        migrationBuilder.DropTable(name: "Users");
        migrationBuilder.DropTable(name: "Tenants");
    }
}
