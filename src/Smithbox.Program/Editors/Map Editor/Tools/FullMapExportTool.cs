using Hexa.NET.ImGui;
using StudioCore.Application;
using StudioCore.Editors.Common;
using StudioCore.Editors.Viewport;
using StudioCore.Renderer;
using StudioCore.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Veldrid;
using Veldrid.Utilities;

namespace StudioCore.Editors.MapEditor;

public class FullMapExportTool
{
    private MapEditorView View;
    private ProjectEntry Project;

    public FullMapExportTool(MapEditorView view, ProjectEntry project)
    {
        View = view;
        Project = project;
    }

    public void OnGui()
    {
        if (ImGui.BeginMenu("Export"))
        {
            if (ImGui.MenuItem("Full Map Top-Down Image"))
            {
                ExportFullMapTopDown();
            }
            UIHelper.Tooltip("Exports an orthographic top-down view of the entire loaded map.");

            ImGui.EndMenu();
        }
    }

    private void ExportFullMapTopDown()
    {
        var viewport = View.GetCurrentViewport();
        if (viewport == null)
        {
            Smithbox.Log(typeof(FullMapExportTool), "No active viewport found for export.", Microsoft.Extensions.Logging.LogLevel.Error, StudioCore.Logger.LogPriority.High);
            return;
        }

        // 1. Calculate bounds of entire map
        BoundingBox totalBounds = new BoundingBox(new Vector3(float.MaxValue), new Vector3(float.MinValue));
        bool anyFound = false;

        if (Project.Handler.MapData?.PrimaryBank?.Maps != null)
        {
            foreach (var mapWrapper in Project.Handler.MapData.PrimaryBank.Maps.Values)
            {
                var map = mapWrapper.MapContainer;
                if (map == null) continue;

                foreach (var obj in map.Objects)
                {
                    if (obj.RenderSceneMesh != null && obj.EditorVisible)
                    {
                        var bounds = obj.GetBounds();
                        if (bounds.Min != bounds.Max) // Avoid invalid/empty bounds
                        {
                            totalBounds = BoundingBox.Combine(totalBounds, bounds);
                            anyFound = true;
                        }
                    }
                }
            }
        }

        if (!anyFound)
        {
            Smithbox.Log(typeof(FullMapExportTool), "No visible objects found in loaded maps to export.", Microsoft.Extensions.Logging.LogLevel.Warning, StudioCore.Logger.LogPriority.High);
            return;
        }

        // 2. Set camera to top-down orthographic
        var center = totalBounds.GetCenter();
        var dims = totalBounds.GetDimensions();
        
        // Ensure camera is high enough to see everything
        float height = Math.Max(dims.X, dims.Z);
        float cameraY = totalBounds.Max.Y + 1000.0f; // 1000 units above the highest point

        viewport.ViewportCamera.SetProjectionType(ViewMode.Orthographic);
        viewport.ViewportCamera.CameraTransform.Position = new Vector3(center.X, cameraY, center.Z);
        
        // Rotation for top-down: Look straight down (X = -90 degrees, Y = 0, Z = 0)
        viewport.ViewportCamera.CameraTransform.EulerRotation = new Vector3(-Utils.PiOver2, 0, 0);
        
        // Set orthographic size to cover the larger dimension
        viewport.ViewportCamera.OrthographicSize = height;
        viewport.ViewportCamera.UpdateProjectionMatrix(true);

        Smithbox.Log(typeof(FullMapExportTool), "Viewport set to full map top-down view. (Automatic image saving not yet implemented - please take a manual screenshot or use RenderDoc)", Microsoft.Extensions.Logging.LogLevel.Information, StudioCore.Logger.LogPriority.High);
    }
}
