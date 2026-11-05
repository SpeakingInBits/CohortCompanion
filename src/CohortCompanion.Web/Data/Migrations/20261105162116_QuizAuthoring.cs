using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CohortCompanion.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class QuizAuthoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Quizzes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OwnerId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Mode = table.Column<int>(type: "int", nullable: false),
                    ScoringEnabled = table.Column<bool>(type: "bit", nullable: false),
                    SpeedBonusEnabled = table.Column<bool>(type: "bit", nullable: false),
                    ShowLeaderboard = table.Column<bool>(type: "bit", nullable: false),
                    DefaultTimeLimitSeconds = table.Column<int>(type: "int", nullable: true),
                    ResultsDisplaySeconds = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Quizzes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Quizzes_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Questions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QuizId = table.Column<int>(type: "int", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Prompt = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CodeSnippet = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CodeLanguage = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ImageUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TimeLimitSeconds = table.Column<int>(type: "int", nullable: true),
                    Points = table.Column<int>(type: "int", nullable: false),
                    AllowMultipleSelect = table.Column<bool>(type: "bit", nullable: false),
                    PartialCredit = table.Column<bool>(type: "bit", nullable: false),
                    MaxEntries = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Questions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Questions_Quizzes_QuizId",
                        column: x => x.QuizId,
                        principalTable: "Quizzes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BlankAnswer",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QuestionId = table.Column<int>(type: "int", nullable: false),
                    BlankIndex = table.Column<int>(type: "int", nullable: false),
                    AcceptedText = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MatchMode = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlankAnswer", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BlankAnswer_Questions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "Questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChoiceOption",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QuestionId = table.Column<int>(type: "int", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    IsCorrect = table.Column<bool>(type: "bit", nullable: false),
                    CodeSnippet = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChoiceOption", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChoiceOption_Questions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "Questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BlankAnswer_QuestionId",
                table: "BlankAnswer",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_ChoiceOption_QuestionId",
                table: "ChoiceOption",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_Questions_QuizId",
                table: "Questions",
                column: "QuizId");

            migrationBuilder.CreateIndex(
                name: "IX_Quizzes_OwnerId",
                table: "Quizzes",
                column: "OwnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlankAnswer");

            migrationBuilder.DropTable(
                name: "ChoiceOption");

            migrationBuilder.DropTable(
                name: "Questions");

            migrationBuilder.DropTable(
                name: "Quizzes");
        }
    }
}
