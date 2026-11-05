using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CohortCompanion.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class CascadeOwnedContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LiveSessions_AspNetUsers_HostId",
                table: "LiveSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_Presentations_AspNetUsers_OwnerId",
                table: "Presentations");

            migrationBuilder.DropForeignKey(
                name: "FK_Quizzes_AspNetUsers_OwnerId",
                table: "Quizzes");

            migrationBuilder.AddForeignKey(
                name: "FK_LiveSessions_AspNetUsers_HostId",
                table: "LiveSessions",
                column: "HostId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Presentations_AspNetUsers_OwnerId",
                table: "Presentations",
                column: "OwnerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Quizzes_AspNetUsers_OwnerId",
                table: "Quizzes",
                column: "OwnerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LiveSessions_AspNetUsers_HostId",
                table: "LiveSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_Presentations_AspNetUsers_OwnerId",
                table: "Presentations");

            migrationBuilder.DropForeignKey(
                name: "FK_Quizzes_AspNetUsers_OwnerId",
                table: "Quizzes");

            migrationBuilder.AddForeignKey(
                name: "FK_LiveSessions_AspNetUsers_HostId",
                table: "LiveSessions",
                column: "HostId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Presentations_AspNetUsers_OwnerId",
                table: "Presentations",
                column: "OwnerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Quizzes_AspNetUsers_OwnerId",
                table: "Quizzes",
                column: "OwnerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
