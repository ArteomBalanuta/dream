using Dream.Simulation.World;

namespace Dream.Rendering.Core;

﻿using OpenTK.Graphics.OpenGL4;
using System;

public static class GLUtils
{
    public static void CheckGLError(string context)
    {
        var error = GL.GetError();
        if (error != ErrorCode.NoError)
        {
            Console.WriteLine($"[OpenGL Error] {error} in {context}");
        }
    }
}
