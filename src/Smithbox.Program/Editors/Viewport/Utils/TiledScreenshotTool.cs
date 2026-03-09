using Hexa.NET.ImGui;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using StudioCore.Application;
using StudioCore.Renderer;
using StudioCore.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Veldrid;
using Veldrid.Utilities;
using Vortice.Vulkan;

namespace StudioCore.Editors.Viewport;

public class TiledScreenshotTool
{
    private VulkanViewport _viewport;
    private int _multiplier = 2;

    public TiledScreenshotTool(VulkanViewport viewport)
    {
        _viewport = viewport;
    }

    public void OnGui()
    {
        if (ImGui.BeginMenu("High-Res Screenshot"))
        {
            if (ImGui.MenuItem("Frame Entire Map"))
            {
                FrameEntireMap();
            }
            UIHelper.Tooltip("Adjusts the camera to fit all visible map objects into the current view.");

            ImGui.Separator();

            ImGui.InputInt("Multiplier", ref _multiplier);
            if (_multiplier < 1) _multiplier = 1;
            if (_multiplier > 8) _multiplier = 8;

            if (ImGui.MenuItem("Capture High-Res Screenshot"))
            {
                Capture();
            }
            UIHelper.Tooltip($"Captures exactly what's in the viewport but at {_multiplier}x resolution.");

            ImGui.EndMenu();
        }
    }

    private void FrameEntireMap()
    {
        BoundingBox totalBounds = new BoundingBox(new Vector3(float.MaxValue), new Vector3(float.MinValue));
        bool anyFound = false;

        foreach (var bounds in _viewport.RenderScene.OpaqueRenderables.cBounds)
        {
            if (bounds.Min != bounds.Max)
            {
                totalBounds = BoundingBox.Combine(totalBounds, bounds);
                anyFound = true;
            }
        }

        if (!anyFound) return;

        var center = totalBounds.GetCenter();
        var dims = totalBounds.GetDimensions();
        float maxDim = Math.Max(dims.X, dims.Z);

        _viewport.ViewportCamera.SetProjectionType(ViewMode.Orthographic);
        _viewport.ViewportCamera.CameraTransform.Position = new Vector3(center.X, totalBounds.Max.Y + 1000.0f, center.Z);
        _viewport.ViewportCamera.CameraTransform.EulerRotation = new Vector3(-(float)Math.PI / 2.0f, 0, 0);
        _viewport.ViewportCamera.OrthographicSize = maxDim;
        _viewport.ViewportCamera.UpdateProjectionMatrix(true);
    }

    private unsafe void Capture()
    {
        string path;
        if (PlatformUtils.Instance.SaveFileDialog("Save Screenshot", new List<string> { "png" }, out path))
        {
            if (!path.EndsWith(".png")) path += ".png";
            CaptureHighRes(path, _multiplier);
        }
    }

    public unsafe void CaptureHighRes(string savePath, int multiplier)
    {
        var device = _viewport.Device;
        var factory = device.ResourceFactory;

        var outputDesc = device.SwapchainFramebuffer.OutputDescription;
        var colorFormat = outputDesc.ColorAttachments[0].Format;
        var depthFormat = outputDesc.DepthAttachment.Value.Format;

        int fullWidth = _viewport.Width * multiplier;
        int fullHeight = _viewport.Height * multiplier;

        Smithbox.Log(this, $"Starting high-res capture: {fullWidth}x{fullHeight}...", Microsoft.Extensions.Logging.LogLevel.Information, StudioCore.Logger.LogPriority.Normal);

        // Create temporary pipeline
        using var tempPipeline = new SceneRenderPipeline(_viewport.RenderScene, device, fullWidth, fullHeight);
        
        // CRITICAL: Unregister queues from the global renderer so the main loop doesn't steal/clear our objects
        SceneRenderer.UnregisterRenderQueue(tempPipeline._renderQueue);
        SceneRenderer.UnregisterRenderQueue(tempPipeline._overlayQueue);

        tempPipeline.SetViewportSetupAction(null);
        tempPipeline.SetOverlayViewportSetupAction(null);

        // Sync camera and frustum
        var viewMatrix = _viewport.ViewportCamera.CameraTransform.CameraViewMatrixLH;
        var eyePos = _viewport.ViewportCamera.CameraTransform.Position;
        float aspectRatio = fullWidth / (float)fullHeight;
        
        Matrix4x4 projMatrix;
        if (_viewport.ViewportCamera.ViewMode == ViewMode.Perspective)
        {
            projMatrix = ViewportUtils.CreatePerspective(device, true, CFG.Current.Viewport_Camera_FOV * (float)Math.PI / 180.0f, aspectRatio, CFG.Current.Viewport_Perspective_Near_Clip, CFG.Current.Viewport_Perspective_Far_Clip);
        }
        else
        {
            projMatrix = ViewportUtils.CreateOrthographic(device, false, _viewport.ViewportCamera.OrthographicSize * aspectRatio, _viewport.ViewportCamera.OrthographicSize, CFG.Current.Viewport_Orthographic_Near_Clip, CFG.Current.Viewport_Orthographic_Far_Clip);
        }

        var frustum = new BoundingFrustum(viewMatrix * projMatrix);

        // Populate SceneParams
        tempPipeline.SceneParams.Projection = projMatrix;
        tempPipeline.SceneParams.View = viewMatrix;
        tempPipeline.SceneParams.EyePosition = new Vector4(eyePos, 0.0f);
        tempPipeline.SceneParams.AmbientLightMult = _viewport.ViewPipeline.SceneParams.AmbientLightMult;
        tempPipeline.SceneParams.DirectLightMult = _viewport.ViewPipeline.SceneParams.DirectLightMult;
        tempPipeline.SceneParams.IndirectLightMult = _viewport.ViewPipeline.SceneParams.IndirectLightMult;
        tempPipeline.SceneParams.SceneBrightness = _viewport.ViewPipeline.SceneParams.SceneBrightness;
        tempPipeline.SceneParams.SelectionColor = _viewport.ViewPipeline.SceneParams.SelectionColor;
        tempPipeline.SceneParams.OutlineColor = _viewport.ViewPipeline.SceneParams.OutlineColor;
        tempPipeline.SceneParams.EnvMap = _viewport.ViewPipeline.SceneParams.EnvMap;

        // Create GPU targets
        var colorDesc = TextureDescription.Texture2D((uint)fullWidth, (uint)fullHeight, 1, 1, colorFormat, VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferSrc, 0, VkImageTiling.Optimal);
        using var colorTex = factory.CreateTexture(ref colorDesc);
        var depthDesc = TextureDescription.Texture2D((uint)fullWidth, (uint)fullHeight, 1, 1, depthFormat, VkImageUsageFlags.DepthStencilAttachment, 0, VkImageTiling.Optimal);
        using var depthTex = factory.CreateTexture(ref depthDesc);
        var framebufferDesc = new FramebufferDescription(depthTex, colorTex);
        using var framebuffer = factory.CreateFramebuffer(ref framebufferDesc);
        var stagingDesc = TextureDescription.Texture2D((uint)fullWidth, (uint)fullHeight, 1, 1, colorFormat, VkImageUsageFlags.TransferDst, 0, VkImageTiling.Linear);
        using var stagingTex = factory.CreateTexture(ref stagingDesc);

        using var fence = factory.CreateFence(false);

        try
        {
            // 1. Force upload of parameters
            var cl_update = factory.CreateCommandList();
            cl_update.UpdateBuffer(tempPipeline.SceneParamBuffer, 0, ref tempPipeline.SceneParams, (uint)Marshal.SizeOf<SceneParam>());
            device.SubmitCommands(cl_update);
            device.WaitForIdle();

            // 2. Render scene into isolated internal lists
            tempPipeline._renderQueue.Clear();
            tempPipeline._overlayQueue.Clear();
            tempPipeline.RenderScene(frustum);

            // 3. Draw synchronously
            var cl_draw = factory.CreateCommandList();
            cl_draw.SetFramebuffer(framebuffer);
            cl_draw.SetFullViewport(0);
            cl_draw.ClearColorTarget(0, RgbaFloat.Black);
            cl_draw.ClearDepthStencil(0.0f);
            
            tempPipeline._renderQueue.ExecuteSynchronous(cl_draw);
            tempPipeline._overlayQueue.ExecuteSynchronous(cl_draw);
            
            cl_draw.CopyTexture(colorTex, stagingTex);
            device.SubmitCommands(cl_draw, fence);
            device.WaitForFence(fence);

            // 4. Save
            MappedResource mapped = device.Map(stagingTex, MapMode.Read);
            byte* srcPtr = (byte*)mapped.Data.ToPointer();
            
            using var bigImage = new Image<Rgba32>(fullWidth, fullHeight);
            bigImage.ProcessPixelRows(accessor =>
            {
                for (int row = 0; row < fullHeight; row++)
                {
                    Span<Rgba32> destRow = accessor.GetRowSpan(row);
                    byte* srcRowPtr = srcPtr + (row * (int)mapped.RowPitch);
                    for (int col = 0; col < fullWidth; col++)
                    {
                        byte* pixelPtr = srcRowPtr + (col * 4);
                        if (colorFormat == VkFormat.B8G8R8A8Unorm || colorFormat == VkFormat.B8G8R8A8Srgb)
                            destRow[col] = new Rgba32(pixelPtr[2], pixelPtr[1], pixelPtr[0], pixelPtr[3]);
                        else
                            destRow[col] = new Rgba32(pixelPtr[0], pixelPtr[1], pixelPtr[2], pixelPtr[3]);
                    }
                }
            });
            device.Unmap(stagingTex);
            
            bigImage.SaveAsPng(savePath);
            Smithbox.Log(this, $"High-res screenshot saved to {savePath}", Microsoft.Extensions.Logging.LogLevel.Information, StudioCore.Logger.LogPriority.High);
        }
        catch (Exception ex)
        {
            Smithbox.Log(this, $"Failed to capture high-res screenshot: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, StudioCore.Logger.LogPriority.High);
        }
    }
}
