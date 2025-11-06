using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;

[ExecuteAlways] //在非runtime的时候渲染草地
public class RenderGrassForTerrain : MonoBehaviour
{
    public Terrain terrain;
	public int instanceCount = 8000;
	public Mesh mesh;
	public Material material;
    
	private ShadowCastingMode castShadows = ShadowCastingMode.Off;
	private bool receiveShadows = true;
	private RenderParams rParams;
	private MaterialPropertyBlock MPB
	{
		get => rParams.matProps;
		set => rParams.matProps = value;
	}

	private Bounds bounds
	{
		get => rParams.worldBounds;
		set => rParams.worldBounds = value;
	}
	
	private struct InstanceData {
		public Matrix4x4 matrix;

		public InstanceData(Matrix4x4 trs)
		{
			matrix = trs;
			color = Color.white;
		}
		public Color color;

		public static int Size() {
			return
				sizeof(float) * 4 * 4 	// matrix
				+ sizeof(float) * 4 		// color
				;
			// Alternatively one of these might work to calculate the size automatically?
			// return System.Runtime.InteropServices.Marshal.SizeOf(typeof(InstanceData));
			// return Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<InstanceData>();
		}
		/*
			Must match the layout/size of the struct in shader
			See https://docs.unity3d.com/ScriptReference/ComputeBufferType.Structured.html
			To avoid issues with how different graphics APIs structure data :
			- Order by largest to smallest
			- Use Vector4/Color/float4 & Matrix4x4/float4x4 instead of float3 & float3x3
		*/
	}
	
	private ComputeBuffer instancesBuffer;

	private void SetupInstances() {
		TerrainData terrainData = terrain.terrainData;
		Vector3 terrainSize = terrainData.size;

		InstanceData[] instances = new InstanceData[instanceCount];
		for (int i = 0; i < instanceCount; i++) {
			Vector3 position = new(
				Random.Range(0, terrainSize.x),
				0,
				Random.Range(0, terrainSize.z)
			);

			// Align rotation to terrain normal
			Vector3 normal = terrainData.GetInterpolatedNormal(position.x / terrainSize.x, position.z / terrainSize.z);
			float dot = Mathf.Abs(Vector3.Dot(normal, Vector3.forward));
			float dot2 = Mathf.Abs(Vector3.Dot(normal, Vector3.right));
			Vector3 perp = Vector3.Cross(normal, (dot2 > dot) ? Vector3.right : Vector3.forward);
			Vector3 forward = Quaternion.AngleAxis(Random.Range(0f, 360f), normal) * perp; // Random rotation around normal
			Quaternion rotation = Quaternion.LookRotation(forward, normal);

			Vector3 scale = new Vector3(1, Random.Range(0.4f, 0.9f), 1); // Random Height
			position.y += scale.y * 0.5f; // assuming origin of mesh is in center, like the unity primitive Quad

			// If you want positions stored in world space,
			/*
			position += transform.position;
			position.y += terrain.SampleHeight(position);
			*/
			// else :
			position.y += terrain.SampleHeight(transform.position + position);
			position -= bounds.size * 0.5f; // make position relative to bounds center
			// (SampleHeight still expects world space, hence the "transform.position +")

			instances[i] = new() {
				matrix = Matrix4x4.TRS(position, rotation, scale)
			};
		}

		if (instancesBuffer == null)
			instancesBuffer = new ComputeBuffer(instanceCount, InstanceData.Size());
		
		instancesBuffer.SetData(instances);
		MPB.SetBuffer("_PerInstanceData", instancesBuffer);
	}
	

	void OnEnable() {
		terrain = GetComponent<Terrain>();
		Vector3 boundsSize = terrain.terrainData.size;
		rParams = new RenderParams(material) {
			worldBounds = new Bounds(transform.position + boundsSize * 0.5f, boundsSize),
			shadowCastingMode = castShadows,
			receiveShadows = receiveShadows,
			matProps = new MaterialPropertyBlock()
		};
		bounds = rParams.worldBounds;
		MPB = rParams.matProps;
        
		SetupInstances();
	}

	private void OnDisable()
	{
		if (instancesBuffer != null)
		{
			instancesBuffer.Release();
			instancesBuffer = null;
		}
	}
	
	private void Update()
	{
		if (instanceCount <= 0) return;
		Graphics.RenderMeshPrimitives(rParams, mesh, 0, instanceCount);
	}
}
