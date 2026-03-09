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
    private int _multiplier = 4;
    private int _mipmapBias = -1;

    public TiledScreenshotTool(VulkanViewport viewport)
    {
        _viewport = viewport;
    }

    public void OnGui()
    {
        if (ImGui.BeginMenu("High-Res Screenshot"))
        {
            ImGui.Text("Export Settings:");
            
            ImGui.InputInt("Multiplier", ref _multiplier);
            if (_multiplier < 1) _multiplier = 1;
            if (_multiplier > 16) _multiplier = 16;
            UIHelper.Tooltip("Resolution multiplier relative to the current viewport size.");

            ImGui.InputInt("Sharpening (Mip Bias)", ref _mipmapBias);
            if (_mipmapBias < -4) _mipmapBias = -4;
            if (_mipmapBias > 0) _mipmapBias = 0;
            UIHelper.Tooltip("Negative value forces higher resolution textures (sharper but can alias). -1 is a good default for high-res.");

            ImGui.Separator();

            int fullWidth = _viewport.Width * _multiplier;
            int fullHeight = _viewport.Height * _multiplier;
            ImGui.TextDisabled($"Final Resolution: {fullWidth}x{fullHeight}");

            if (ImGui.MenuItem("Capture High-Res Screenshot"))
            {
                Capture();
            }
            UIHelper.Tooltip($"Captures exactly what's in the viewport at {_multiplier}x resolution with tiling.");

            ImGui.EndMenu();
        }
    }

    private unsafe void Capture()
    {
        string path;
        if (PlatformUtils.Instance.SaveFileDialog("Save Screenshot", new List<string> { "png" }, out path))
        {
            if (!path.EndsWith(".png")) path += ".png";
            CaptureHighRes(path, _multiplier, _mipmapBias);
        }
    }

    public unsafe void CaptureHighRes(string savePath, int multiplier, int mipmapBias)
    {
        var device = _viewport.Device;
        var factory = device.ResourceFactory;

        var outputDesc = device.SwapchainFramebuffer.OutputDescription;
        var colorFormat = outputDesc.ColorAttachments[0].Format;
        var depthFormat = outputDesc.DepthAttachment.Value.Format;

        int fullWidth = _viewport.Width * multiplier;
        int fullHeight = _viewport.Height * multiplier;

        // Determine tiling to keep GPU framebuffer size reasonable (e.g. ~2048-4096)
        int tileCount = (int)Math.Ceiling((double)multiplier / 2.0);
        if (tileCount < 1) tileCount = 1;

        int tileWidth = (int)Math.Ceiling((double)fullWidth / tileCount);
        int tileHeight = (int)Math.Ceiling((double)fullHeight / tileCount);

        Smithbox.Log(this, $"Starting high-res capture: {fullWidth}x{fullHeight} using {tileCount}x{tileCount} tiles...", Microsoft.Extensions.Logging.LogLevel.Information, StudioCore.Logger.LogPriority.Normal);

        // Create temporary pipeline
        using var tempPipeline = new SceneRenderPipeline(_viewport.RenderScene, device, tileWidth, tileHeight);
        
        // Use biased samplers if requested
        ResourceSet biasedRS = null;
        Sampler biasedSampler = null;
        if (mipmapBias != 0)
        {
            (biasedRS, biasedSampler) = SamplerSet.CreateResourceSetWithBias(device, mipmapBias);
            tempPipeline.SamplerResourceSet = biasedRS;
        }

        // CRITICAL: Unregister queues from the global renderer
        SceneRenderer.UnregisterRenderQueue(tempPipeline._renderQueue);
        SceneRenderer.UnregisterRenderQueue(tempPipeline._overlayQueue);

        tempPipeline.SetViewportSetupAction(null);
        tempPipeline.SetOverlayViewportSetupAction(null);

        // Base matrices
        var viewMatrix = _viewport.ViewportCamera.CameraTransform.CameraViewMatrixLH;
        var eyePos = _viewport.ViewportCamera.CameraTransform.Position;
        float aspectRatio = (float)fullWidth / fullHeight;
        
        Matrix4x4 baseProjMatrix;
        if (_viewport.ViewportCamera.ViewMode == ViewMode.Perspective)
        {
            baseProjMatrix = ViewportUtils.CreatePerspective(device, true, CFG.Current.Viewport_Camera_FOV * (float)Math.PI / 180.0f, aspectRatio, CFG.Current.Viewport_Perspective_Near_Clip, CFG.Current.Viewport_Perspective_Far_Clip);
        }
        else
        {
            baseProjMatrix = ViewportUtils.CreateOrthographic(device, false, _viewport.ViewportCamera.OrthographicSize * aspectRatio, _viewport.ViewportCamera.OrthographicSize, CFG.Current.Viewport_Orthographic_Near_Clip, CFG.Current.Viewport_Orthographic_Far_Clip);
        }

        // Create GPU targets for a single tile
        var colorDesc = TextureDescription.Texture2D((uint)tileWidth, (uint)tileHeight, 1, 1, colorFormat, VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferSrc, 0, VkImageTiling.Optimal);
        using var colorTex = factory.CreateTexture(ref colorDesc);
        var depthDesc = TextureDescription.Texture2D((uint)tileWidth, (uint)tileHeight, 1, 1, depthFormat, VkImageUsageFlags.DepthStencilAttachment, 0, VkImageTiling.Optimal);
        using var depthTex = factory.CreateTexture(ref depthDesc);
        var framebufferDesc = new FramebufferDescription(depthTex, colorTex);
        using var framebuffer = factory.CreateFramebuffer(ref framebufferDesc);
        var stagingDesc = TextureDescription.Texture2D((uint)tileWidth, (uint)tileHeight, 1, 1, colorFormat, VkImageUsageFlags.TransferDst, 0, VkImageTiling.Linear);
        using var stagingTex = factory.CreateTexture(ref stagingDesc);

        using var bigImage = new Image<Rgba32>(fullWidth, fullHeight);

        try
        {
            for (int ty = 0; ty < tileCount; ty++)
            {
                for (int tx = 0; tx < tileCount; tx++)
                {
                    // Calculate tiled projection
                    var tiledProj = ViewportUtils.CreateTiledProjection(device, baseProjMatrix, tx, ty, tileCount);
                    var frustum = new BoundingFrustum(viewMatrix * tiledProj);

                    // Populate SceneParams
                    tempPipeline.SceneParams.Projection = tiledProj;
                    tempPipeline.SceneParams.View = viewMatrix;
                    tempPipeline.SceneParams.EyePosition = new Vector4(eyePos, 0.0f);
                    tempPipeline.SceneParams.AmbientLightMult = _viewport.ViewPipeline.SceneParams.AmbientLightMult;
                    tempPipeline.SceneParams.DirectLightMult = _viewport.ViewPipeline.SceneParams.DirectLightMult;
                    tempPipeline.SceneParams.IndirectLightMult = _viewport.ViewPipeline.SceneParams.IndirectLightMult;
                    tempPipeline.SceneParams.SceneBrightness = _viewport.ViewPipeline.SceneParams.SceneBrightness;
                    tempPipeline.SceneParams.SelectionColor = _viewport.ViewPipeline.SceneParams.SelectionColor;
                    tempPipeline.SceneParams.OutlineColor = _viewport.ViewPipeline.SceneParams.OutlineColor;
                    tempPipeline.SceneParams.EnvMap = _viewport.ViewPipeline.SceneParams.EnvMap;

                    // 1. Force upload of parameters
                    var cl_update = factory.CreateCommandList();
                    cl_update.UpdateBuffer(tempPipeline.SceneParamBuffer, 0, ref tempPipeline.SceneParams, (uint)Marshal.SizeOf<SceneParam>());
                    device.SubmitCommands(cl_update);
                    device.WaitForIdle();

                    // 2. Render scene into isolated internal lists
                    tempPipeline._renderQueue.Clear();
                    tempPipeline._overlayQueue.Clear();
                    tempPipeline.RenderScene(frustum);

                    // 3. Draw tile
                    using var fence = factory.CreateFence(false);
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

                    // 4. Map and copy to big image
                    MappedResource mapped = device.Map(stagingTex, MapMode.Read);
                    byte* srcPtr = (byte*)mapped.Data.ToPointer();

                    int startX = tx * tileWidth;
                    int startY = ty * tileHeight;

                    bigImage.ProcessPixelRows(accessor =>
                    {
                        for (int row = 0; row < tileHeight; row++)
                        {
                            int globalRow = startY + row;
                            if (globalRow >= fullHeight) break;

                            var destRow = accessor.GetRowSpan(globalRow);
                            byte* srcRowPtr = srcPtr + (row * (int)mapped.RowPitch);

                            for (int col = 0; col < tileWidth; col++)
                            {
                                int globalCol = startX + col;
                                if (globalCol >= fullWidth) break;

                                byte* pixelPtr = srcRowPtr + (col * 4);
                                if (colorFormat == VkFormat.B8G8R8A8Unorm || colorFormat == VkFormat.B8G8R8A8Srgb)
                                    destRow[globalCol] = new Rgba32(pixelPtr[2], pixelPtr[1], pixelPtr[0], pixelPtr[3]);
                                else
                                    destRow[globalCol] = new Rgba32(pixelPtr[0], pixelPtr[1], pixelPtr[2], pixelPtr[3]);
                            }
                        }
                    });
                    device.Unmap(stagingTex);
                }
            }

            bigImage.SaveAsPng(savePath);
            Smithbox.Log(this, $"High-res tiled screenshot saved to {savePath}", Microsoft.Extensions.Logging.LogLevel.Information, StudioCore.Logger.LogPriority.High);
        }
        catch (Exception ex)
        {
            Smithbox.Log(this, $"Failed to capture high-res screenshot: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, StudioCore.Logger.LogPriority.High);
        }
        finally
        {
            biasedRS?.Dispose();
            biasedSampler?.Dispose();
        }
    }
}
