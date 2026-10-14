// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

namespace Edict.Gl;

/// <summary>
/// The OpenGL 3.3 core entry points the renderer uses, as function pointers the platform's loader resolves
/// (wglGetProcAddress or glXGetProcAddress, opengl32's exports for 1.1). No package.
/// </summary>
public static unsafe class Gl
{
    public const uint DepthBufferBit = 0x100, ColorBufferBit = 0x4000;
    public const uint Triangles = 0x0004, TriangleStrip = 0x0005, TriangleFan = 0x0006;
    public const uint DepthTest = 0x0B71, Blend = 0x0BE2, CullFaceCap = 0x0B44, ScissorTest = 0x0C11, PolygonOffsetFill = 0x8037;
    public const uint SrcAlpha = 0x0302, OneMinusSrcAlpha = 0x0303, One = 1, Zero = 0, DstColor = 0x0306;
    public const uint Less = 0x0201, LEqual = 0x0203, Always = 0x0207;
    public const uint Front = 0x0404, Back = 0x0405, Cw = 0x0900, Ccw = 0x0901;
    public const uint Texture2D = 0x0DE1, Texture0 = 0x84C0;
    public const uint TextureMagFilter = 0x2800, TextureMinFilter = 0x2801, TextureWrapS = 0x2802, TextureWrapT = 0x2803;
    public const int Nearest = 0x2600, Linear = 0x2601, LinearMipmapLinear = 0x2703, Repeat = 0x2901, ClampToEdge = 0x812F;
    public const uint Rgba = 0x1908, Rgba8 = 0x8058, Red = 0x1903, R8 = 0x8229, Rgb = 0x1907, UnsignedByte = 0x1401, Float = 0x1406,
        UnsignedShort = 0x1403, UnsignedInt = 0x1405;
    public const uint UnpackAlignment = 0x0CF5, PackAlignment = 0x0D05;
    public const uint ArrayBuffer = 0x8892, ElementArrayBuffer = 0x8893, StreamDraw = 0x88E0, StaticDraw = 0x88E4, DynamicDraw = 0x88E8;
    public const uint FragmentShader = 0x8B30, VertexShader = 0x8B31, CompileStatus = 0x8B81, LinkStatus = 0x8B82, InfoLogLength = 0x8B84;
    public const uint Framebuffer = 0x8D40, Renderbuffer = 0x8D41, ColorAttachment0 = 0x8CE0, DepthAttachment = 0x8D00,
        DepthComponent24 = 0x81A6, FramebufferComplete = 0x8CD5;

    public static delegate* unmanaged<int, int, int, int, void> Viewport;
    public static delegate* unmanaged<int, int, int, int, void> Scissor;
    public static delegate* unmanaged<float, float, float, float, void> ClearColor;
    public static delegate* unmanaged<uint, void> Clear;
    public static delegate* unmanaged<uint, void> Enable;
    public static delegate* unmanaged<uint, void> Disable;
    public static delegate* unmanaged<uint, uint, void> BlendFunc;
    public static delegate* unmanaged<uint, void> DepthFunc;
    public static delegate* unmanaged<byte, void> DepthMask;
    public static delegate* unmanaged<double, double, void> DepthRange;
    public static delegate* unmanaged<uint, void> CullFace;
    public static delegate* unmanaged<uint, void> FrontFace;
    public static delegate* unmanaged<float, float, void> PolygonOffset;
    public static delegate* unmanaged<uint> GetError;
    public static delegate* unmanaged<int, uint*, void> GenTextures;
    public static delegate* unmanaged<int, uint*, void> DeleteTextures;
    public static delegate* unmanaged<uint, uint, void> BindTexture;
    public static delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> TexImage2D;
    public static delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> TexSubImage2D;
    public static delegate* unmanaged<uint, uint, int, void> TexParameteri;
    public static delegate* unmanaged<uint, void> ActiveTexture;
    public static delegate* unmanaged<uint, void> GenerateMipmap;
    public static delegate* unmanaged<uint, int, void> PixelStorei;
    public static delegate* unmanaged<int, uint*, void> GenBuffers;
    public static delegate* unmanaged<int, uint*, void> DeleteBuffers;
    public static delegate* unmanaged<uint, uint, void> BindBuffer;
    public static delegate* unmanaged<uint, nint, void*, uint, void> BufferData;
    public static delegate* unmanaged<uint, nint, nint, void*, void> BufferSubData;
    public static delegate* unmanaged<int, uint*, void> GenVertexArrays;
    public static delegate* unmanaged<int, uint*, void> DeleteVertexArrays;
    public static delegate* unmanaged<uint, void> BindVertexArray;
    public static delegate* unmanaged<uint, int, uint, byte, int, void*, void> VertexAttribPointer;
    public static delegate* unmanaged<uint, void> EnableVertexAttribArray;
    public static delegate* unmanaged<uint, uint> CreateShader;
    public static delegate* unmanaged<uint, int, byte**, int*, void> ShaderSource;
    public static delegate* unmanaged<uint, void> CompileShader;
    public static delegate* unmanaged<uint, uint, int*, void> GetShaderiv;
    public static delegate* unmanaged<uint, int, int*, byte*, void> GetShaderInfoLog;
    public static delegate* unmanaged<uint, void> DeleteShader;
    public static delegate* unmanaged<uint> CreateProgram;
    public static delegate* unmanaged<uint, uint, void> AttachShader;
    public static delegate* unmanaged<uint, uint, byte*, void> BindAttribLocation;
    public static delegate* unmanaged<uint, void> LinkProgram;
    public static delegate* unmanaged<uint, uint, int*, void> GetProgramiv;
    public static delegate* unmanaged<uint, int, int*, byte*, void> GetProgramInfoLog;
    public static delegate* unmanaged<uint, void> UseProgram;
    public static delegate* unmanaged<uint, byte*, int> GetUniformLocation;
    public static delegate* unmanaged<int, int, void> Uniform1i;
    public static delegate* unmanaged<int, float, void> Uniform1f;
    public static delegate* unmanaged<int, float, float, void> Uniform2f;
    public static delegate* unmanaged<int, float, float, float, void> Uniform3f;
    public static delegate* unmanaged<int, float, float, float, float, void> Uniform4f;
    public static delegate* unmanaged<int, int, float*, void> Uniform4fv;
    public static delegate* unmanaged<int, int, byte, float*, void> UniformMatrix4fv;
    public static delegate* unmanaged<uint, int, int, void> DrawArrays;
    public static delegate* unmanaged<uint, int, uint, void*, void> DrawElements;
    public static delegate* unmanaged<int, uint*, void> GenFramebuffers;
    public static delegate* unmanaged<uint, uint, void> BindFramebuffer;
    public static delegate* unmanaged<uint, uint, uint, uint, int, void> FramebufferTexture2D;
    public static delegate* unmanaged<int, uint*, void> GenRenderbuffers;
    public static delegate* unmanaged<uint, uint, void> BindRenderbuffer;
    public static delegate* unmanaged<uint, uint, int, int, void> RenderbufferStorage;
    public static delegate* unmanaged<uint, uint, uint, uint, void> FramebufferRenderbuffer;
    public static delegate* unmanaged<uint, uint> CheckFramebufferStatus;
    public static delegate* unmanaged<int, int, int, int, uint, uint, void*, void> ReadPixels;

    /// <summary>Resolves every entry point through the platform's loader; a missing one is an error.</summary>
    public static void Load(Func<string, nint> getProc)
    {
        nint P(string name)
        {
            nint p = getProc(name);
            return p != 0 ? p : throw new InvalidOperationException($"OpenGL: {name} not found (3.3 core needed)");
        }
        Viewport = (delegate* unmanaged<int, int, int, int, void>)P("glViewport");
        Scissor = (delegate* unmanaged<int, int, int, int, void>)P("glScissor");
        ClearColor = (delegate* unmanaged<float, float, float, float, void>)P("glClearColor");
        Clear = (delegate* unmanaged<uint, void>)P("glClear");
        Enable = (delegate* unmanaged<uint, void>)P("glEnable");
        Disable = (delegate* unmanaged<uint, void>)P("glDisable");
        BlendFunc = (delegate* unmanaged<uint, uint, void>)P("glBlendFunc");
        DepthFunc = (delegate* unmanaged<uint, void>)P("glDepthFunc");
        DepthMask = (delegate* unmanaged<byte, void>)P("glDepthMask");
        DepthRange = (delegate* unmanaged<double, double, void>)P("glDepthRange");
        CullFace = (delegate* unmanaged<uint, void>)P("glCullFace");
        FrontFace = (delegate* unmanaged<uint, void>)P("glFrontFace");
        PolygonOffset = (delegate* unmanaged<float, float, void>)P("glPolygonOffset");
        GetError = (delegate* unmanaged<uint>)P("glGetError");
        GenTextures = (delegate* unmanaged<int, uint*, void>)P("glGenTextures");
        DeleteTextures = (delegate* unmanaged<int, uint*, void>)P("glDeleteTextures");
        BindTexture = (delegate* unmanaged<uint, uint, void>)P("glBindTexture");
        TexImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void>)P("glTexImage2D");
        TexSubImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void>)P("glTexSubImage2D");
        TexParameteri = (delegate* unmanaged<uint, uint, int, void>)P("glTexParameteri");
        ActiveTexture = (delegate* unmanaged<uint, void>)P("glActiveTexture");
        GenerateMipmap = (delegate* unmanaged<uint, void>)P("glGenerateMipmap");
        PixelStorei = (delegate* unmanaged<uint, int, void>)P("glPixelStorei");
        GenBuffers = (delegate* unmanaged<int, uint*, void>)P("glGenBuffers");
        DeleteBuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteBuffers");
        BindBuffer = (delegate* unmanaged<uint, uint, void>)P("glBindBuffer");
        BufferData = (delegate* unmanaged<uint, nint, void*, uint, void>)P("glBufferData");
        BufferSubData = (delegate* unmanaged<uint, nint, nint, void*, void>)P("glBufferSubData");
        GenVertexArrays = (delegate* unmanaged<int, uint*, void>)P("glGenVertexArrays");
        DeleteVertexArrays = (delegate* unmanaged<int, uint*, void>)P("glDeleteVertexArrays");
        BindVertexArray = (delegate* unmanaged<uint, void>)P("glBindVertexArray");
        VertexAttribPointer = (delegate* unmanaged<uint, int, uint, byte, int, void*, void>)P("glVertexAttribPointer");
        EnableVertexAttribArray = (delegate* unmanaged<uint, void>)P("glEnableVertexAttribArray");
        CreateShader = (delegate* unmanaged<uint, uint>)P("glCreateShader");
        ShaderSource = (delegate* unmanaged<uint, int, byte**, int*, void>)P("glShaderSource");
        CompileShader = (delegate* unmanaged<uint, void>)P("glCompileShader");
        GetShaderiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetShaderiv");
        GetShaderInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetShaderInfoLog");
        DeleteShader = (delegate* unmanaged<uint, void>)P("glDeleteShader");
        CreateProgram = (delegate* unmanaged<uint>)P("glCreateProgram");
        AttachShader = (delegate* unmanaged<uint, uint, void>)P("glAttachShader");
        BindAttribLocation = (delegate* unmanaged<uint, uint, byte*, void>)P("glBindAttribLocation");
        LinkProgram = (delegate* unmanaged<uint, void>)P("glLinkProgram");
        GetProgramiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetProgramiv");
        GetProgramInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetProgramInfoLog");
        UseProgram = (delegate* unmanaged<uint, void>)P("glUseProgram");
        GetUniformLocation = (delegate* unmanaged<uint, byte*, int>)P("glGetUniformLocation");
        Uniform1i = (delegate* unmanaged<int, int, void>)P("glUniform1i");
        Uniform1f = (delegate* unmanaged<int, float, void>)P("glUniform1f");
        Uniform2f = (delegate* unmanaged<int, float, float, void>)P("glUniform2f");
        Uniform3f = (delegate* unmanaged<int, float, float, float, void>)P("glUniform3f");
        Uniform4f = (delegate* unmanaged<int, float, float, float, float, void>)P("glUniform4f");
        Uniform4fv = (delegate* unmanaged<int, int, float*, void>)P("glUniform4fv");
        UniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>)P("glUniformMatrix4fv");
        DrawArrays = (delegate* unmanaged<uint, int, int, void>)P("glDrawArrays");
        DrawElements = (delegate* unmanaged<uint, int, uint, void*, void>)P("glDrawElements");
        GenFramebuffers = (delegate* unmanaged<int, uint*, void>)P("glGenFramebuffers");
        BindFramebuffer = (delegate* unmanaged<uint, uint, void>)P("glBindFramebuffer");
        FramebufferTexture2D = (delegate* unmanaged<uint, uint, uint, uint, int, void>)P("glFramebufferTexture2D");
        GenRenderbuffers = (delegate* unmanaged<int, uint*, void>)P("glGenRenderbuffers");
        BindRenderbuffer = (delegate* unmanaged<uint, uint, void>)P("glBindRenderbuffer");
        RenderbufferStorage = (delegate* unmanaged<uint, uint, int, int, void>)P("glRenderbufferStorage");
        FramebufferRenderbuffer = (delegate* unmanaged<uint, uint, uint, uint, void>)P("glFramebufferRenderbuffer");
        CheckFramebufferStatus = (delegate* unmanaged<uint, uint>)P("glCheckFramebufferStatus");
        ReadPixels = (delegate* unmanaged<int, int, int, int, uint, uint, void*, void>)P("glReadPixels");
    }

    /// <summary>A shader program from vertex and fragment source, with the attribute locations bound by name.</summary>
    public static uint Program(string vertex, string fragment, params string[] attributes)
    {
        uint vs = Shader(VertexShader, vertex), fs = Shader(FragmentShader, fragment);
        uint p = CreateProgram();
        AttachShader(p, vs);
        AttachShader(p, fs);
        for (int i = 0; i < attributes.Length; i++)
            fixed (byte* n = Encoding.ASCII.GetBytes(attributes[i] + "\0"))
                BindAttribLocation(p, (uint)i, n);
        LinkProgram(p);
        int ok;
        GetProgramiv(p, LinkStatus, &ok);
        if (ok == 0)
            throw new InvalidOperationException("OpenGL: link failed: " + Log(p, program: true));
        DeleteShader(vs);
        DeleteShader(fs);
        return p;
    }

    private static uint Shader(uint type, string source)
    {
        uint s = CreateShader(type);
        byte[] src = Encoding.ASCII.GetBytes(source);
        fixed (byte* b = src)
        {
            int len = src.Length;
            byte* bp = b;
            ShaderSource(s, 1, &bp, &len);
        }
        CompileShader(s);
        int ok;
        GetShaderiv(s, CompileStatus, &ok);
        if (ok == 0)
            throw new InvalidOperationException("OpenGL: compile failed: " + Log(s, program: false));
        return s;
    }

    private static string Log(uint o, bool program)
    {
        byte* buf = stackalloc byte[4096];
        int n;
        if (program)
            GetProgramInfoLog(o, 4096, &n, buf);
        else
            GetShaderInfoLog(o, 4096, &n, buf);
        return Encoding.ASCII.GetString(buf, n);
    }

    /// <summary>A uniform's location.</summary>
    public static int Uniform(uint program, string name)
    {
        fixed (byte* n = Encoding.ASCII.GetBytes(name + "\0"))
            return GetUniformLocation(program, n);
    }
}
