using System.Diagnostics;
using System.Windows.Threading;
using Mangosteen.Platform;

namespace Mangosteen;

/// <summary>
/// Runs file jobs off the UI thread, one file at a time, with the progress panel for anything that takes longer
/// than a moment, a Stop button, and one notification at the end.
/// </summary>
internal sealed class Jobs(Dispatcher dispatcher, TrayIcon tray)
{
    private static readonly TimeSpan HudDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>Applies <paramref name="work"/> to each input. Work returns the new file or folder, or null when there was nothing to do.</summary>
    public void RunEach(string title, string doneTitle, IReadOnlyList<string> inputs, Func<string, CancellationToken, string?> work) =>
        RunEach(title, doneTitle, inputs, (input, token, _) => work(input, token));

    /// <summary>Like <see cref="RunEach(string, string, IReadOnlyList{string}, Func{string, CancellationToken, string?})"/>, with progress (0…1) within each file.</summary>
    public void RunEach(string title, string doneTitle, IReadOnlyList<string> inputs, Func<string, CancellationToken, Action<double>, string?> work)
    {
        var cancel = new CancellationTokenSource();
        ProgressHud? hud = null;
        var finished = false;
        var timer = new DispatcherTimer(HudDelay, DispatcherPriority.Normal, (_, _) => { }, dispatcher);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!finished)
            {
                hud = new ProgressHud(title);
                hud.StopRequested += cancel.Cancel;
                hud.Show();
            }
        };
        timer.Start();

        Task.Run(() =>
        {
            var outputs = new List<string>();
            var failures = new List<string>();
            var done = 0;
            foreach (var input in inputs)
            {
                if (cancel.IsCancellationRequested)
                {
                    break;
                }
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(input));
                var index = done;
                dispatcher.BeginInvoke(() => hud?.Report(index, inputs.Count, name));
                var clock = Stopwatch.StartNew();
                try
                {
                    var lastShown = 0.0;
                    void Progress(double fraction)
                    {
                        // A few updates a second are plenty.
                        if (fraction - lastShown < 0.01 && fraction < 1)
                        {
                            return;
                        }
                        lastShown = fraction;
                        dispatcher.BeginInvoke(() => hud?.Report(index, inputs.Count, name, fraction));
                    }
                    if (work(input, cancel.Token, Progress) is { } output)
                    {
                        outputs.Add(output);
                        Log.Write($"{title}: {input} -> {output} in {clock.ElapsedMilliseconds} ms");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception error)
                {
                    failures.Add($"{name}: {error.Message}");
                    Log.Error($"{title}: {input}", error);
                }
                done++;
            }
            var stopped = cancel.IsCancellationRequested;
            dispatcher.BeginInvoke(() =>
            {
                finished = true;
                timer.Stop();
                hud?.Close();
                Report(doneTitle, outputs, failures, stopped ? string.Format(L("Stopped after {0} of {1}."), done, inputs.Count) : null);
                cancel.Dispose();
            });
        });
    }

    /// <summary>One job that turns all inputs into a single result; <paramref name="label"/> is shown while it runs.</summary>
    public void RunOnce(string title, string doneTitle, string label, Func<string> work) =>
        RunEach(title, doneTitle, [label], (_, _) => work());

    private void Report(string doneTitle, List<string> outputs, List<string> failures, string? stoppedNote)
    {
        if (failures.Count > 0)
        {
            var total = failures.Count + outputs.Count;
            tray.Notify(total == 1 ? string.Format(L("{0}: failed"), doneTitle) : string.Format(L("{0} of {1} failed"), failures.Count, total),
                string.Join("\n", failures.Take(3)), error: true);
            return;
        }
        if (stoppedNote is not null)
        {
            tray.Notify(string.Format(L("{0}: stopped"), doneTitle), stoppedNote, reveal: outputs.LastOrDefault());
            return;
        }
        if (outputs.Count == 0)
        {
            tray.Notify(doneTitle, L("Nothing to do: the files already have that format."));
            return;
        }
        tray.Notify(doneTitle, outputs.Count == 1 ? Path.GetFileName(outputs[0]) : Core.Loc.Count(outputs.Count, "file"), reveal: outputs[^1]);
    }
}
