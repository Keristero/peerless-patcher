using System.Numerics;
using System.Runtime.CompilerServices;
using ImGuiNET;
using Silk.NET.OpenGL;

namespace PeerlessPatcher.UI;

/// <summary>
/// Minimal ImGui renderer for Silk.NET/OpenGL.
/// Handles font atlas upload, draw-data submission, and per-frame input propagation.
/// </summary>
internal sealed class ImGuiController : IDisposable
{
    private readonly GL _gl;
    private int _windowWidth;
    private int _windowHeight;

    // GL objects
    private uint _vbo, _ebo, _vao;
    private uint _shader;
    private uint _fontTexture;
    private int _attribLocationTex, _attribLocationProjMtx;
    private int _attribLocationVtxPos, _attribLocationVtxUV, _attribLocationVtxColor;

    public ImGuiController(GL gl, int width, int height)
    {
        _gl = gl;
        _windowWidth = width;
        _windowHeight = height;

        var context = ImGui.CreateContext();
        ImGui.SetCurrentContext(context);

        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(width, height);
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        CreateDeviceObjects();
    }

    public void Resize(int w, int h)
    {
        _windowWidth = w;
        _windowHeight = h;
        ImGui.GetIO().DisplaySize = new Vector2(w, h);
    }

    public void NewFrame(double deltaTime)
    {
        var io = ImGui.GetIO();
        io.DeltaTime = (float)deltaTime;
        ImGui.NewFrame();
    }

    public void Render()
    {
        ImGui.Render();
        RenderDrawData(ImGui.GetDrawData());
    }

    // ── Input helpers (called by the window event handlers) ───────────────────

    public void AddKeyEvent(ImGuiKey key, bool down) => ImGui.GetIO().AddKeyEvent(key, down);
    public void AddInputCharacter(uint c) => ImGui.GetIO().AddInputCharactersUTF8(char.ConvertFromUtf32((int)c));
    public void SetMousePos(float x, float y) => ImGui.GetIO().MousePos = new Vector2(x, y);
    public void SetMouseButton(int btn, bool down) => ImGui.GetIO().MouseDown[btn] = down;
    public void SetMouseWheel(float x, float y) { ImGui.GetIO().MouseWheelH += x; ImGui.GetIO().MouseWheel += y; }

    // ── Device objects ────────────────────────────────────────────────────────

    private unsafe void CreateDeviceObjects()
    {
        const string vertSrc = @"#version 330 core
layout(location=0) in vec2 Position;
layout(location=1) in vec2 UV;
layout(location=2) in vec4 Color;
uniform mat4 ProjMtx;
out vec2 Frag_UV;
out vec4 Frag_Color;
void main()
{
    Frag_UV = UV;
    Frag_Color = Color;
    gl_Position = ProjMtx * vec4(Position.xy, 0, 1);
}";
        const string fragSrc = @"#version 330 core
in vec2 Frag_UV;
in vec4 Frag_Color;
uniform sampler2D Texture;
out vec4 Out_Color;
void main()
{
    Out_Color = Frag_Color * texture(Texture, Frag_UV.st);
}";

        _shader = CreateShader(vertSrc, fragSrc);
        _attribLocationTex = _gl.GetUniformLocation(_shader, "Texture");
        _attribLocationProjMtx = _gl.GetUniformLocation(_shader, "ProjMtx");
        _attribLocationVtxPos = _gl.GetAttribLocation(_shader, "Position");
        _attribLocationVtxUV = _gl.GetAttribLocation(_shader, "UV");
        _attribLocationVtxColor = _gl.GetAttribLocation(_shader, "Color");

        _vbo = _gl.GenBuffer();
        _ebo = _gl.GenBuffer();
        _vao = _gl.GenVertexArray();

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);

        var stride = (uint)sizeof(ImDrawVert);
        _gl.EnableVertexAttribArray((uint)_attribLocationVtxPos);
        _gl.VertexAttribPointer((uint)_attribLocationVtxPos, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray((uint)_attribLocationVtxUV);
        _gl.VertexAttribPointer((uint)_attribLocationVtxUV, 2, VertexAttribPointerType.Float, false, stride, (void*)(2 * sizeof(float)));
        _gl.EnableVertexAttribArray((uint)_attribLocationVtxColor);
        _gl.VertexAttribPointer((uint)_attribLocationVtxColor, 4, VertexAttribPointerType.UnsignedByte, true, stride, (void*)(4 * sizeof(float)));

        UploadFontTexture();
    }

    private unsafe void UploadFontTexture()
    {
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int w, out int h);

        _fontTexture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _fontTexture);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        io.Fonts.SetTexID((IntPtr)_fontTexture);
        io.Fonts.ClearTexData();
    }

    private uint CreateShader(string vert, string frag)
    {
        uint v = _gl.CreateShader(ShaderType.VertexShader);
        _gl.ShaderSource(v, vert);
        _gl.CompileShader(v);
        CheckShader(v, "vertex");

        uint f = _gl.CreateShader(ShaderType.FragmentShader);
        _gl.ShaderSource(f, frag);
        _gl.CompileShader(f);
        CheckShader(f, "fragment");

        uint prog = _gl.CreateProgram();
        _gl.AttachShader(prog, v);
        _gl.AttachShader(prog, f);
        _gl.LinkProgram(prog);
        _gl.DeleteShader(v);
        _gl.DeleteShader(f);
        return prog;
    }

    private void CheckShader(uint shader, string label)
    {
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int status);
        if (status == 0)
            throw new Exception($"ImGui {label} shader compile error: {_gl.GetShaderInfoLog(shader)}");
    }

    private unsafe void RenderDrawData(ImDrawDataPtr data)
    {
        if (data.CmdListsCount == 0) return;

        int fbW = (int)(data.DisplaySize.X * data.FramebufferScale.X);
        int fbH = (int)(data.DisplaySize.Y * data.FramebufferScale.Y);
        if (fbW <= 0 || fbH <= 0) return;

        _gl.Enable(EnableCap.Blend);
        _gl.BlendEquation(BlendEquationModeEXT.FuncAdd);
        _gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.StencilTest);
        _gl.Enable(EnableCap.ScissorTest);

        _gl.Viewport(0, 0, (uint)fbW, (uint)fbH);

        float L = data.DisplayPos.X, R = data.DisplayPos.X + data.DisplaySize.X;
        float T = data.DisplayPos.Y, B = data.DisplayPos.Y + data.DisplaySize.Y;
        Span<float> mtx = stackalloc float[16]
        {
            2f/(R-L), 0, 0, 0,
            0, 2f/(T-B), 0, 0,
            0, 0, -1, 0,
            (R+L)/(L-R), (T+B)/(B-T), 0, 1,
        };

        _gl.UseProgram(_shader);
        _gl.Uniform1(_attribLocationTex, 0);
        _gl.UniformMatrix4(_attribLocationProjMtx, 1, false, mtx);
        _gl.BindVertexArray(_vao);

        var clipOff = data.DisplayPos;
        var clipScale = data.FramebufferScale;

        for (int n = 0; n < data.CmdListsCount; n++)
        {
            var cmdList = data.CmdLists[n];

            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(cmdList.VtxBuffer.Size * sizeof(ImDrawVert)),
                (void*)cmdList.VtxBuffer.Data, BufferUsageARB.StreamDraw);

            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(cmdList.IdxBuffer.Size * sizeof(ushort)),
                (void*)cmdList.IdxBuffer.Data, BufferUsageARB.StreamDraw);

            for (int cmdI = 0; cmdI < cmdList.CmdBuffer.Size; cmdI++)
            {
                var cmd = cmdList.CmdBuffer[cmdI];
                if (cmd.UserCallback != IntPtr.Zero) continue;

                var clipMin = new Vector2((cmd.ClipRect.X - clipOff.X) * clipScale.X,
                                          (cmd.ClipRect.Y - clipOff.Y) * clipScale.Y);
                var clipMax = new Vector2((cmd.ClipRect.Z - clipOff.X) * clipScale.X,
                                          (cmd.ClipRect.W - clipOff.Y) * clipScale.Y);
                if (clipMax.X <= clipMin.X || clipMax.Y <= clipMin.Y) continue;

                _gl.Scissor((int)clipMin.X, (int)(fbH - clipMax.Y), (uint)(clipMax.X - clipMin.X), (uint)(clipMax.Y - clipMin.Y));
                _gl.BindTexture(TextureTarget.Texture2D, (uint)cmd.TextureId.ToInt32());
                _gl.DrawElementsBaseVertex(PrimitiveType.Triangles, cmd.ElemCount,
                    DrawElementsType.UnsignedShort,
                    (void*)(cmd.IdxOffset * sizeof(ushort)),
                    (int)cmd.VtxOffset);
            }
        }

        _gl.Disable(EnableCap.ScissorTest);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteProgram(_shader);
        _gl.DeleteTexture(_fontTexture);
        ImGui.DestroyContext();
    }
}
