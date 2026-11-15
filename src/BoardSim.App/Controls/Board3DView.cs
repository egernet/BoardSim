using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using BoardSim.Core.Components;
using static Avalonia.OpenGL.GlConsts;

namespace BoardSim.App.Controls;

/// <summary>
/// The board in 3D with its real component models. Drag to turn it, scroll to
/// zoom, shift-drag (or right-drag) to move, double-click to reset.
/// </summary>
public sealed class Board3DView : OpenGlControlBase
{
    private const int GL_LEQUAL = 0x0203, GL_UNSIGNED_INT = 0x1405;
    private Mesh3D? _mesh;
    private bool _upload;
    private int _program, _vao, _vbo, _ebo, _uMvp, _uView, _count;
    private float _yaw = -25, _pitch = 50, _distance = 1;
    private Vector2 _pan;
    private Point? _drag;
    private bool _panning;

    public string? Error { get; private set; }

    public Mesh3D? Mesh
    {
        get => _mesh;
        set { _mesh = value; _upload = true; ResetView(); RequestNextFrameRendering(); }
    }

    public void ResetView()
    {
        _yaw = -25; _pitch = 50; _pan = Vector2.Zero;
        if (_mesh is { } m) _distance = Vector3.Distance(m.Min, m.Max) * 1.25f;
        RequestNextFrameRendering();
    }

    private string Header(bool fragment) => GlVersion.Type == GlProfileType.OpenGLES
        ? "#version 300 es\n" + (fragment ? "precision mediump float;\n" : "")
        : "#version 150\n";

    private const string VertexSource = """
        in vec3 aPos;
        in vec3 aNormal;
        in vec3 aColor;
        uniform mat4 uMvp;
        uniform mat4 uView;
        out vec3 vNormal;
        out vec3 vColor;
        out vec3 vPos;
        void main() {
            gl_Position = uMvp * vec4(aPos, 1.0);
            vNormal = (uView * vec4(aNormal, 0.0)).xyz;
            vPos = (uView * vec4(aPos, 1.0)).xyz;
            vColor = aColor;
        }
        """;

    private const string FragmentSource = """
        in vec3 vNormal;
        in vec3 vColor;
        in vec3 vPos;
        out vec4 fragColor;
        void main() {
            vec3 n = normalize(vNormal);
            if (!gl_FrontFacing) n = -n;
            vec3 key = normalize(vec3(0.35, 0.55, 0.75));
            vec3 fill = normalize(vec3(-0.6, -0.2, 0.5));
            float diffuse = max(dot(n, key), 0.0) * 0.75 + max(dot(n, fill), 0.0) * 0.25;
            vec3 viewDir = normalize(-vPos);
            float spec = pow(max(dot(reflect(-key, n), viewDir), 0.0), 32.0) * 0.25;
            vec3 c = vColor * (0.28 + 0.85 * diffuse) + vec3(spec);
            fragColor = vec4(pow(clamp(c, 0.0, 1.0), vec3(1.0 / 2.2)), 1.0);
        }
        """;

    protected override void OnOpenGlInit(GlInterface gl)
    {
        var vs = gl.CreateShader(GL_VERTEX_SHADER);
        var fs = gl.CreateShader(GL_FRAGMENT_SHADER);
        var e1 = gl.CompileShaderAndGetError(vs, Header(false) + VertexSource);
        var e2 = gl.CompileShaderAndGetError(fs, Header(true) + FragmentSource);
        _program = gl.CreateProgram();
        gl.AttachShader(_program, vs);
        gl.AttachShader(_program, fs);
        gl.BindAttribLocationString(_program, 0, "aPos");
        gl.BindAttribLocationString(_program, 1, "aNormal");
        gl.BindAttribLocationString(_program, 2, "aColor");
        var e3 = gl.LinkProgramAndGetError(_program);
        Error = e1 ?? e2 ?? e3;
        _uMvp = gl.GetUniformLocationString(_program, "uMvp");
        _uView = gl.GetUniformLocationString(_program, "uView");
        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        _ebo = gl.GenBuffer();
        _upload = true;
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        gl.DeleteBuffer(_vbo);
        gl.DeleteBuffer(_ebo);
        gl.DeleteVertexArray(_vao);
        gl.DeleteProgram(_program);
    }

    private unsafe void Upload(GlInterface gl)
    {
        _upload = false;
        _count = 0;
        if (_mesh is not { } m) return;
        var n = m.Positions.Length / 3;
        var data = new float[n * 9];
        for (var i = 0; i < n; i++)
        {
            data[i * 9 + 0] = m.Positions[i * 3]; data[i * 9 + 1] = m.Positions[i * 3 + 1]; data[i * 9 + 2] = m.Positions[i * 3 + 2];
            data[i * 9 + 3] = m.Normals[i * 3]; data[i * 9 + 4] = m.Normals[i * 3 + 1]; data[i * 9 + 5] = m.Normals[i * 3 + 2];
            data[i * 9 + 6] = m.Colors[i * 3]; data[i * 9 + 7] = m.Colors[i * 3 + 1]; data[i * 9 + 8] = m.Colors[i * 3 + 2];
        }
        gl.BindVertexArray(_vao);
        gl.BindBuffer(GL_ARRAY_BUFFER, _vbo);
        fixed (float* p = data) gl.BufferData(GL_ARRAY_BUFFER, new IntPtr(data.Length * 4), new IntPtr(p), GL_STATIC_DRAW);
        gl.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, _ebo);
        fixed (uint* p = m.Indices) gl.BufferData(GL_ELEMENT_ARRAY_BUFFER, new IntPtr(m.Indices.Length * 4), new IntPtr(p), GL_STATIC_DRAW);
        for (var a = 0; a < 3; a++)
        {
            gl.VertexAttribPointer(a, 3, GL_FLOAT, 0, 36, new IntPtr(a * 12));
            gl.EnableVertexAttribArray(a);
        }
        _count = m.Indices.Length;
    }

    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        var scale = VisualRoot?.RenderScaling ?? 1;
        var w = (int)(Bounds.Width * scale); var h = (int)(Bounds.Height * scale);
        gl.Viewport(0, 0, w, h);
        gl.ClearColor(0.043f, 0.055f, 0.075f, 1);
        gl.Clear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
        if (_upload) Upload(gl);
        if (_count == 0 || _mesh is not { } m || w == 0 || h == 0) return;

        gl.Enable(GL_DEPTH_TEST);
        gl.DepthFunc(GL_LEQUAL);
        gl.UseProgram(_program);
        gl.BindVertexArray(_vao);

        // Board coordinates: X right, Y up the board, Z out of the top side. The camera orbits the centre.
        var centre = (m.Min + m.Max) / 2;
        var yaw = MathF.PI / 180 * _yaw; var pitch = MathF.PI / 180 * _pitch;
        var dir = new Vector3(MathF.Sin(yaw) * MathF.Sin(pitch), -MathF.Cos(yaw) * MathF.Sin(pitch), MathF.Cos(pitch));
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, dir) is { } r && r.LengthSquared() > 1e-6 ? r : Vector3.UnitX);
        var up = Vector3.Cross(dir, right);
        var target = centre + right * _pan.X + up * _pan.Y;
        var view = Matrix4x4.CreateLookAt(target + dir * _distance, target, up);
        var size = Vector3.Distance(m.Min, m.Max);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4.5f, (float)w / h, Math.Max(0.1f, _distance - size * 2), _distance + size * 2);
        var mvp = view * proj;
        gl.UniformMatrix4fv(_uMvp, 1, false, &mvp);
        gl.UniformMatrix4fv(_uView, 1, false, &view);
        gl.DrawElements(GL_TRIANGLES, _count, GL_UNSIGNED_INT, IntPtr.Zero);
        gl.BindVertexArray(0);
        gl.UseProgram(0);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.ClickCount == 2) { ResetView(); return; }
        _drag = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        _panning = props.IsRightButtonPressed || props.IsMiddleButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not { } from) return;
        var p = e.GetPosition(this);
        var d = p - from;
        _drag = p;
        if (_panning)
        {
            var k = _distance / (float)Math.Max(200, Bounds.Height) * 0.9f;
            _pan += new Vector2((float)-d.X * k, (float)d.Y * k);
        }
        else
        {
            _yaw -= (float)d.X * 0.4f;
            _pitch = Math.Clamp(_pitch - (float)d.Y * 0.4f, 0f, 179f);
        }
        RequestNextFrameRendering();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _distance = Math.Clamp(_distance * MathF.Pow(0.88f, (float)e.Delta.Y), 5, 2000);
        RequestNextFrameRendering();
    }
}
