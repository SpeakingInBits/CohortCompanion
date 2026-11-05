using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CohortCompanion.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class LiveSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LiveSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    QuizId = table.Column<int>(type: "int", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    HostId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    JoinCode = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CurrentIndex = table.Column<int>(type: "int", nullable: false),
                    Phase = table.Column<int>(type: "int", nullable: false),
                    PhaseDeadlineUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastActivityUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RuntimeJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiveSessions_AspNetUsers_HostId",
                        column: x => x.HostId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Participants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LiveSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nickname = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    JoinedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsRemoved = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Participants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Participants_LiveSessions_LiveSessionId",
                        column: x => x.LiveSessionId,
                        principalTable: "LiveSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SessionSummaries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LiveSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuestionIndex = table.Column<int>(type: "int", nullable: false),
                    PromptText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ParticipantCount = table.Column<int>(type: "int", nullable: false),
                    AggregateJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionSummaries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionSummaries_LiveSessions_LiveSessionId",
                        column: x => x.LiveSessionId,
                        principalTable: "LiveSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Responses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuestionId = table.Column<int>(type: "int", nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SubmittedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ElapsedMs = table.Column<int>(type: "int", nullable: false),
                    IsCorrect = table.Column<bool>(type: "bit", nullable: true),
                    Score = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Responses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Responses_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LiveSessions_HostId_Status",
                table: "LiveSessions",
                columns: new[] { "HostId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveSessions_JoinCode",
                table: "LiveSessions",
                column: "JoinCode",
                unique: true,
                filter: "[Status] <> 2");

            migrationBuilder.CreateIndex(
                name: "IX_Participants_LiveSessionId",
                table: "Participants",
                column: "LiveSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Participants_TokenHash",
                table: "Participants",
                column: "TokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_Responses_ParticipantId_QuestionId",
                table: "Responses",
                columns: new[] { "ParticipantId", "QuestionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SessionSummaries_LiveSessionId",
                table: "SessionSummaries",
                column: "LiveSessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Responses");

            migrationBuilder.DropTable(
                name: "SessionSummaries");

            migrationBuilder.DropTable(
                name: "Participants");

            migrationBuilder.DropTable(
                name: "LiveSessions");
        }
    }
}
