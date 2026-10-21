// Copyright (C) 1996-1997 Id Software, Inc.
// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Gl;

/// <summary>The renderer's GLSL 3.30 core shaders.</summary>
internal static class Shaders
{
    public const string CompositeVs = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        out vec2 vUv;
        void main()
        {
            vUv = vec2((aPos.x + 1.0) * 0.5, 1.0 - (aPos.y + 1.0) * 0.5);
            gl_Position = vec4(aPos, 0.0, 1.0);
        }
        """;

    // the 2D layer through the palette; its zero pixels in the view's rectangle let the 3D view show through
    public const string CompositeFs = """
        #version 330 core
        uniform sampler2D uFrame;
        uniform sampler2D uPalette;
        uniform vec4 uVrect;
        uniform int uViewDrawn;
        in vec2 vUv;
        out vec4 oColor;
        void main()
        {
            ivec2 p = ivec2(vUv * vec2(320.0, 200.0));
            int i = int(texelFetch(uFrame, p, 0).r * 255.0 + 0.5);
            if (uViewDrawn == 1 && i == 0 && p.x >= int(uVrect.x) && p.x < int(uVrect.z) && p.y >= int(uVrect.y) && p.y < int(uVrect.w))
                discard;
            oColor = texelFetch(uPalette, ivec2(i, 0), 0);
        }
        """;

    // world and brush entity surfaces: texture, lightmap (stored halved: up to twice the texture), turbulence, sky
    public const string SurfaceVs = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aTex;
        layout(location = 2) in vec2 aLm;
        uniform mat4 uMvp;
        uniform mat4 uModel;
        out vec2 vTex;
        out vec2 vLm;
        out vec3 vWorld;
        void main()
        {
            vTex = aTex;
            vLm = aLm;
            vWorld = (uModel * vec4(aPos, 1.0)).xyz;
            gl_Position = uMvp * vec4(aPos, 1.0);
        }
        """;

    public const string SurfaceFs = """
        #version 330 core
        uniform sampler2D uTex;
        uniform sampler2D uLm;
        uniform sampler2D uSkyAlpha;
        uniform vec2 uTexSize;
        uniform int uMode;          // 0 lightmapped, 1 turbulent, 2 sky
        uniform float uTime;
        uniform vec3 uEye;
        uniform vec3 uTintK;
        uniform vec3 uTintM;
        uniform float uAlpha;
        in vec2 vTex;
        in vec2 vLm;
        in vec3 vWorld;
        out vec4 oColor;
        void main()
        {
            vec3 c;
            if (uMode == 1)
            {
                // EmitWaterPolys: s + 8 sin (t / 8 + time), t + 8 sin (s / 8 + time)
                vec2 st = vec2(vTex.x + 8.0 * sin(vTex.y * 0.125 + uTime), vTex.y + 8.0 * sin(vTex.x * 0.125 + uTime));
                c = texture(uTex, st / 64.0).rgb;
            }
            else if (uMode == 2)
            {
                // EmitSkyPolys: the direction squashed in z, 6 * 63 units out, two layers moving at 8 and 16
                vec3 dir = vWorld - uEye;
                dir.z *= 3.0;
                dir.xy *= 6.0 * 63.0 / length(dir);
                vec2 solid = (vec2(uTime * 8.0) + dir.xy) / 128.0;
                vec2 alpha = (vec2(uTime * 16.0) + dir.xy) / 128.0;
                vec4 a = texture(uSkyAlpha, alpha);
                c = mix(texture(uTex, solid).rgb, a.rgb, a.a);
            }
            else
                c = texture(uTex, vTex / uTexSize).rgb * texture(uLm, vLm).r * 2.0;
            oColor = vec4(c * uTintK + uTintM, uAlpha);
        }
        """;

    // alias models and sprites: a texture, a per-vertex light, the tint
    public const string ModelVs = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aTex;
        layout(location = 2) in float aLight;
        uniform mat4 uMvp;
        out vec2 vTex;
        out float vLight;
        void main()
        {
            vTex = aTex;
            vLight = aLight;
            gl_Position = uMvp * vec4(aPos, 1.0);
        }
        """;

    public const string ModelFs = """
        #version 330 core
        uniform sampler2D uTex;
        uniform vec3 uTintK;
        uniform vec3 uTintM;
        uniform int uAlphaTest;
        in vec2 vTex;
        in float vLight;
        out vec4 oColor;
        void main()
        {
            vec4 t = texture(uTex, vTex);
            if (uAlphaTest == 1 && t.a < 0.5)
                discard;
            oColor = vec4(t.rgb * vLight * uTintK + uTintM, 1.0);
        }
        """;

    // particles: a colored round point on a quad
    public const string ParticleVs = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aCorner;
        layout(location = 2) in vec3 aColor;
        uniform mat4 uMvp;
        out vec2 vCorner;
        out vec3 vColor;
        void main()
        {
            vCorner = aCorner;
            vColor = aColor;
            gl_Position = uMvp * vec4(aPos, 1.0);
        }
        """;

    public const string ParticleFs = """
        #version 330 core
        uniform vec3 uTintK;
        uniform vec3 uTintM;
        in vec2 vCorner;
        in vec3 vColor;
        out vec4 oColor;
        void main()
        {
            float d = dot(vCorner, vCorner);
            if (d > 1.0)
                discard;
            oColor = vec4(vColor * uTintK + uTintM, 1.0);
        }
        """;
}
