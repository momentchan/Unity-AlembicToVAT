using UnityEngine;
using GLTFast.Export;
using System.IO;
using System.Threading.Tasks;

public class GltfExportExample : MonoBehaviour
{
    // Assign the GameObject you want to export in the Inspector
    public GameObject objectToExport;
    public string ExportPath = "Assets/ExportVAT";
    
    // Define the output path for the glTF file
    public string outputFileName = "ExportedModel.glb";

    [ContextMenu("Export")]
    void Export()
    {
        if (objectToExport == null)
        {
            Debug.LogError("Object to export is not assigned!");
            return;
        }

        // Run the export operation asynchronously
        ExportGameObjectAsync();
    }

    private async Task ExportGameObjectAsync()
    {
        Debug.Log("Starting glTFast export...");

        // 1. Define Export Settings
        var exportSettings = new ExportSettings
        {
            // Set the file format to GLB (single binary file)
            Format = GltfFormat.Binary,
            // Ensure the exported asset includes scene information
            // --- Crucial for UVs/Vertex Colors ---
            // Force the preservation of all available vertex attributes, 
            // even if the active Unity material doesn't seem to use them.
              PreservedVertexAttributes = VertexAttributeUsage.AllTexCoords | VertexAttributeUsage.Color | VertexAttributeUsage.Normal | VertexAttributeUsage.Tangent | VertexAttributeUsage.Position
            // Alternative for specific attributes: 
            // PreservedVertexAttributes = VertexAttribute.Color | VertexAttribute.TexCoord0 | VertexAttribute.TexCoord1
        };

        // 2. Initialize the exporter
        var export = new GameObjectExport(exportSettings);

        // 3. Add the target GameObject to the export queue
        export.AddScene(new [] { objectToExport });

        // 4. Define the file path
        string path = Path.Combine(ExportPath, outputFileName);
        

        // 5. Save the file asynchronously
        bool success = await export.SaveToFileAndDispose(path);

        if (success)
        {
            Debug.Log($"glTF export successful! File saved to: {path}");
        }
        else
        {
            Debug.LogError("glTF export failed.");
        }
    }
}
