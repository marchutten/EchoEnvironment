using System.Runtime.ExceptionServices;

namespace EchoEnvironment.Tests;

/// <summary>
/// Runs a test body on an STA thread.
///
/// Added at Phase 5 commit 2, and the reason it exists is a finding rather than a workaround. The
/// suite had never needed one before: every earlier test either used free-threaded value types
/// (Matrix3D, Vector3D) or read source files as text. ZoneRenderer is the first production type the
/// tests construct UI objects for, and constructing one turned out to require a single-threaded
/// apartment.
///
/// The stack trace that produced this helper is the interesting part:
///
///   System.InvalidOperationException : The calling thread must be STA, because many UI components require this.
///     at System.Windows.Input.InputManager..ctor()
///     at System.Windows.FrameworkElement..ctor()
///     at System.Windows.Controls.Panel..ctor()
///     at EchoEnvironment.Tests.ZoneRendererTests.New()
///
/// It failed on `new UniformGrid()`. Not on a Model3DGroup, not on a GeometryModel3D, not on a brush:
/// all of those are Freezable value-ish objects that construct fine on an MTA thread. What failed is
/// the 2D panel. So the honest reading is that ZoneRenderer mixes two jobs - 3D zone geometry, which is
/// apartment-free, and the 2D heat grid on the right panel, which is a FrameworkElement and therefore
/// Dispatcher-affine. Splitting the heat grid out of ZoneRenderer would let most of the renderer stay
/// testable on the thread-pool. That split is not done here; it is recorded as a candidate, and this
/// helper is what the unsplit design currently costs.
///
/// Exceptions from the body are rethrown on the calling thread with their original stack trace, so a
/// failure inside Run reports as the test's own assertion failure rather than as an opaque thread error.
/// </summary>
internal static class StaRunner
{
    public static void Run(Action body)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "EchoEnvironment.Tests.STA",
        };

        // Must be set before Start: an apartment cannot be changed once the thread is running.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
