using Microsoft.Maui.Graphics;
using ARGuidanceMAUI.Models;
using System.Numerics;

#if ANDROID
using Android.Opengl;
using Javax.Microedition.Khronos.Opengles;
using Java.Nio;
#endif

#if IOS
using Foundation;
using CoreGraphics;
using GLKit;
#endif

namespace ARGuidanceMAUI.Views;

public class FeaturePointsDrawable : IDrawable
{
    private ArDebugTelemetry? _telemetry;

#if IOS
    private static IOSCubeRenderer? _cubeRenderer;
#endif

    public void Update(ArDebugTelemetry telemetry) => _telemetry = telemetry;

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
    }

#if ANDROID
    // New OpenGL-based feature point rendering method
    public static void RenderFeaturePointsOpenGL(FeaturePoint[] allPoints, FeaturePoint[] filteredPoints, int screenWidth, int screenHeight)
    {
        if (allPoints.Length == 0 && filteredPoints.Length == 0) return;

        // Enable blending for point rendering
        GLES20.GlEnable(GLES20.GlBlend);
        GLES20.GlBlendFunc(GLES20.GlSrcAlpha, GLES20.GlOneMinusSrcAlpha);

        // Create simple shader program for points
        var vertexShader = @"
            attribute vec4 a_Position;
            attribute float a_PointSize;
            attribute vec4 a_Color;
            varying vec4 v_Color;
            void main() {
                gl_Position = a_Position;
                gl_PointSize = a_PointSize;
                v_Color = a_Color;
            }";

        var fragmentShader = @"
            precision mediump float;
            varying vec4 v_Color;
            void main() {
                float dist = distance(gl_PointCoord, vec2(0.5));
                if (dist > 0.5) discard;
                gl_FragColor = v_Color;
            }";

        var program = CreateShaderProgram(vertexShader, fragmentShader);
        if (program == 0) return;

        GLES20.GlUseProgram(program);

        var aPosition = GLES20.GlGetAttribLocation(program, "a_Position");
        var aPointSize = GLES20.GlGetAttribLocation(program, "a_PointSize");
        var aColor = GLES20.GlGetAttribLocation(program, "a_Color");

        // Render all points (yellow)
        RenderPointsWithColor(allPoints, aPosition, aPointSize, aColor, screenWidth, screenHeight, 
            [1.0f, 1.0f, 0.0f, 1.0f], 10.0f);

        // Render filtered points (green) - these will be on top
        RenderPointsWithColor(filteredPoints, aPosition, aPointSize, aColor, screenWidth, screenHeight, 
            [0.0f, 1.0f, 0.0f, 1.0f], 10.0f);

        GLES20.GlDisable(GLES20.GlBlend);
        GLES20.GlUseProgram(0);
        GLES20.GlDeleteProgram(program);
    }

    private static void RenderPointsWithColor(FeaturePoint[] points, int aPosition, int aPointSize, int aColor, 
        int screenWidth, int screenHeight, float[] color, float pointSize)
    {
        if (points.Length == 0) return;

        // Use appropriate scale factors for converting from normalized coordinates to screen space
        var scaleX = screenWidth * 0.5f;  // Scale factor for X coordinates
        var scaleY = screenHeight * 0.5f; // Scale factor for Y coordinates
        var centerX = screenWidth / 2f;
        var centerY = screenHeight / 2f;

        var vertices = new float[points.Length * 2];
        for (int i = 0; i < points.Length; i++)
        {
            // Convert 3D world coordinates to normalized device coordinates
            // Assuming points[i].X and points[i].Y are in normalized space [-1, 1]
            var screenX = (centerX + points[i].X * scaleX) / screenWidth * 2.0f - 1.0f;
            var screenY = 1.0f - (centerY - points[i].Y * scaleY) / screenHeight * 2.0f; // Flip Y axis
            
            vertices[i * 2] = screenX;
            vertices[i * 2 + 1] = screenY;
        }

        var vertexBuffer = ByteBuffer.AllocateDirect(vertices.Length * sizeof(float))
            .Order(ByteOrder.NativeOrder() ?? ByteOrder.LittleEndian!)
            .AsFloatBuffer();
        vertexBuffer.Put(vertices);
        vertexBuffer.Position(0);

        GLES20.GlVertexAttribPointer(aPosition, 2, GLES20.GlFloat, false, 0, vertexBuffer);
        GLES20.GlEnableVertexAttribArray(aPosition);

        GLES20.GlVertexAttrib1f(aPointSize, pointSize);
        GLES20.GlVertexAttrib4f(aColor, color[0], color[1], color[2], color[3]);

        GLES20.GlDrawArrays(GLES20.GlPoints, 0, points.Length);

        GLES20.GlDisableVertexAttribArray(aPosition);
    }

    private static int CreateShaderProgram(string vertexSource, string fragmentSource)
    {
        var vertexShader = LoadShader(GLES20.GlVertexShader, vertexSource);
        var fragmentShader = LoadShader(GLES20.GlFragmentShader, fragmentSource);

        if (vertexShader == 0 || fragmentShader == 0) return 0;

        var program = GLES20.GlCreateProgram();
        GLES20.GlAttachShader(program, vertexShader);
        GLES20.GlAttachShader(program, fragmentShader);
        GLES20.GlLinkProgram(program);

        var linkStatus = new int[1];
        GLES20.GlGetProgramiv(program, GLES20.GlLinkStatus, linkStatus, 0);
        if (linkStatus[0] != GLES20.GlTrue)
        {
            GLES20.GlDeleteProgram(program);
            return 0;
        }

        GLES20.GlDeleteShader(vertexShader);
        GLES20.GlDeleteShader(fragmentShader);

        return program;
    }

    private static int LoadShader(int type, string source)
    {
        var shader = GLES20.GlCreateShader(type);
        GLES20.GlShaderSource(shader, source);
        GLES20.GlCompileShader(shader);

        var compileStatus = new int[1];
        GLES20.GlGetShaderiv(shader, GLES20.GlCompileStatus, compileStatus, 0);
        if (compileStatus[0] != GLES20.GlTrue)
        {
            GLES20.GlDeleteShader(shader);
            return 0;
        }

        return shader;
    }
#endif

#if IOS
    // iOS OpenGL-based cube rendering method
    public static void RenderCubeOpenGL(Matrix4x4 viewProjectionMatrix, Vector3 position, Vector3 scale, Vector3 rotation)
    {
        try
        {
            // Initialize cube renderer if needed
            _cubeRenderer ??= new IOSCubeRenderer();

            // Render the cube
            _cubeRenderer.RenderCube(viewProjectionMatrix, position, scale, rotation);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"iOS cube rendering failed: {ex.Message}");
        }
    }

    // Helper method to dispose iOS resources
    public static void DisposeIOSResources()
    {
        _cubeRenderer?.Dispose();
        _cubeRenderer = null;
    }
#endif
}