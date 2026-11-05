using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using CohortCompanion.Shared.Live;
using Microsoft.AspNetCore.SignalR.Client;

namespace CohortCompanion.LoadTest;

/// <summary>Starts the limited server, runs sessions with simulated students, and reports what it measured.</summary>
internal static class LoadRunner
{
    private const int Port = 5090;
    private const int Questions = 4;

    /// <summary>The app allows 3000 joins a minute per address, and every student here shares one address.</summary>
    private const int JoinsPerMinute = 3000;

    /// <summary>The last question's time limit. It must be longer than <see cref="AnswerSpread"/>.</summary>
    public const int TimedQuestionSeconds = 10;

    private const int WarmUpStudents = 5;

    /// <summary>How long students take to answer in the "normal" rounds. One round has them all answer at once.</summary>
    private static readonly TimeSpan AnswerSpread = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How many connections open at once across all classes, so the tool itself doesn't become the bottleneck.</summary>
    private static readonly SemaphoreSlim Opening = new(100);

    private static readonly HttpClient Http = new() { BaseAddress = new Uri($"http://127.0.0.1:{Port}"), Timeout = TimeSpan.FromMinutes(3) };

    public static async Task<int> RunAsync(string[] args)
    {
        var students = ArgumentValue(args, "--students", 300);
        var classes = ArgumentValue(args, "--classes", 1);
        var staggerSeconds = ArgumentValue(args, "--stagger", 0);
        var databaseDelayMs = ArgumentValue(args, "--db-delay", 0);
        var resultsPath = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault();
        var total = students * classes;
        if (total > JoinsPerMinute)
        {
            Console.WriteLine($"Note: {total} students is over the app's limit of {JoinsPerMinute} joins a minute per address, so expect refused joins.");
        }

        ThreadPool.SetMinThreads(256, 256);

        // The server gets the last core to itself, and the simulated students get the rest.
        var serverCore = 1L << (Environment.ProcessorCount - 1);
        var allCores = (1L << Environment.ProcessorCount) - 1;
        Process.GetCurrentProcess().ProcessorAffinity = new IntPtr(allCores & ~serverCore);

        using var server = StartServer(serverCore, databaseDelayMs);
        try
        {
            await WaitForServerAsync(server);

            // First-use costs like JIT compilation shouldn't count: a real server has already been running.
            Console.WriteLine("Warming up...");
            await new SessionRun(WarmUpStudents).ExecuteAsync();
            if (total + WarmUpStudents > JoinsPerMinute)
            {
                Console.WriteLine("Waiting for the rate limit window to reset...");
                await Task.Delay(TimeSpan.FromSeconds(61));
            }

            await Http.PostAsync("__load/reset-stats", null);
            var label = classes == 1 ? $"{students} students" : $"{classes} classes of {students}";
            if (staggerSeconds > 0)
            {
                label += $", starting within {staggerSeconds} s";
            }

            Console.WriteLine($"Running {label}...");
            var random = new Random(1);
            var reports = await Task.WhenAll(Enumerable.Range(0, classes).Select(async _ =>
            {
                await Task.Delay(TimeSpan.FromSeconds(random.NextDouble() * staggerSeconds));
                return await new SessionRun(students).ExecuteAsync();
            }));
            var report = Report.Merge(label, reports);
            report.DatabaseDelayMs = databaseDelayMs;
            var stats = (await Http.GetFromJsonAsync<ResourceStats>("__load/stats"))!;

            var text = report.Format(stats);
            Console.WriteLine(text);
            if (resultsPath is not null)
            {
                await File.AppendAllTextAsync(resultsPath, text + Environment.NewLine);
            }

            return report.Failures == 0 ? 0 : 2;
        }
        finally
        {
            try
            {
                await Http.PostAsync("__load/shutdown", null);
                await server.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
            }
            catch (Exception)
            {
                server.Kill(entireProcessTree: true);
            }
        }
    }

    private static Process StartServer(long coreMask, int databaseDelayMs)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
        };
        // dotnet run launches the apphost, so the path above is this tool itself.
        start.ArgumentList.Add(LoadServer.ModeArgument);
        start.ArgumentList.Add(Port.ToString());
        start.ArgumentList.Add(coreMask.ToString());
        start.ArgumentList.Add(databaseDelayMs.ToString());

        // The test host's own guess at the app's folder assumes projects sit beside the solution file.
        start.Environment["ASPNETCORE_TEST_CONTENTROOT_COHORTCOMPANION_WEB"] = FindAppFolder();

        // What the runtime would see on a B1: one processor and 1.75 GB, less room for the operating system.
        start.Environment["DOTNET_PROCESSOR_COUNT"] = "1";
        start.Environment["DOTNET_gcServer"] = "0";
        start.Environment["DOTNET_GCHeapHardLimit"] = "50000000";

        return Process.Start(start)!;
    }

    private static string FindAppFolder()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "CohortCompanion.slnx")))
        {
            folder = folder.Parent;
        }

        return Path.Combine(folder?.FullName ?? throw new DirectoryNotFoundException("Couldn't find the repository."), "src", "CohortCompanion.Web");
    }

    private static async Task WaitForServerAsync(Process server)
    {
        for (var attempt = 0; attempt < 240; attempt++)
        {
            if (server.HasExited)
            {
                throw new InvalidOperationException("The server stopped while starting.");
            }

            try
            {
                using var response = await Http.GetAsync("__load/stats");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("The server didn't start.");
    }

    private static int ArgumentValue(string[] args, string name, int fallback)
    {
        var value = args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        return value is null ? fallback : int.Parse(value);
    }

    /// <summary>One session from join to end.</summary>
    private sealed class SessionRun(int studentCount)
    {
        private readonly EventRecorder recorder = new();
        private readonly List<HubConnection> connections = [];
        private readonly Dictionary<HubConnection, int> connectionOwners = [];
        private readonly Report report = new($"{studentCount} students");
        private int disconnects;
        private int joined;
        private readonly string?[] tokens = new string?[studentCount];
        private volatile bool droppingOnPurpose;

        public async Task<Report> ExecuteAsync()
        {
            var created = (await (await Http.PostAsync($"__load/create?questions={Questions}", null)).Content.ReadFromJsonAsync<SessionCreated>())!;

            await JoinEveryoneAsync(created.Code);
            if (joined == 0)
            {
                return report;
            }

            await CommandAsync("start", created.Id);

            for (var question = 1; question <= Questions; question++)
            {
                // The second round is the worst case: the whole class answers in the same instant.
                var spread = question == 2 ? TimeSpan.Zero : AnswerSpread;
                await RunQuestionAsync(created.Id, question, spread, closesOnTimer: question == Questions);
            }

            await ReconnectEveryoneAsync();
            report.End = await CommandAsync("end", created.Id);
            report.Disconnects = disconnects;
            await Task.WhenAll(connections.Select(c => c.DisposeAsync().AsTask()));
            return report;
        }

        private async Task JoinEveryoneAsync(string code)
        {
            var started = Stopwatch.GetTimestamp();
            var latencies = new ConcurrentBag<double>();

            // Everyone at once is harsher than a real class, who scan the code over half a minute or so.
            var refused = new ConcurrentBag<HubConnection>();
            await Task.WhenAll(Enumerable.Range(0, studentCount).Select(async index =>
            {
                await Opening.WaitAsync();
                var connection = Connect(index);
                lock (connections)
                {
                    connections.Add(connection);
                    connectionOwners[connection] = index;
                }

                try
                {
                    var joinStarted = Stopwatch.GetTimestamp();
                    await connection.StartAsync();
                    var result = await connection.InvokeAsync<JoinResult>("Join", code, $"Student{index}");
                    latencies.Add(Stopwatch.GetElapsedTime(joinStarted).TotalMilliseconds);
                    tokens[index] = result.ParticipantToken;
                    if (result.Error != JoinError.None)
                    {
                        Interlocked.Increment(ref report.JoinFailures);
                        refused.Add(connection);
                    }
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref report.JoinFailures);
                    refused.Add(connection);
                }
                finally
                {
                    Opening.Release();
                }
            }));

            report.JoinTotal = Stopwatch.GetElapsedTime(started);
            report.Join = latencies.ToList();

            droppingOnPurpose = true;
            foreach (var connection in refused)
            {
                connections.Remove(connection);
                await connection.DisposeAsync();
            }

            droppingOnPurpose = false;
            joined = connections.Count;
        }

        private HubConnection Connect(int index)
        {
            var connection = new HubConnectionBuilder().WithUrl($"http://127.0.0.1:{Port}{HubEvents.HubPath}").Build();

            connection.On<PhaseUpdate>(HubEvents.PhaseChanged, update =>
                recorder.Record($"{update.Phase}:{update.QuestionIndex}", index));
            connection.On<Standing?>(HubEvents.LeaderboardUpdated, _ => recorder.RecordNext("standing", index));
            connection.Closed += _ =>
            {
                if (!droppingOnPurpose)
                {
                    Interlocked.Increment(ref disconnects);
                }


                return Task.CompletedTask;
            };
            return connection;
        }

        /// <summary>The Wi-Fi drops and the whole class comes back at once, soon after joining.</summary>
        private async Task ReconnectEveryoneAsync()
        {
            droppingOnPurpose = true;
            await Task.WhenAll(connections.Select(c => c.StopAsync()));

            var latencies = new ConcurrentBag<double>();
            var started = Stopwatch.GetTimestamp();
            await Task.WhenAll(connections.Select(async (connection, position) =>
            {
                await Opening.WaitAsync();
                try
                {
                    var attempt = Stopwatch.GetTimestamp();
                    await connection.StartAsync();
                    var state = await connection.InvokeAsync<StudentSessionState?>("Rejoin", tokens[connectionOwners[connection]]);
                    latencies.Add(Stopwatch.GetElapsedTime(attempt).TotalMilliseconds);
                    if (state is null)
                    {
                        Interlocked.Increment(ref report.RejoinFailures);
                    }
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref report.RejoinFailures);
                }
                finally
                {
                    Opening.Release();
                }
            }));

            report.RejoinTotal = Stopwatch.GetElapsedTime(started);
            report.Rejoin = latencies.ToList();
            droppingOnPurpose = false;
        }

        private async Task RunQuestionAsync(Guid id, int question, TimeSpan spread, bool closesOnTimer)
        {
            var index = question - 1;

            var opened = Stopwatch.GetTimestamp();
            report.Commands.Add((await CommandAsync("open", id)).TotalMilliseconds);
            report.AddDelivery(report.QuestionDelivery, await recorder.WaitAsync($"{QuestionPhase.Open}:{index}", joined, opened, EventTimeout), joined);

            await SubmitAllAsync(question, spread);

            if (closesOnTimer)
            {
                // The server allows half a second of grace past the deadline, so that's when this should arrive.
                var closes = await recorder.WaitAsync($"{QuestionPhase.Closed}:{index}", joined, opened, EventTimeout);
                var expected = (TimedQuestionSeconds * 1000) + 500;
                report.AddDelivery(report.TimerLateness, closes.Select(ms => ms - expected).ToList(), joined);
            }
            else
            {
                report.Commands.Add((await CommandAsync("close", id)).TotalMilliseconds);
            }

            report.Commands.Add((await CommandAsync("show", id)).TotalMilliseconds);

            var revealed = Stopwatch.GetTimestamp();
            report.Reveals.Add((await CommandAsync("reveal", id)).TotalMilliseconds);
            report.AddDelivery(report.RevealDelivery, await recorder.WaitAsync($"{QuestionPhase.AnswerRevealed}:{index}", joined, revealed, EventTimeout), joined);
            report.AddDelivery(report.StandingDelivery, await recorder.WaitAsync($"standing:{question}", joined, revealed, EventTimeout), joined);

            report.Commands.Add((await CommandAsync("next", id)).TotalMilliseconds);
        }

        private async Task SubmitAllAsync(int question, TimeSpan spread)
        {
            var target = spread == TimeSpan.Zero ? report.BurstAnswers : report.SpreadAnswers;
            var random = new Random(question);
            var delays = connections.Select(_ => TimeSpan.FromMilliseconds(random.NextDouble() * spread.TotalMilliseconds)).ToList();

            await Task.WhenAll(connections.Select(async (connection, position) =>
            {
                await Task.Delay(delays[position]);
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var result = await connection.InvokeAsync<SubmitResult>("SubmitAnswer", question, new AnswerPayload(Options: [position % 3]));
                    target.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    if (!result.Accepted)
                    {
                        Interlocked.Increment(ref report.AnswerFailures);
                    }
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref report.AnswerFailures);
                }
            }));
        }

        private async Task<TimeSpan> CommandAsync(string command, Guid id)
        {
            var started = Stopwatch.GetTimestamp();
            using var response = await Http.PostAsync($"__load/{command}?id={id}", null);
            if (!response.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref report.CommandFailures);
            }

            return Stopwatch.GetElapsedTime(started);
        }
    }

    /// <summary>Remembers when each student received each event, and lets the run wait for all of them.</summary>
    private sealed class EventRecorder
    {
        private readonly ConcurrentDictionary<string, ConcurrentBag<long>> events = new();
        private readonly ConcurrentDictionary<int, int> standingsSeen = new();

        public void Record(string key, int student) =>
            events.GetOrAdd(key, _ => []).Add(Stopwatch.GetTimestamp());

        /// <summary>Standings carry no question number, so each student's count says which question it's for.</summary>
        public void RecordNext(string key, int student) =>
            Record($"{key}:{standingsSeen.AddOrUpdate(student, 1, (_, seen) => seen + 1)}", student);

        public async Task<List<double>> WaitAsync(string key, int count, long startedAt, TimeSpan timeout)
        {
            var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < deadline && (!events.TryGetValue(key, out var received) || received.Count < count))
            {
                await Task.Delay(5);
            }

            return events.TryGetValue(key, out var all)
                ? all.Select(timestamp => Stopwatch.GetElapsedTime(startedAt, timestamp).TotalMilliseconds).ToList()
                : [];
        }
    }

    private sealed class Report(string label)
    {
        public int JoinFailures;
        public int RejoinFailures;
        public int AnswerFailures;
        public int CommandFailures;
        public int MissedEvents;

        public int Disconnects { get; set; }

        public TimeSpan JoinTotal { get; set; }

        public TimeSpan RejoinTotal { get; set; }

        public int DatabaseDelayMs { get; set; }

        public List<double> Rejoin { get; set; } = [];

        public TimeSpan End { get; set; }

        public List<double> Join { get; set; } = [];

        public ConcurrentBag<double> SpreadAnswers { get; } = [];

        public ConcurrentBag<double> BurstAnswers { get; } = [];

        public List<double> QuestionDelivery { get; } = [];

        public List<double> RevealDelivery { get; } = [];

        public List<double> StandingDelivery { get; } = [];

        public List<double> Reveals { get; } = [];

        public List<double> TimerLateness { get; } = [];

        public List<double> Commands { get; } = [];

        public int Failures => JoinFailures + RejoinFailures + AnswerFailures + CommandFailures + MissedEvents + Disconnects;

        /// <summary>Combines each class's report. Durations keep the slowest class.</summary>
        public static Report Merge(string label, IReadOnlyList<Report> reports)
        {
            var merged = new Report(label);
            foreach (var report in reports)
            {
                merged.JoinFailures += report.JoinFailures;
                merged.RejoinFailures += report.RejoinFailures;
                merged.AnswerFailures += report.AnswerFailures;
                merged.CommandFailures += report.CommandFailures;
                merged.MissedEvents += report.MissedEvents;
                merged.Disconnects += report.Disconnects;
                merged.JoinTotal = TimeSpan.FromTicks(Math.Max(merged.JoinTotal.Ticks, report.JoinTotal.Ticks));
                merged.RejoinTotal = TimeSpan.FromTicks(Math.Max(merged.RejoinTotal.Ticks, report.RejoinTotal.Ticks));
                merged.End = TimeSpan.FromTicks(Math.Max(merged.End.Ticks, report.End.Ticks));
                merged.Join.AddRange(report.Join);
                merged.Rejoin.AddRange(report.Rejoin);
                merged.QuestionDelivery.AddRange(report.QuestionDelivery);
                merged.RevealDelivery.AddRange(report.RevealDelivery);
                merged.StandingDelivery.AddRange(report.StandingDelivery);
                merged.Reveals.AddRange(report.Reveals);
                merged.Commands.AddRange(report.Commands);
                merged.TimerLateness.AddRange(report.TimerLateness);
                foreach (var value in report.SpreadAnswers)
                {
                    merged.SpreadAnswers.Add(value);
                }

                foreach (var value in report.BurstAnswers)
                {
                    merged.BurstAnswers.Add(value);
                }
            }

            return merged;
        }

        public void AddDelivery(List<double> target, List<double> times, int expected)
        {
            target.AddRange(times);
            MissedEvents += Math.Max(0, expected - times.Count);
        }

        public string Format(ResourceStats stats) => string.Join(Environment.NewLine,
            $"## {label}" + (DatabaseDelayMs > 0 ? $", {DatabaseDelayMs} ms added to each database command" : ""),
            "",
            "| Measure | Median | 95th percentile | Worst |",
            "|---|---|---|---|",
            Row("Join (connect and join)", Join),
            Row("Question reaches a student after opening", QuestionDelivery),
            Row("Answer round trip, spread over 5 seconds", SpreadAnswers),
            Row("Answer round trip, everyone at once", BurstAnswers),
            Row("Results reach a student after the reveal", RevealDelivery),
            Row("Own score reaches a student after the reveal", StandingDelivery),
            Row("Reveal command", Reveals),
            Row("Other instructor commands", Commands),
            Row("Reconnect after everyone drops at once", Rejoin),
            Row("Timer closes a question late by", TimerLateness),
            "",
            $"- The slowest class joined in {JoinTotal.TotalSeconds:0.0} s and reconnected in {RejoinTotal.TotalSeconds:0.0} s. Ending a session took up to {End.TotalMilliseconds:0} ms.",
            $"- Server CPU: average {stats.AverageCpuPercent:0}%, peak {stats.PeakCpuPercent:0}% of its one core.",
            $"- Server memory: peak working set {stats.PeakWorkingSetMb:0} MB, peak managed heap {stats.PeakManagedHeapMb:0} MB.",
            $"- Failures: {JoinFailures} joins, {RejoinFailures} reconnects, {AnswerFailures} answers, {CommandFailures} commands, {MissedEvents} missed events, {Disconnects} disconnects.",
            "");

        private static string Row(string name, IEnumerable<double> values)
        {
            var sorted = values.Order().ToList();
            return sorted.Count == 0
                ? $"| {name} | none | none | none |"
                : $"| {name} | {Percentile(sorted, 0.5):0} ms | {Percentile(sorted, 0.95):0} ms | {sorted[^1]:0} ms |";
        }

        private static double Percentile(List<double> sorted, double percentile) =>
            sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(percentile * sorted.Count) - 1)];
    }
}
