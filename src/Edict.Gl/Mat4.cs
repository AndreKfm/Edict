// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Gl;

/// <summary>A column-major 4x4 matrix as OpenGL takes it, with GL 1.x's matrix calls (glRotatef, glTranslatef, gluPerspective).</summary>
internal unsafe struct Mat4
{
    public fixed float M[16];

    public static Mat4 Identity()
    {
        Mat4 m;
        for (int i = 0; i < 16; i++)
            m.M[i] = i % 5 == 0 ? 1 : 0;
        return m;
    }

    /// <summary>a * b.</summary>
    public static Mat4 Mul(in Mat4 a, in Mat4 b)
    {
        Mat4 r;
        for (int c = 0; c < 4; c++)
            for (int row = 0; row < 4; row++)
            {
                float s = 0;
                for (int k = 0; k < 4; k++)
                    s += a.M[k * 4 + row] * b.M[c * 4 + k];
                r.M[c * 4 + row] = s;
            }
        return r;
    }

    /// <summary>glRotatef: this * rotation by the angle (degrees) about the axis.</summary>
    public Mat4 Rotate(float degrees, float x, float y, float z)
    {
        double a = degrees * Math.PI / 180;
        float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
        float len = MathF.Sqrt(x * x + y * y + z * z);
        x /= len;
        y /= len;
        z /= len;
        Mat4 r = Identity();
        r.M[0] = x * x * (1 - c) + c;
        r.M[1] = y * x * (1 - c) + z * s;
        r.M[2] = x * z * (1 - c) - y * s;
        r.M[4] = x * y * (1 - c) - z * s;
        r.M[5] = y * y * (1 - c) + c;
        r.M[6] = y * z * (1 - c) + x * s;
        r.M[8] = x * z * (1 - c) + y * s;
        r.M[9] = y * z * (1 - c) - x * s;
        r.M[10] = z * z * (1 - c) + c;
        return Mul(this, r);
    }

    /// <summary>glTranslatef: this * translation.</summary>
    public Mat4 Translate(float x, float y, float z)
    {
        Mat4 t = Identity();
        t.M[12] = x;
        t.M[13] = y;
        t.M[14] = z;
        return Mul(this, t);
    }

    /// <summary>MYgluPerspective (gl_rmain.c): fov_y in degrees.</summary>
    public static Mat4 Perspective(double fovy, double aspect, double zNear, double zFar)
    {
        double ymax = zNear * Math.Tan(fovy * Math.PI / 360.0);
        double ymin = -ymax, xmin = ymin * aspect, xmax = ymax * aspect;
        Mat4 m = default;
        m.M[0] = (float)(2 * zNear / (xmax - xmin));
        m.M[5] = (float)(2 * zNear / (ymax - ymin));
        m.M[8] = (float)((xmax + xmin) / (xmax - xmin));
        m.M[9] = (float)((ymax + ymin) / (ymax - ymin));
        m.M[10] = (float)(-(zFar + zNear) / (zFar - zNear));
        m.M[11] = -1;
        m.M[14] = (float)(-2 * zFar * zNear / (zFar - zNear));
        return m;
    }

    /// <summary>Whether the box can be inside the clip volume of this (a model-view-projection) matrix.</summary>
    public bool BoxVisible(float minx, float miny, float minz, float maxx, float maxy, float maxz)
    {
        // the six planes of the clip volume (rows: w +- x, w +- y, w +- z)
        for (int p = 0; p < 6; p++)
        {
            int row = p >> 1;
            float sign = (p & 1) == 0 ? 1 : -1;
            float a = M[3] + sign * M[row], b = M[7] + sign * M[4 + row], c = M[11] + sign * M[8 + row],
                d = M[15] + sign * M[12 + row];
            // the box's corner furthest along the plane's normal
            float x = a >= 0 ? maxx : minx, y = b >= 0 ? maxy : miny, z = c >= 0 ? maxz : minz;
            if (a * x + b * y + c * z + d < 0)
                return false;
        }
        return true;
    }
}
