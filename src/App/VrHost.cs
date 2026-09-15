using System;
using Godot;

namespace BSVRManager.App;

/// <summary>
/// VR scene: three world-space panels around the player —
/// left = Profiles, center = main content, right = Launch + Settings.
/// Controller lasers work on whichever panel the ray hits.
/// </summary>
public partial class VrHost : Node3D
{
    const float Dist = 1.1f;         // center panel distance (m)
    const float SideDist = 1.3f;     // side panel distance (m)
    const float SideAngle = 45f;     // side panel angle off the forward axis (deg)

    XROrigin3D _origin;
    XRCamera3D _camera;
    XRController3D _left, _right;
    readonly SubViewport[] _viewports = new SubViewport[3];
    readonly MeshInstance3D[] _panels = new MeshInstance3D[3];
    readonly float[] _widths, _heights;
    readonly Laser[] _lasers = { new("left_hand"), new("right_hand") };
    readonly MeshInstance3D[] _dots = new MeshInstance3D[2];
    StandardMaterial3D _dotMat;
    AppMain _app;
    Vector2 _scrollAccum;
    bool _placed;
    bool _lasersHidden;

    /// <summary>Cover-shot mode: no controllers exist, so park the beams out of sight.</summary>
    public bool ShowLasers = true;

    class Laser
    {
        public string Tracker;
        public MeshInstance3D Beam;
        public XRController3D Controller;
        public int Panel = -1;           // index of the panel under the ray, -1 none
        public Vector2 LastUV;
        public Laser(string tracker) { Tracker = tracker; }
    }

    public VrHost(AppMain app, SubViewport leftVp, SubViewport centerVp, SubViewport rightVp, float[] widths, float[] heights)
    {
        _app = app;
        _widths = widths;
        _heights = heights;
        _viewports[0] = leftVp; _viewports[1] = centerVp; _viewports[2] = rightVp;
        Name = "VrHost";
    }

    public override void _Ready()
    {
        _origin = new XROrigin3D { Name = "XROrigin" };
        AddChild(_origin);
        _camera = new XRCamera3D { Name = "XRCamera" };
        _origin.AddChild(_camera);
        _left = new XRController3D { Tracker = "left_hand", Name = "LeftHand" };
        _right = new XRController3D { Tracker = "right_hand", Name = "RightHand" };
        _origin.AddChild(_left);
        _origin.AddChild(_right);

        for (int i = 0; i < 3; i++)
        {
            var size = new Vector2(_widths[i], _heights[i]);
            var panel = new MeshInstance3D { Mesh = new QuadMesh { Size = size } };
            var mat = new StandardMaterial3D
            {
                AlbedoTexture = _viewports[i].GetTexture(),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            panel.MaterialOverride = mat;
            AddChild(panel);
            _panels[i] = panel;
        }

        // frame borders so panels read as objects against dark games.
        // parented to the panel so they follow placement (PlacePanel runs later).
        for (int i = 0; i < 3; i++)
        {
            var frame = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(_widths[i] * 1.02f, _heights[i] * 1.02f) } };
            var m = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.16f, 0.2f, 0.3f) };
            frame.MaterialOverride = m;
            frame.Position = new Vector3(0, 0, -0.002f);
            _panels[i].AddChild(frame);
        }

        _dotMat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.4f, 0.75f, 1f) };
        _dots[0] = MakeDot();
        _dots[1] = MakeDot();

        foreach (var laser in _lasers)
        {
            laser.Beam = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.003f, 0.003f, 1f) } };
            var m = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.35f, 0.6f, 1f, 0.55f) };
            m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            laser.Beam.MaterialOverride = m;
            AddChild(laser.Beam);
        }

        _left.ButtonPressed += name => OnButton(_lasers[0], name, true);
        _left.ButtonReleased += name => OnButton(_lasers[0], name, false);
        _right.ButtonPressed += name => OnButton(_lasers[1], name, true);
        _right.ButtonReleased += name => OnButton(_lasers[1], name, false);
    }

    MeshInstance3D MakeDot()
    {
        var d = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.008f, Height = 0.016f }, MaterialOverride = _dotMat };
        d.Visible = false;
        AddChild(d);
        return d;
    }

    void OnButton(Laser laser, string button, bool pressed)
    {
        if (laser.Panel < 0) return;
        if (button is "trigger_click" or "trigger")
            PushMouse(laser, pressed);
    }

    public override void _Process(double delta)
    {
        if (!_placed)
        {
            _placed = true;
            var camXf = _camera.GlobalTransform;
            var forward = -camXf.Basis.Z;
            forward.Y = 0;
            if (forward.LengthSquared() < 0.001f) forward = new Vector3(0, 0, -1);
            forward = forward.Normalized();
            var eye = camXf.Origin;

            // center straight ahead; sides angled toward the player
            PlacePanel(_panels[0], eye, forward, -1);
            PlacePanel(_panels[1], eye, forward, 0);
            PlacePanel(_panels[2], eye, forward, 1);
        }

        if (ShowLasers)
        {
            UpdateLaser(_lasers[0], _left, _dots[0]);
            UpdateLaser(_lasers[1], _right, _dots[1]);
        }
        else
        {
            SetLasersVisible(false);
        }
        UpdateScroll();
    }

    void SetLasersVisible(bool visible)
    {
        if (_lasersHidden == !visible) return;
        _lasersHidden = !visible;
        foreach (var laser in _lasers) laser.Beam.Visible = visible;
        foreach (var dot in _dots) dot.Visible = visible;
    }

    void PlacePanel(MeshInstance3D panel, Vector3 eye, Vector3 forward, int side)
    {
        // forward rotated SideAngle toward that side, at the same 1.1 m distance
        var right = forward.Cross(Vector3.Up).Normalized();
        float ang = SideAngle * MathF.PI / 180f;
        Vector3 pos;
        if (side == 0)
            pos = eye + forward * Dist;
        else
            pos = eye + forward * (SideDist * MathF.Cos(ang)) + right * (SideDist * MathF.Sin(ang) * side);
        panel.GlobalPosition = pos;
        var toEye = eye - pos;
        panel.LookAt(pos - toEye, Vector3.Up); // front face toward the eye
    }

    void UpdateLaser(Laser laser, XRController3D ctrl, MeshInstance3D dot)
    {
        if (ctrl == null || !ctrl.IsInsideTree()) { laser.Beam.Visible = false; laser.Panel = -1; return; }
        var from = ctrl.GlobalPosition;
        var dir = -ctrl.GlobalTransform.Basis.Z;

        int bestPanel = -1;
        var bestPoint = Vector3.Zero;
        var bestUv = Vector2.Zero;
        float bestDist = float.MaxValue;

        for (int i = 0; i < 3; i++)
        {
            var panel = _panels[i];
            if (panel == null || !panel.IsInsideTree()) continue;
            var n = panel.GlobalTransform.Basis.Z;
            var denom = n.Dot(dir);
            if (Mathf.Abs(denom) < 0.0001f) continue;
            float t = n.Dot(panel.GlobalPosition - from) / denom;
            if (t <= 0 || t >= bestDist) continue;
            var hit = from + dir * t;
            var local = panel.GlobalTransform.AffineInverse() * hit;
            var half = _widths[i] / 2f;
            var halfH = _heights[i] / 2f;
            if (Mathf.Abs(local.X) <= half && Mathf.Abs(local.Y) <= halfH)
            {
                bestPanel = i;
                bestPoint = hit;
                bestDist = t;
                bestUv = new Vector2(
                    Mathf.Clamp((local.X + half) / _widths[i], 0, 1) * _viewports[i].Size.X,
                    (1f - Mathf.Clamp((local.Y + halfH) / _heights[i], 0, 1)) * _viewports[i].Size.Y);
            }
        }

        laser.Panel = bestPanel;
        laser.LastUV = bestUv;

        float beamLen = bestPanel >= 0 ? bestDist : 3f;
        laser.Beam.Visible = true;
        laser.Beam.GlobalPosition = from + dir * (beamLen / 2f);
        laser.Beam.LookAtFromPosition(from + dir * (beamLen / 2f), from + dir * beamLen, Vector3.Up);
        laser.Beam.Scale = new Vector3(1, 1, beamLen);

        dot.Visible = bestPanel >= 0;
        if (dot.Visible) dot.GlobalPosition = from + dir * (bestDist - 0.01f);
        if (bestPanel >= 0) PushMotion(laser);
    }

    void PushMotion(Laser laser)
    {
        if (laser.Panel < 0 || laser.LastUV.X < 0) return;
        var move = new InputEventMouseMotion { Position = laser.LastUV, GlobalPosition = laser.LastUV };
        _viewports[laser.Panel].PushInput(move, true);
    }

    void PushMouse(Laser laser, bool pressed)
    {
        if (laser.Panel < 0 || laser.LastUV.X < 0) return;
        var vp = _viewports[laser.Panel];
        var press = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = laser.LastUV, GlobalPosition = laser.LastUV };
        vp.PushInput(press, true);
        if (!pressed)
        {
            var up = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = laser.LastUV, GlobalPosition = laser.LastUV };
            vp.PushInput(up, true);
        }
    }

    void UpdateScroll()
    {
        foreach (var laser in _lasers)
        {
            var ctrl = laser == _lasers[0] ? _left : _right;
            if (ctrl == null) continue;
            var v = ctrl.GetVector2("primary");
            if (Mathf.Abs(v.Y) > 0.55f && laser.Panel >= 0)
                _scrollAccum.Y += v.Y * 14f;
        }
        if (Mathf.Abs(_scrollAccum.Y) >= 1f)
        {
            var steps = (int)_scrollAccum.Y;
            _scrollAccum.Y -= steps;
            var laser = _lasers[1].Panel >= 0 ? _lasers[1] : (_lasers[0].Panel >= 0 ? _lasers[0] : null);
            if (laser == null) return;
            var vp = _viewports[laser.Panel];
            for (int i = 0; i < System.Math.Abs(steps); i++)
            {
                var wheel = new InputEventMouseButton
                {
                    Position = laser.LastUV,
                    GlobalPosition = laser.LastUV,
                    ButtonIndex = steps > 0 ? MouseButton.WheelUp : MouseButton.WheelDown,
                    Pressed = true,
                };
                vp.PushInput(wheel, true);
            }
        }
    }
}
