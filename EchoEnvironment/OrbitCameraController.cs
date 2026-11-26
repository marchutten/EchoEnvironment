using System.Windows;
using System.Windows.Media.Media3D;

namespace EchoEnvironment;

/// <summary>
/// The orbit view: where the camera sits, how far away it is, and the extra rotation the user puts
/// on the scene itself with X/Y/Z or the mouse.
///
/// Extracted from MainWindow at Phase 5 commit 2 (MIGRATION_PLAN 5 maps CreateViewTransform,
/// UpdateCamera, RotateView, the camera state and the View_* handlers here). The arithmetic is
/// copied element-for-element.
///
/// MIGRATION_PLAN 5 names the state `_yaw/_pitch/_zoom`. The fields it actually replaced are
/// `_camYaw`, `_camPitch`, `_camDistance`, plus `_viewRotation`, `_lastMouse` and `_dragging` - six
/// fields, not three. Dropping the last three would have lost drag state, and MainWindowNoLongerOwnsCameraOrZoneState
/// now pins all six names so they cannot creep back.
///
/// Two deliberate choices:
///
/// 1. It is public, not internal. WpfConversionIsConfinedToOneType only inspects public types, so an
///    internal controller would have been invisible to the guard that is supposed to notice a second
///    Core-to-WPF conversion point appearing in the App.
///
/// 2. CreateViewTransform takes the orientation as a parameter instead of owning it. The orientation
///    is the sensor's, not the camera's. Owning it here would have put sensor state inside a view
///    object and made the shared MatrixTransform3D harder to see, not easier.
///
/// The state-mutating methods call UpdateCamera themselves. MainWindow used to call UpdateCamera once
/// after its key switch; each mutation now refreshes the camera in the same place it changes the
/// angles, so no caller has to remember a second step.
/// </summary>
public sealed class OrbitCameraController
{
    private const double DefaultYaw = 20;
    private const double DefaultPitch = 20;
    private const double DefaultDistance = 5;

    /// <summary>Pitch is clamped short of the poles, which is what keeps LookDirection finite.</summary>
    private const double PitchLimitDegrees = 89;

    private const double MinDistance = 0.5;
    private const double MaxDistance = 30;
    private const double ZoomInFactor = 0.9;
    private const double ZoomOutFactor = 1.1;
    private const double DragDegreesPerPixel = 0.5;

    private readonly PerspectiveCamera _camera;
    private readonly ModelVisual3D _sceneVisual;
    private readonly UIElement _viewSurface;

    private double _yaw = DefaultYaw;
    private double _pitch = DefaultPitch;
    private double _distance = DefaultDistance;
    private Point _lastMouse;
    private bool _dragging;
    private Quaternion _viewRotation = Quaternion.Identity;

    public OrbitCameraController(PerspectiveCamera camera, ModelVisual3D sceneVisual, UIElement viewSurface)
    {
        _camera = camera;
        _sceneVisual = sceneVisual;
        _viewSurface = viewSurface;
    }

    public void UpdateCamera()
    {
        double yaw = _yaw * Math.PI / 180.0;
        double pitch = _pitch * Math.PI / 180.0;
        var position = new Vector3D(
            _distance * Math.Cos(pitch) * Math.Sin(yaw),
            _distance * Math.Sin(pitch),
            _distance * Math.Cos(pitch) * Math.Cos(yaw));
        _camera.Position = new Point3D(position.X, position.Y, position.Z);
        _camera.LookDirection = -position;
    }

    public void NudgeYaw(double degrees)
    {
        _yaw += degrees;
        UpdateCamera();
    }

    public void NudgePitch(double degrees)
    {
        _pitch = Math.Clamp(_pitch + degrees, -PitchLimitDegrees, PitchLimitDegrees);
        UpdateCamera();
    }

    public void Zoom(int wheelDelta)
    {
        _distance = Math.Clamp(_distance * (wheelDelta > 0 ? ZoomInFactor : ZoomOutFactor), MinDistance, MaxDistance);
        UpdateCamera();
    }

    /// <summary>Back to the angles the window starts with, and no scene rotation.</summary>
    public void Reset()
    {
        _viewRotation = Quaternion.Identity;
        _sceneVisual.Transform = Transform3D.Identity;
        _yaw = DefaultYaw;
        _pitch = DefaultPitch;
        _distance = DefaultDistance;
        UpdateCamera();
    }

    /// <summary>
    /// Rotates the scene rather than the camera. Shift inverts the direction, which is the same
    /// behaviour the key handler had; it stays here so the sign convention lives with the rotation.
    /// </summary>
    public void RotateScene(Vector3D axis, double degrees)
    {
        bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
        var delta = new Quaternion(axis, shift ? -degrees : degrees);
        _viewRotation = delta * _viewRotation;
        _viewRotation.Normalize();
        _sceneVisual.Transform = new RotateTransform3D(new QuaternionRotation3D(_viewRotation));
    }

    public void BeginDrag(Point at)
    {
        _dragging = true;
        _lastMouse = at;
        _viewSurface.CaptureMouse();
    }

    public void EndDrag()
    {
        _dragging = false;
        _viewSurface.ReleaseMouseCapture();
    }

    public void DragTo(Point at)
    {
        if (!_dragging)
        {
            return;
        }

        _yaw -= (at.X - _lastMouse.X) * DragDegreesPerPixel;
        _pitch = Math.Clamp(_pitch + (at.Y - _lastMouse.Y) * DragDegreesPerPixel, -PitchLimitDegrees, PitchLimitDegrees);
        _lastMouse = at;
        UpdateCamera();
    }

    /// <summary>
    /// The transform chain that places a model group in the view: an optional model remap, the
    /// sensor orientation, then the 90 degree turn that makes the sensor's -Z look direction point
    /// along the view.
    ///
    /// `remapModel` is the board view's axis swap, and it is the reason the two viewports disagree
    /// about which axis is which (defect D9). 5d settles that; this method only reproduces it.
    /// </summary>
    public Transform3DGroup CreateViewTransform(bool remapModel, MatrixTransform3D orientation)
    {
        var group = new Transform3DGroup();
        if (remapModel)
        {
            group.Children.Add(new MatrixTransform3D(new Matrix3D(0, 0, 1, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1)));
        }
        else
        {
            group.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), 180)));
        }

        group.Children.Add(orientation);
        group.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 90)));
        return group;
    }
}
