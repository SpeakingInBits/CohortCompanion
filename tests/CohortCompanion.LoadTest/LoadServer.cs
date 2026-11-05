using System.Data.Common;
using System.Diagnostics;
using CohortCompanion.Shared;
using CohortCompanion.Web.Data;
using CohortCompanion.Web.Live;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace CohortCompanion.LoadTest;

/// <summary>
/// Runs the real app with control endpoints added on top. Instructor commands normally come from the host
/// page, and these endpoints stand in for it. They exist only in this test host, never in the app itself.
/// </summary>
internal static class LoadServer
{
    public const string ModeArgument = "server";

    /// <summary>A database of its own, deleted at the start of every run. Never point this at real data.</summary>
    private const string ConnectionString =
        @"Server=(localdb)\mssqllocaldb;Database=CohortCompanionLoadTest;Trusted_Connection=True;MultipleActiveResultSets=true";

    public static async Task<int> RunAsync(string[] args)
    {
        var port = int.Parse(args[1]);
        var coreMask = long.Parse(args[2]);
        var databaseDelayMs = int.Parse(args[3]);
        Process.GetCurrentProcess().ProcessorAffinity = new IntPtr(coreMask);

        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(ConnectionString).Options;
        await using (var db = new ApplicationDbContext(options))
        {
            await db.Database.EnsureDeletedAsync();
        }

        var stopped = new TaskCompletionSource();
        using var sampler = new ResourceSampler();
        await using var factory = new LoadServerFactory(sampler, stopped, databaseDelayMs);
        factory.UseKestrel(port);
        factory.StartServer();

        await stopped.Task;
        return 0;
    }

    private sealed class LoadServerFactory(ResourceSampler sampler, TaskCompletionSource stopped, int databaseDelayMs) : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:DefaultConnection", ConnectionString);
            builder.UseSetting("Database:MigrateOnStartup", "true");
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new ControlEndpoints(sampler, stopped));
                if (databaseDelayMs > 0)
                {
                    services.ConfigureDbContext<ApplicationDbContext>(options => options.AddInterceptors(new SlowDatabase(databaseDelayMs)));
                }
            });
        }
    }

    private sealed class ControlEndpoints(ResourceSampler sampler, TaskCompletionSource stopped) : IStartupFilter
    {
        private string? hostId;

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Path.StartsWithSegments("/__load", out var command))
                {
                    await HandleAsync(context, command.Value!.TrimStart('/'));
                }
                else
                {
                    await nextMiddleware();
                }
            });

            next(app);
        };

        private async Task HandleAsync(HttpContext context, string command)
        {
            var services = context.RequestServices;
            var manager = services.GetRequiredService<LiveSessionManager>();

            if (command == "create")
            {
                hostId ??= await CreateHostAsync(services);
                var quiz = BuildQuiz(hostId, int.Parse(context.Request.Query["questions"]!));
                var created = (await manager.CreateQuizSessionAsync(quiz, hostId))!.Value;
                var view = (await manager.GetHostViewAsync(created, hostId))!;
                await context.Response.WriteAsJsonAsync(new SessionCreated(created, view.JoinCode));
                return;
            }

            if (command == "stats")
            {
                await context.Response.WriteAsJsonAsync(sampler.Read());
                return;
            }

            if (command == "reset-stats")
            {
                sampler.Reset();
                return;
            }

            if (command == "shutdown")
            {
                stopped.TrySetResult();
                return;
            }

            var id = Guid.Parse(context.Request.Query["id"]!);
            var ok = await (command switch
            {
                "start" => manager.StartAsync(id, hostId!),
                "open" => manager.OpenQuestionAsync(id, hostId!),
                "close" => manager.CloseQuestionAsync(id, hostId!),
                "show" => manager.ShowResultsAsync(id, hostId!),
                "reveal" => manager.RevealAnswerAsync(id, hostId!),
                "next" => manager.NextAsync(id, hostId!),
                "end" => manager.EndAsync(id, hostId!),
                _ => throw new InvalidOperationException($"Unknown command {command}."),
            });

            context.Response.StatusCode = ok ? StatusCodes.Status200OK : StatusCodes.Status409Conflict;
        }

        private static async Task<string> CreateHostAsync(IServiceProvider services)
        {
            var factory = services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var host = new ApplicationUser { UserName = "load@example.test", Email = "load@example.test", DisplayName = "Load test" };
            db.Users.Add(host);
            await db.SaveChangesAsync();
            return host.Id;
        }

        // Scored, with a leaderboard, because that's the heaviest kind of reveal.
        private static Quiz BuildQuiz(string hostId, int questions) => new()
        {
            OwnerId = hostId,
            Title = "Load test",
            ScoringEnabled = true,
            ShowLeaderboard = true,
            Questions = Enumerable.Range(0, questions).Select(i => new Question
            {
                Order = i,
                Type = QuestionType.MultipleChoice,
                Prompt = $"<p>Question {i + 1}: which keyword declares a constant in C#?</p>",
                Options =
                [
                    new ChoiceOption { Order = 0, Text = "const", IsCorrect = true },
                    new ChoiceOption { Order = 1, Text = "var" },
                    new ChoiceOption { Order = 2, Text = "let" },
                ],
            }).ToList(),
        };
    }
}

/// <summary>Adds a delay to every database command, because LocalDB answers far faster than Azure SQL over a network.</summary>
internal sealed class SlowDatabase(int delayMs) : DbCommandInterceptor
{
    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await Task.Delay(delayMs, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await Task.Delay(delayMs, cancellationToken);
        return result;
    }
}

internal sealed record SessionCreated(Guid Id, string Code);

internal sealed record ResourceStats(double AverageCpuPercent, double PeakCpuPercent, double PeakWorkingSetMb, double PeakManagedHeapMb);

/// <summary>Samples this process's CPU and memory. CPU is a percentage of the one core it's limited to.</summary>
internal sealed class ResourceSampler : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private readonly Lock gate = new();
    private readonly Process process = Process.GetCurrentProcess();
    private readonly Timer timer;
    private TimeSpan lastCpu;
    private long lastTimestamp = Stopwatch.GetTimestamp();
    private double cpuSum;
    private double cpuPeak;
    private int samples;
    private long peakWorkingSet;
    private long peakHeap;

    public ResourceSampler()
    {
        lastCpu = process.TotalProcessorTime;
        timer = new Timer(_ => Sample(), null, Interval, Interval);
    }

    private void Sample()
    {
        lock (gate)
        {
            process.Refresh();
            var cpu = process.TotalProcessorTime;
            var now = Stopwatch.GetTimestamp();
            var percent = (cpu - lastCpu).TotalMilliseconds / Stopwatch.GetElapsedTime(lastTimestamp, now).TotalMilliseconds * 100;
            lastCpu = cpu;
            lastTimestamp = now;

            cpuSum += percent;
            cpuPeak = Math.Max(cpuPeak, percent);
            samples++;
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
            peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(false));
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            cpuSum = 0;
            cpuPeak = 0;
            samples = 0;
            peakWorkingSet = 0;
            peakHeap = 0;
        }
    }

    public ResourceStats Read()
    {
        lock (gate)
        {
            const double Mb = 1024 * 1024;
            return new ResourceStats(samples == 0 ? 0 : cpuSum / samples, cpuPeak, peakWorkingSet / Mb, peakHeap / Mb);
        }
    }

    public void Dispose() => timer.Dispose();
}
