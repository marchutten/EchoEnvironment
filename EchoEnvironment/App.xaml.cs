using System.Windows;

namespace EchoEnvironment
{
    /// <summary>
    /// The composition root (MIGRATION_PLAN 6c): the one place that decides which sensor transport the
    /// app talks to, and the only place that constructs the window.
    ///
    /// WHY THIS IS A COMPOSITION ROOT AND NOT A CONVENIENCE. Since Phase 3 the solution has contained
    /// SimulatedSensorStream and ReplaySensorStream, and §5 has carried "App runs with --sim and
    /// --replay &lt;file&gt; - no hardware required" as only half satisfied for that entire time: the seam
    /// existed, the implementations existed, and MainWindow constructed SerialSensorStream directly, so
    /// no offline workflow was reachable. The missing piece was never a stream. It was a decision point,
    /// and a decision point inside a Window is not testable and not visible. It is now a parser over
    /// string[] and one switch here.
    ///
    /// NO DI CONTAINER. §6c says so explicitly and the size of the graph agrees: one stream, one window.
    /// A container would add a dependency and a concept and remove nothing.
    ///
    /// WHY A BAD COMMAND LINE ENDS THE PROCESS INSTEAD OF OPENING THE WINDOW ANYWAY. Opening a serial
    /// session when the user meant to replay a log is not a cosmetic failure: no capture file is written,
    /// no frames arrive, and the status line says "Invalid port or baud rate" about a problem that was
    /// knowable before the window existed. The failure is reported on stderr and the process exits with a
    /// non-zero code, which is what a script or a CI job can actually check.
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (!AppLaunchOptions.TryParse(e.Args, out SensorSource? source, out string? error))
            {
                Console.Error.WriteLine(error);
                Shutdown(2);
                return;
            }

            var window = new MainWindow(source!);
            window.Show();
        }
    }
}
