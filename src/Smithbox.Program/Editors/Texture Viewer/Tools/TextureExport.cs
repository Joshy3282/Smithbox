using Hexa.NET.ImGui;
using SoulsFormats;
using StudioCore.Application;
using StudioCore.Renderer;
using StudioCore.Utilities;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace StudioCore.Editors.TextureViewer;

public class TextureExport
{
    public TextureViewerScreen Editor;
    public ProjectEntry Project;

    public TextureExport(TextureViewerScreen editor, ProjectEntry project)
    {
        Editor = editor;
        Project = project;
    }

    public string[] exportTypes = new[] { "DDS", "PNG", "BMP", "TGA", "TIFF", "JPEG", "WEBP" };

    public void Display()
    {
        var windowWidth = ImGui.GetWindowWidth();

        if (ImGui.CollapsingHeader("Export Texture"))
        {
            ImGui.BeginChild("TextureExportToolSection");

            UIHelper.WrappedText("Export the viewed texture.");
            UIHelper.WrappedText("");

            var index = CFG.Current.TextureViewerToolbar_ExportTextureType;

            UIHelper.WrappedText("Export File Type:");
            DPI.ApplyInputWidth(windowWidth);
            if (ImGui.Combo("##ExportType", ref index, exportTypes, exportTypes.Length))
            {
                CFG.Current.TextureViewerToolbar_ExportTextureType = index;
            }
            UIHelper.Tooltip("The file type the exported texture will be saved as.");
            UIHelper.WrappedText("");

            UIHelper.WrappedText("Export Destination:");
            DPI.ApplyInputWidth(windowWidth);
            ImGui.InputText("##exportDestination", ref CFG.Current.TextureViewerToolbar_ExportTextureLocation, 255);
            if (ImGui.Button("Select", DPI.HalfWidthButton(windowWidth, 24)))
            {
                string path;
                var result = PlatformUtils.Instance.OpenFolderDialog("Select Export Destination", out path);
                if (result)
                {
                    CFG.Current.TextureViewerToolbar_ExportTextureLocation = path;
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("View Folder", DPI.HalfWidthButton(windowWidth, 24)))
            {
                Process.Start("explorer.exe", CFG.Current.TextureViewerToolbar_ExportTextureLocation);
            }
            UIHelper.Tooltip("The folder destination to export the texture to.");
            UIHelper.WrappedText("");

            ImGui.Checkbox("Include Container Folder", ref CFG.Current.TextureViewerToolbar_ExportTexture_IncludeFolder);
            UIHelper.Tooltip("Place the exported texture in a folder with the title of the texture container.");

            ImGui.Checkbox("Display Export Confirmation", ref CFG.Current.TextureViewerToolbar_ExportTexture_DisplayConfirm);
            UIHelper.Tooltip("Display the confirmation message box after each export.");
            UIHelper.WrappedText("");

            if (ImGui.Button("Export##action_Selection_ExportTexture", DPI.WholeWidthButton(windowWidth, 24)))
            {
                ExportTextureHandler();
            }

            if (ImGui.Button("Mass Export (PNG)##action_Selection_MassExportTexture", DPI.WholeWidthButton(windowWidth, 24)))
            {
                ExportAllTexturesHandler();
            }

            ImGui.EndChild();
        }
    }

    public void ExportAllTexturesHandler()
    {
        var activeView = Editor.ViewHandler.ActiveView;

        if (activeView == null)
            return;

        if (activeView.Selection.SelectedBinder == null)
        {
            PlatformUtils.Instance.MessageBox($"No container is currently selected.", $"Smithbox", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var exportPath = CFG.Current.TextureViewerToolbar_ExportTextureLocation;
        if (exportPath == "")
        {
            PlatformUtils.Instance.MessageBox($"Export Destination is not set.", $"Smithbox", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!Directory.Exists(exportPath))
        {
            PlatformUtils.Instance.MessageBox($"Directory is not valid.", $"Smithbox", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int count = 0;
        foreach (var fileEntry in activeView.Selection.SelectedBinder.Files)
        {
            var tpf = fileEntry.Value;
            var tpfName = Path.GetFileNameWithoutExtension(fileEntry.Key.Name);

            foreach (var tex in tpf.Textures)
            {
                var filename = tex.Name;
                var finalExportPath = exportPath;

                if (CFG.Current.TextureViewerToolbar_ExportTexture_IncludeFolder)
                {
                    finalExportPath = Path.Join(exportPath, tpfName);
                    if (!Directory.Exists(finalExportPath))
                    {
                        Directory.CreateDirectory(finalExportPath);
                    }
                }

                var exportFilePath = Path.Join(finalExportPath, filename);

                byte[] bytes = tex.Bytes.ToArray();
                if (tpf.Platform != SoulsFormats.TPF.TPFPlatform.PC)
                {
                    bytes = tex.Headerize();
                }

                try
                {
                    TexUtils.ExportPNGImage(exportFilePath, bytes);
                    count++;
                }
                catch (System.Exception e)
                {
                    Smithbox.LogError(this, $"Failed to export {filename} in {tpfName}: {e.Message}");
                }
            }
        }

        if (CFG.Current.TextureViewerToolbar_ExportTexture_DisplayConfirm)
        {
            PlatformUtils.Instance.MessageBox($"{count} textures exported to {exportPath}.", $"Smithbox", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    public void ExportTextureHandler()
    {
        var activeView = Editor.ViewHandler.ActiveView;

        if (activeView == null)
            return;

        var currentTexture = activeView.Selection.ViewerTextureResource;
        var currentTextureName = activeView.Selection.SelectedTextureKey;

        if (currentTexture != null)
        {
            var filename = currentTextureName;
            var exportPath = CFG.Current.TextureViewerToolbar_ExportTextureLocation;

            if (exportPath != "")
            {
                var exportFilePath = Path.Join(exportPath, filename);
                var write = true;

                if (File.Exists(exportFilePath))
                {
                    var result = PlatformUtils.Instance.MessageBox($"Overwrite existing file at {exportFilePath}?", $"Smithbox", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (result == DialogResult.No)
                    {
                        write = false;
                    }
                }

                if (!Directory.Exists(exportPath))
                {
                    write = false;
                    PlatformUtils.Instance.MessageBox($"Directory is not valid.", $"Smithbox", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                if (write)
                {
                    if (CFG.Current.TextureViewerToolbar_ExportTexture_IncludeFolder)
                    {
                        var folder = currentTextureName;
                        var newFolderPath = Path.Join(exportPath, folder);

                        if (!Directory.Exists(newFolderPath))
                        {
                            Directory.CreateDirectory(newFolderPath);
                        }

                        exportFilePath = Path.Join(exportPath, folder, filename);
                    }

                    ExportTextureFile(currentTexture, exportFilePath);

                    if (CFG.Current.TextureViewerToolbar_ExportTexture_DisplayConfirm)
                    {
                        PlatformUtils.Instance.MessageBox($"{filename} exported.", $"Smithbox", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            else
            {
                PlatformUtils.Instance.MessageBox($"Export Destination is not set.", $"Smithbox", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        else
        {
            PlatformUtils.Instance.MessageBox($"No texture is currently being viewed.", $"Smithbox", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void ExportTextureFile(TextureResource texResource, string exportFilePath)
    {
        var tex = texResource.GPUTexture.TpfTexture;
        var bytes = tex.Bytes.ToArray();

        // DDS
        if (CFG.Current.TextureViewerToolbar_ExportTextureType == 0)
        {
            TexUtils.ExportDDSImage(exportFilePath, bytes);
        }

        // PNG
        if (CFG.Current.TextureViewerToolbar_ExportTextureType == 1)
        {
            TexUtils.ExportPNGImage(exportFilePath, bytes);
        }

        // BMP
        if (CFG.Current.TextureViewerToolbar_ExportTextureType == 2)
        {
            TexUtils.ExportBMPImage(exportFilePath, bytes);
        }

        // TGA
        if (CFG.Current.TextureViewerToolbar_ExportTextureType == 3)
        {
            TexUtils.ExportTGAImage(exportFilePath, bytes);
        }

        // TIFF
        if (CFG.Current.TextureViewerToolbar_ExportTextureType == 4)
        {
            TexUtils.ExportTIFFImage(exportFilePath, bytes);
        }

        // JPEG
        if (CFG.Current.TextureViewerToolbar_ExportTextureType == 5)
        {
            TexUtils.ExportJPEGImage(exportFilePath, bytes);
        }

        // WEBP
        if (CFG.Current.TextureViewerToolbar_ExportTextureType == 6)
        {
            TexUtils.ExportWEBPImage(exportFilePath, bytes);
        }
    }
}
