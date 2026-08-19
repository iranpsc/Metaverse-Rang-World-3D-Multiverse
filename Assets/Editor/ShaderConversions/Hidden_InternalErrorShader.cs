using UnityEditor;
using UnityEngine;
using UnityGLTF;

class Convert_Hidden_InternalErrorShader_to_GLTF
{
	const string shaderName = "Hidden/InternalErrorShader";

	[InitializeOnLoadMethod]
	private static void Register()
	{
		GLTFMaterialHelper.RegisterMaterialConversionToGLTF(ConvertMaterialProperties);
	}

	private static bool ConvertMaterialProperties(Material material, Shader oldShader, Shader newShader)
	{
		if (oldShader.name != shaderName) return false;

		// Reading old shader properties.


		material.shader = newShader;

		// Assigning new shader properties.
		// Uncomment lines you need, and set properties from values from the section above.

		// material.SetColor("baseColorFactor", insert_value_here); // Base Color
		// material.SetTexture("baseColorTexture", insert_value_here); // Base Color Map
		// material.SetVector("baseColorTexture_ST", insert_value_here); // Map Tiling/Offset
		// material.SetFloat("baseColorTextureRotation", insert_value_here); // Map Rotation
		// material.SetFloat("baseColorTextureTexCoord", insert_value_here); // Map UV
		// material.SetFloat("alphaCutoff", insert_value_here); // Alpha Cutoff
		// material.SetFloat("_VERTEX_COLORS", insert_value_here); // Enable Vertex Colors
		// material.SetFloat("_TEXTURE_TRANSFORM", insert_value_here); // Enable Texture Transforms


		// Ensure keywords are correctly set after conversion.
		// Example:
		// if (material.GetFloat("_VERTEX_COLORS") > 0.5f) material.EnableKeyword("_VERTEX_COLORS_ON");

		ShaderGraphHelpers.ValidateMaterialKeywords(material);
		return true;
	}
}
