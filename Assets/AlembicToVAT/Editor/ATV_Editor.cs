// ATV_Editor.cs
// Alembic → VAT baker for three.js
// Outputs: Pos/Normal EXR (linear, Nearest, Clamp), meta.json, basis Mesh (asset), optional FBX
// Author: you + ChatGPT ;)
// Requires: Unity 2020+; Alembic package (com.unity.formats.alembic); Editor Coroutines

using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using Unity.EditorCoroutines.Editor;
using UnityEngine.Formats.Alembic.Importer;


#if UNITY_FBX_AVAILABLE
using UnityEditor.Formats.Fbx.Exporter; // com.unity.formats.fbx
#endif

public enum TopologyType { Undefined, Analysing, Variable, Fixed }

public class ATV_Editor : EditorWindow
{
    // -------- UI / Settings --------
    [Header("Export")]
    public string ExportPath = "Assets/ExportVAT";
    public bool NameFromAlembicPlayer = true;
    public string ExportFilename = "AlembicVAT";

    [Header("Time / Sampling")]
    public float StartTime = 0f;
    public float EndTime = 10f;
    public float SampleRate = 24f; // fps

    [Header("Baking Options")]
    public bool StoreCenterPositionInUV3 = false; // legacy; not needed for web VAT
    public bool FromBlender = false;              // transforms to world before sampling
    public bool UnlitMesh = false;                // normals optional
    public bool CompressNormal = false;           // store normals in RGBA8 [0..1] instead of float
    public TopologyType VariableTopology = TopologyType.Undefined;

    [Header("Source")]
    public AlembicStreamPlayer AlembicPlayer;

    // internal
    SerializedProperty timeProp = null;
    SerializedProperty startTimeProp = null;
    SerializedProperty endTimeProp = null;
    SerializedObject alembicObject = null;

    EditorCoroutine currentBaking = null;
    bool bakingInProgress = false;
    float progress = 0f;

    Transform meshRoot = null; // root containing MeshFilters to sample
    int maxTriangleCount = 0;
    int minTriangleCount = 10000000;

    [MenuItem("Window/Alembic → VAT (three.js)")]
    public static void ShowWindow() => GetWindow<ATV_Editor>("Alembic → VAT");

    void OnEnable()
    {
        // make sure export folder exists
        if (!AssetDatabase.IsValidFolder(ExportPath))
        {
            var baseFolder = "Assets";
            var segments = ExportPath.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                if (string.IsNullOrEmpty(segments[i])) continue;
                var next = (i == 0 && segments[i] == "Assets") ? "Assets" : Path.Combine(baseFolder, segments[i]).Replace("\\", "/");
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(baseFolder, segments[i]);
                }
                baseFolder = next;
            }
        }
    }

    void OnGUI()
    {
        GUILayout.Label("Source", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        AlembicPlayer = EditorGUILayout.ObjectField("Alembic Player", AlembicPlayer, typeof(AlembicStreamPlayer), true) as AlembicStreamPlayer;
        if (EditorGUI.EndChangeCheck())
        {
            if (AlembicPlayer != null)
            {
                if (NameFromAlembicPlayer) ExportFilename = AlembicPlayer.gameObject.name;
                currentBaking = EditorCoroutineUtility.StartCoroutine(AnalyzeFromAlembic(), this);
            }
            else
            {
                if (currentBaking != null) EditorCoroutineUtility.StopCoroutine(currentBaking);
                VariableTopology = TopologyType.Undefined;
            }
        }

        GUILayout.Space(8);
        GUILayout.Label("Export", EditorStyles.boldLabel);
        GUILayout.BeginHorizontal();
        ExportPath = EditorGUILayout.TextField("Export Folder", ExportPath);
        if (GUILayout.Button("Browse", GUILayout.MaxWidth(70)))
        {
            var chosen = EditorUtility.OpenFolderPanel("Choose export folder (inside project)", Application.dataPath, "");
            if (!string.IsNullOrEmpty(chosen))
            {
                if (!chosen.Replace("\\","/").StartsWith(Application.dataPath))
                {
                    EditorUtility.DisplayDialog("Folder must be inside Assets", "Pick a folder under the project Assets/", "OK");
                }
                else
                {
                    ExportPath = "Assets" + chosen.Replace("\\","/").Substring(Application.dataPath.Replace("\\","/").Length);
                }
            }
        }
        GUILayout.EndHorizontal();

        NameFromAlembicPlayer = EditorGUILayout.Toggle("Name from Alembic", NameFromAlembicPlayer);
        ExportFilename = EditorGUILayout.TextField("Export Filename", ExportFilename);
        EditorGUILayout.HelpBox($"Files will be saved to:\n{ExportPath}/{ExportFilename}_pos.exr\n{ExportPath}/{ExportFilename}_nrm.exr (optional)\n{ExportPath}/{ExportFilename}_meta.json", MessageType.Info);

        GUILayout.Space(8);
        GUILayout.Label("Animation Info", EditorStyles.boldLabel);
        if (VariableTopology != TopologyType.Undefined && VariableTopology != TopologyType.Analysing)
        {
            StartTime  = EditorGUILayout.FloatField("Start Time (s)", StartTime);
            EndTime    = EditorGUILayout.FloatField("End Time (s)", EndTime);
            SampleRate = EditorGUILayout.FloatField("Sample Rate (fps)", SampleRate);
            if (!float.IsFinite(SampleRate) || SampleRate <= 0) SampleRate = 24f;

            FromBlender = EditorGUILayout.Toggle("Source was Blender", FromBlender);
            UnlitMesh   = EditorGUILayout.Toggle("Unlit (skip normals)", UnlitMesh);
            if (!UnlitMesh) CompressNormal = EditorGUILayout.Toggle("Compress Normal (RGBA8)", CompressNormal);
        }

        switch (VariableTopology)
        {
            case TopologyType.Undefined:  GUILayout.Label("Topology: Undefined"); break;
            case TopologyType.Analysing:  GUILayout.Label("Topology: Analysing…"); break;
            case TopologyType.Fixed:      GUILayout.Label("Topology: Fixed (morphing mesh)"); break;
            case TopologyType.Variable:   GUILayout.Label("Topology: Variable (triangle soup per frame)"); break;
        }

        GUILayout.Space(8);
        GUI.enabled = (VariableTopology != TopologyType.Undefined && VariableTopology != TopologyType.Analysing && AlembicPlayer != null);
        if (!bakingInProgress)
        {
            if (GUILayout.Button("Bake VAT for three.js")) currentBaking = EditorCoroutineUtility.StartCoroutine(ExportFrames(), this);
        }
        else
        {
            if (GUILayout.Button("Cancel Bake")) CancelBake();
            EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(false, 18), progress, "Baking…");
        }
        GUI.enabled = true;

        GUILayout.FlexibleSpace();
        EditorGUILayout.HelpBox("Tip: If you install com.unity.formats.fbx,\nthe basis mesh will also be exported as FBX.", MessageType.None);
    }

    // ---------------- Alembic helpers ----------------
    SerializedObject InitAlembic()
    {
        if (AlembicPlayer == null)
        {
            Debug.LogError("Assign an AlembicStreamPlayer.");
            return null;
        }
        alembicObject = new SerializedObject(AlembicPlayer);
        timeProp      = alembicObject.FindProperty("currentTime");
        startTimeProp = alembicObject.FindProperty("startTime");
        endTimeProp   = alembicObject.FindProperty("endTime");
        return alembicObject;
    }

    IEnumerator AnalyzeFromAlembic()
    {
        VariableTopology = TopologyType.Analysing;
        progress = 0f;

        var alembic = InitAlembic();
        if (alembic == null) yield break;

        // find a mesh root to iterate
        var mf = AlembicPlayer.GetComponentInChildren<MeshFilter>();
        meshRoot = mf ? mf.transform.parent : AlembicPlayer.transform;

        // adopt times from Alembic when possible
        StartTime  = 0f;
        SampleRate = 1.0f / Mathf.Max(1e-6f, alembic.FindProperty("startTime").floatValue);
        EndTime    = endTimeProp.floatValue;

        maxTriangleCount = 0;
        minTriangleCount = int.MaxValue;

        int framesCount = Mathf.Max(1, Mathf.RoundToInt((EndTime - StartTime) * SampleRate + 0.5f));
        for (int frame = 0; frame < framesCount; frame++)
        {
            progress = frame / (float)Mathf.Max(1, framesCount - 1);
            float t = StartTime + frame / SampleRate;
            timeProp.floatValue = t;
            alembicObject.ApplyModifiedProperties();
            yield return null;

            int triangleCount = 0;
            if (meshRoot != null)
            {
                for (int i = 0; i < meshRoot.childCount; i++)
                {
                    var child = meshRoot.GetChild(i);
                    var localMF = child.GetComponent<MeshFilter>();
                    if (localMF == null && child.childCount > 0) localMF = child.GetChild(0).GetComponent<MeshFilter>();
                    if (localMF && localMF.sharedMesh) triangleCount += localMF.sharedMesh.triangles.Length / 3;
                }
            }
            maxTriangleCount = Mathf.Max(maxTriangleCount, triangleCount);
            minTriangleCount = Mathf.Min(minTriangleCount, triangleCount);
        }

        VariableTopology = (maxTriangleCount == minTriangleCount) ? TopologyType.Fixed : TopologyType.Variable;
        progress = 1f;
        yield return null;
        Repaint();
    }

    void CancelBake()
    {
        if (currentBaking != null) EditorCoroutineUtility.StopCoroutine(currentBaking);
        bakingInProgress = false;
        progress = 0f;
        EditorUtility.ClearProgressBar();
        Debug.Log("Bake cancelled.");
    }

    // ---------------- VAT export ----------------

    // map integer pixel to normalized UV
    static Vector2 UVFromPixel(int px, int py, int w, int h) => new Vector2((px + 0.5f) / w, (py + 0.5f) / h);

    // layout helper: given (frame, vertexIndex) return (x,y) pixel
    static Vector2Int Coord(int frameIdx, int vertIdx, int texW, int texH, int stride /*frames+pad*/)
    {
        int columnIndex   = vertIdx / texH;        // which vertical column this vertex belongs to
        int verticalIndex = vertIdx % texH;        // row inside that column
        int x = frameIdx + columnIndex * stride;   // stride columns per vertex column
        int y = verticalIndex;
        return new Vector2Int(x, y);
    }

    [Serializable]
    class VatMeta
    {
        public int vertexCount;
        public int frameCount;
        public int fps;
        public int texWidth;
        public int texHeight;
        public int columns;
        public int frameStride;    // adjustedFramesCount = frames + padding
        public bool storeDelta;    // Fixed topology writes delta; Variable writes absolute
        public bool normalsCompressed;
    }

    IEnumerator ExportFrames()
    {
        if (AlembicPlayer == null)
        {
            Debug.LogError("No Alembic player.");
            yield break;
        }

        bakingInProgress = true;
        progress = 0f;

        var alembic = InitAlembic();
        if (alembic == null) yield break;

        // Ensure export dir exists
        Directory.CreateDirectory(ExportPath);

        // time setup
        timeProp.floatValue = StartTime;
        alembicObject.ApplyModifiedProperties();
        yield return null;

        // build a merged basis mesh (vertex order matters)
        var basisMesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        var vertsList = new List<Vector3>();
        var trisList  = new List<int>();
        var uvsList   = new List<Vector2>();
        var uv2List   = new List<Vector2>(); // will hold per-vertex index UV for frame 0
        var nrmsList  = new List<Vector3>();
        var colsList  = new List<Color>();

        int verticesCount = 0;
        int trisIndexCount = 0;

        bool hasNormals = false;
        bool hasUVs     = false;
        bool hasColors  = false;

        if (meshRoot == null)
        {
            var mf = AlembicPlayer.GetComponentInChildren<MeshFilter>();
            meshRoot = mf ? mf.transform.parent : AlembicPlayer.transform;
        }

        if (VariableTopology == TopologyType.Variable)
        {
            hasNormals     = true;
            verticesCount  = maxTriangleCount * 3;
            trisIndexCount = maxTriangleCount * 3;

            // create a dummy soup mesh for bounds & uv2 (filled later)
            vertsList.Capacity = verticesCount;
            nrmsList.Capacity  = verticesCount;
            for (int i = 0; i < verticesCount; i++)
            {
                vertsList.Add(Vector3.zero);
                nrmsList.Add(Vector3.up);
                trisList.Add(i);
            }
        }
        else // Fixed
        {
            int vOffset = 0;
            if (meshRoot != null)
            {
                for (int i = 0; i < meshRoot.childCount; i++)
                {
                    var child = meshRoot.GetChild(i);
                    var localMF = child.GetComponent<MeshFilter>();
                    if (localMF == null && child.childCount > 0) localMF = child.GetChild(0).GetComponent<MeshFilter>();
                    if (localMF == null || localMF.sharedMesh == null) continue;

                    var m = localMF.sharedMesh;
                    var v = m.vertices;
                    var t = m.triangles;
                    var n = m.normals;
                    var u = m.uv;
                    var c = m.colors;

                    hasNormals |= (n != null && n.Length == v.Length);
                    hasUVs     |= (u != null && u.Length == v.Length);
                    hasColors  |= (c != null && c.Length == v.Length);

                    for (int j = 0; j < v.Length; j++)
                    {
                        vertsList.Add(v[j]);
                        if (hasNormals) nrmsList.Add(j < n.Length ? n[j] : Vector3.up);
                        if (hasUVs)     uvsList.Add(j < u.Length ? u[j] : Vector2.zero);
                        if (hasColors)  colsList.Add(j < c.Length ? c[j] : Color.white);
                    }
                    for (int j = 0; j < t.Length; j++) trisList.Add(t[j] + vOffset);
                    vOffset += v.Length;
                }
            }
            verticesCount  = vertsList.Count;
            trisIndexCount = trisList.Count;
        }

        basisMesh.SetVertices(vertsList);
        if (hasUVs)     basisMesh.SetUVs(0, uvsList);
        if (hasNormals) basisMesh.SetNormals(nrmsList);
        if (hasColors)  basisMesh.SetColors(colsList);
        basisMesh.SetTriangles(trisList, 0);
        basisMesh.RecalculateBounds();

        // ----- texture layout (no need to be power-of-two) -----
        int framesCount = Mathf.Max(1, Mathf.RoundToInt((EndTime - StartTime) * SampleRate + 0.5f));
        int adjustedFramesCount = framesCount + 2; // keep padding columns like your original
        int maxTexSize = SystemInfo.maxTextureSize; // 8192/16384 typical

        // choose column count so height <= max
        int columns = Mathf.Max(1, Mathf.CeilToInt(verticesCount / (float)maxTexSize));
        int texHeight = Mathf.CeilToInt(verticesCount / (float)columns);

        // choose width so columns*stride <= max; if not, increase columns and recompute height
        while (columns * adjustedFramesCount > maxTexSize)
        {
            columns++;
            texHeight = Mathf.CeilToInt(verticesCount / (float)columns);
            if (texHeight > maxTexSize)
            {
                Debug.LogError($"VAT too large for GPU max texture size {maxTexSize}. Reduce frames/vertices or bake in pages.");
                bakingInProgress = false;
                yield break;
            }
        }
        int texWidth = columns * adjustedFramesCount;

        Debug.Log($"VAT Layout: {texWidth} x {texHeight}, vertices={verticesCount}, frames={framesCount}, columns={columns}, stride={adjustedFramesCount}");

        // create textures (linear, no mips, nearest, clamp)
        var posFormat = TextureFormat.RGBAFloat; // use RGBAHalf if you must save space
        var posTex = new Texture2D(texWidth, texHeight, posFormat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        Texture2D nrmTex = null;
        if (!UnlitMesh)
        {
            if (CompressNormal) nrmTex = new Texture2D(texWidth, texHeight, TextureFormat.RGBA32,  false, true);
            else                nrmTex = new Texture2D(texWidth, texHeight, TextureFormat.RGBAFloat, false, true);
            nrmTex.filterMode = FilterMode.Point;
            nrmTex.wrapMode   = TextureWrapMode.Clamp;
        }

        // pre-allocate one row buffers to reduce gc
        var posRow = new Color[texWidth];
        var nrmRow = new Color[texWidth];

        // uv2 for frame 0 (index UV)
        var uv2 = new Vector2[verticesCount];
        for (int i = 0; i < verticesCount; i++)
        {
            var coord0 = Coord(0, i, texWidth, texHeight, adjustedFramesCount);
            uv2[i] = UVFromPixel(coord0.x, coord0.y, texWidth, texHeight);
        }
        basisMesh.uv2 = uv2;

        // bounds tracking
        var minB = new Vector3( 1e9f,  1e9f,  1e9f);
        var maxB = new Vector3(-1e9f, -1e9f, -1e9f);

        // bake each frame
        for (int f = 0; f < framesCount; f++)
        {
            progress = f / (float)Mathf.Max(1, framesCount - 1);
            EditorUtility.DisplayProgressBar("Baking VAT", $"Frame {f+1}/{framesCount}", progress);

            float t = StartTime + f / SampleRate;
            timeProp.floatValue = t;
            alembicObject.ApplyModifiedProperties();
            yield return null;

            if (VariableTopology == TopologyType.Variable)
            {
                // take first child mesh; treat as soup; absolute positions
                var child = meshRoot.childCount > 0 ? meshRoot.GetChild(0) : meshRoot;
                var localMF = child.GetComponent<MeshFilter>();
                if (localMF == null && child.childCount > 0) localMF = child.GetChild(0).GetComponent<MeshFilter>();
                if (localMF == null || localMF.sharedMesh == null || localMF.sharedMesh.subMeshCount == 0)
                {
                    // fill row with zeros
                    for (int x = 0; x < texWidth; x++) posRow[x] = new Color(0,0,0,1);
                    posTex.SetPixels(0, 0, texWidth, 1, posRow);
                }
                else
                {
                    var m = localMF.sharedMesh;
                    var v = new List<Vector3>(); m.GetVertices(v);
                    var n = new List<Vector3>(); m.GetNormals(n);
                    var idx = m.GetTriangles(0);

                    // write row-by-row for this frame
                    // We have 'maxTriangleCount*3' target slots. idx.Length may be smaller on some frames.
                    for (int i = 0; i < verticesCount; i++)
                    {
                        Vector3 P = Vector3.zero;
                        Vector3 N = Vector3.up;

                        if (i < idx.Length)
                        {
                            int vi = idx[i];
                            P = v[vi];
                            N = (n.Count == v.Count) ? n[vi] : Vector3.up;

                            if (FromBlender)
                            {
                                P = localMF.transform.TransformPoint(P);
                                N = localMF.transform.TransformDirection(N);
                            }
                        }

                        minB = Vector3.Min(minB, P);
                        maxB = Vector3.Max(maxB, P);

                        // compute pixel coord for (frame=f, vertex=i)
                        var coord = Coord(f, i, texWidth, texHeight, adjustedFramesCount);
                        posTex.SetPixel(coord.x, coord.y, new Color(P.x, P.y, P.z, 1f));

                        if (!UnlitMesh)
                        {
                            Vector3 NN = Vector3.Normalize(N);
                            if (CompressNormal) // pack [−1,1] → [0,1]
                                NN = NN * 0.5f + Vector3.one * 0.5f;

                            nrmTex.SetPixel(coord.x, coord.y, new Color(NN.x, NN.y, NN.z, 1f));
                        }
                    }
                }
            }
            else // Fixed topology: write DELTA = current − basis
            {
                int vOffset = 0;
                for (int i = 0; i < meshRoot.childCount; i++)
                {
                    var child = meshRoot.GetChild(i);
                    var localMF = child.GetComponent<MeshFilter>();
                    if (localMF == null && child.childCount > 0) localMF = child.GetChild(0).GetComponent<MeshFilter>();
                    if (localMF == null || localMF.sharedMesh == null) continue;

                    var m = localMF.sharedMesh;
                    var v = new List<Vector3>(); m.GetVertices(v);
                    var n = new List<Vector3>(); m.GetNormals(n);

                    for (int j = 0; j < v.Count; j++)
                    {
                        int targetIndex = vOffset + j;

                        Vector3 P = v[j];
                        Vector3 N = (n.Count == v.Count) ? n[j] : Vector3.up;

                        if (FromBlender)
                        {
                            P = localMF.transform.TransformPoint(P);
                            N = localMF.transform.TransformDirection(N);
                        }

                        // delta from basis
                        Vector3 baseP = vertsList[targetIndex];
                        Vector3 D = P - baseP;

                        minB = Vector3.Min(minB, D);
                        maxB = Vector3.Max(maxB, D);

                        var coord = Coord(f, targetIndex, texWidth, texHeight, adjustedFramesCount);
                        posTex.SetPixel(coord.x, coord.y, new Color(D.x, D.y, D.z, 1f));

                        if (!UnlitMesh)
                        {
                            Vector3 NN = Vector3.Normalize(N);
                            if (CompressNormal) NN = NN * 0.5f + Vector3.one * 0.5f;
                            nrmTex.SetPixel(coord.x, coord.y, new Color(NN.x, NN.y, NN.z, 1f));
                        }
                    }
                    vOffset += v.Count;
                }
            }
        }

        // finalize
        posTex.Apply(false, false);
        if (!UnlitMesh) nrmTex.Apply(false, false);

        // save EXR (linear)
        var exrFlags = Texture2D.EXRFlags.OutputAsFloat; // switch to OutputAsHalf to halve size
        var posPath = Path.Combine(ExportPath, $"{ExportFilename}_pos.exr");
        File.WriteAllBytes(posPath, posTex.EncodeToEXR(exrFlags));
        string nrmPath = null;
        if (!UnlitMesh)
        {
            nrmPath = Path.Combine(ExportPath, $"{ExportFilename}_nrm.exr");
            if (CompressNormal)
            {
                // RGBA32 is LDR; save as PNG for compactness
                File.WriteAllBytes(nrmPath.Replace(".exr", ".png"), nrmTex.EncodeToPNG());
                nrmPath += " (PNG written)";
            }
            else
            {
                File.WriteAllBytes(nrmPath, nrmTex.EncodeToEXR(exrFlags));
            }
        }

        // mesh bounds (for reference)
        var b = new Bounds();
        b.SetMinMax(minB, maxB);
        basisMesh.bounds = b;

        // save basis mesh as .asset
        var meshAssetPath = Path.Combine(ExportPath, $"{ExportFilename}_basisMesh.asset").Replace("\\","/");
        AssetDatabase.CreateAsset(UnityEngine.Object.Instantiate(basisMesh), meshAssetPath);
        AssetDatabase.SaveAssets();

        // optional FBX (needs com.unity.formats.fbx)
        #if UNITY_FBX_AVAILABLE
        try
        {
            var go = new GameObject(ExportFilename + "_Basis");
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mf.sharedMesh = basisMesh;
            var fbxPath = Path.Combine(ExportPath, $"{ExportFilename}_basisMesh.fbx").Replace("\\","/");
            ModelExporter.ExportObject(fbxPath, go);
            DestroyImmediate(go);
            Debug.Log("Exported FBX: " + fbxPath);
        }
        catch (Exception e)
        {
            Debug.LogWarning("FBX export failed (package missing?): " + e.Message);
        }
        #endif

        // write meta.json
        var meta = new VatMeta
        {
            vertexCount       = verticesCount,
            frameCount        = framesCount,
            fps               = Mathf.RoundToInt(SampleRate),
            texWidth          = texWidth,
            texHeight         = texHeight,
            columns           = columns,
            frameStride       = adjustedFramesCount,
            storeDelta        = (VariableTopology != TopologyType.Variable),
            normalsCompressed = CompressNormal
        };
        var metaJson = JsonUtility.ToJson(meta, true);
        File.WriteAllText(Path.Combine(ExportPath, $"{ExportFilename}_meta.json"), metaJson);

        bakingInProgress = false;
        progress = 1f;
        EditorUtility.ClearProgressBar();

        Debug.Log($"VAT bake done.\nPos: {posPath}\nNrm: {nrmPath}\nMeta: {Path.Combine(ExportPath, ExportFilename + "_meta.json")}\nBasis Mesh: {meshAssetPath}");
        yield return null;
        AssetDatabase.Refresh();
    }
}
