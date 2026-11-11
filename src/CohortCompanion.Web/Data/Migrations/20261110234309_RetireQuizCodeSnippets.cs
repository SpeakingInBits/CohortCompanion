using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CohortCompanion.Web.Data.Migrations
{
    /// <summary>
    /// Quiz questions and answer options no longer have a code snippet. Any snippets already saved go with the columns.
    /// </summary>
    public partial class RetireQuizCodeSnippets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CodeLanguage",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "CodeSnippet",
                table: "Questions");

            migrationBuilder.DropColumn(
                name: "CodeSnippet",
                table: "ChoiceOption");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CodeLanguage",
                table: "Questions",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CodeSnippet",
                table: "Questions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CodeSnippet",
                table: "ChoiceOption",
                type: "nvarchar(max)",
                nullable: true);
        }
    }
}
