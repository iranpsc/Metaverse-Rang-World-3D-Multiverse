//#if UNITY_EDITOR
//using UnityEditor;
//using UnityEngine;

//[InitializeOnLoad]
//public static class PinkFish_CustomHierarchy
//{
//    static Texture2D UiIcon;
//    static Texture2D NetworkIcon;
//    static Texture2D ChunkIcon;
//    static Texture2D AudioIcon;
//    static Texture2D VolumeIcon;
//    static Texture2D EventIcon;
//    static Texture2D MetaIcon;
//    static Texture2D DefaultIcon;

//    // Static constructor runs automatically when Unity loads the editor
//    static PinkFish_CustomHierarchy()
//    {
//        UiIcon = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Editor/Hierarchy/UiIcon.png");
//        NetworkIcon = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Editor/Hierarchy/NetworkIcon.png");
//        ChunkIcon = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Editor/Hierarchy/ChunkIcon.png");
//        AudioIcon = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Editor/Hierarchy/AudioIcon.png");
//        VolumeIcon = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Editor/Hierarchy/VolumeIcon.png");
//        EventIcon = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Editor/Hierarchy/EventIcon.png");
//        MetaIcon = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Editor/Gizmos/PinkFish_Script.png");
//        DefaultIcon = EditorGUIUtility.IconContent("GameObject Icon").image as Texture2D;

//        EditorApplication.hierarchyWindowItemOnGUI += DrawCustomHierarchy;
//    }

//    private static void DrawCustomHierarchy(int instanceID, Rect selectionRect)
//    {
//        GameObject obj = EditorUtility.InstanceIDToObject(instanceID) as GameObject;
//        if (obj == null) return;

//        // Default values
//        Texture2D icon = DefaultIcon;
//        Color rowColor = Color.clear; // transparent (no color)

//        // Match categories (case-insensitive)
//        string name = obj.name.ToLower();

//        if (name.Contains("ui"))
//        {
//            icon = UiIcon;
//            rowColor = new Color(0.65f, 0.82f, 1f, 0.25f); // light blue
//        }
//        else if (name.Contains("network") || obj.GetComponent("NetworkBehaviour") != null)
//        {
//            icon = NetworkIcon;
//            rowColor = new Color(1f, 0.6f, 0.6f, 0.25f); // light red
//        }
//        else if (name.Contains("chunk"))
//        {
//            icon = ChunkIcon;
//            rowColor = new Color(0.9f, 0.9f, 1f, 0.15f);
//        }
//        else if (name.Contains("audio"))
//        {
//            icon = AudioIcon;
//            rowColor = new Color(1f, 1f, 0.65f, 0.3f); // light yellow
//        }
//        else if (name.Contains("volume"))
//        {
//            icon = VolumeIcon;
//            rowColor = new Color(0.8f, 1f, 0.8f, 0.3f);
//        }
//        else if (name.Contains("event"))
//        {
//            icon = EventIcon;
//            rowColor = new Color(1f, 0.8f, 0.6f, 0.3f);
//        }
//        else if (name.Contains("meta"))
//        {
//            icon = MetaIcon;
//            rowColor = new Color(0.9f, 0.9f, 1f, 0.2f);
//        }

//        // Draw background color (before Unity draws text)
//        if (rowColor.a > 0f)
//            EditorGUI.DrawRect(selectionRect, rowColor);

//        // Icon position — slightly offset so it doesn’t overlap Unity’s default one
//        Rect iconRect = new Rect(selectionRect.x, selectionRect.y, 16, 16);

//        if (icon != null)
//            GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit, true);
//    }
//}
//#endif
