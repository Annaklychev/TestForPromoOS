    using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TestForPromoOS.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // xmin is a PostgreSQL system column on every table — no DDL needed.
            // The shadow property in AppDbContext maps to it so EF emits
            // "WHERE xmin = @p" on UPDATE for optimistic concurrency.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
