using System;
using UnityEngine;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;

[ExecuteAlways] //在非runtime的时候渲染草地
public class RenderGrassIndirect : MonoBehaviour
{
    public int instanceCount = 1000;
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
    
    #region IndirectArgs
    private GraphicsBuffer indirectBuffer;
    private GraphicsBuffer.IndirectDrawIndexedArgs[] commandData;
    private int commandCount;

    private void SetupIndirectArgs() {
	    commandCount = 1;
	    commandData = new GraphicsBuffer.IndirectDrawIndexedArgs[commandCount];
	    commandData[0].indexCountPerInstance = mesh.GetIndexCount(0);
	    commandData[0].instanceCount = (uint)instanceCount;
	    // and optionally,
	    commandData[0].startIndex = mesh.GetIndexStart(0);
	    commandData[0].baseVertexIndex = mesh.GetBaseVertex(0);
	    commandData[0].startInstance = 0;

	    indirectBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, commandCount, GraphicsBuffer.IndirectDrawIndexedArgs.size);
	    indirectBuffer.SetData(commandData);
    }
    #endregion 

    private void OnEnable()
    {
	    if (instancesBuffer != null) return;
	    
        Vector3 boundsSize = new Vector3(20, 1, 20);
        rParams = new RenderParams(material) {
            worldBounds = new Bounds(transform.position + boundsSize * 0.5f, boundsSize),
            shadowCastingMode = castShadows,
            receiveShadows = receiveShadows,
            matProps = new MaterialPropertyBlock()
        };
        bounds = rParams.worldBounds;
        MPB = rParams.matProps;
        
        SetupInstances();
        SetupIndirectArgs();
    }

    private void OnDisable()
    {
	    instancesBuffer?.Release();
	    instancesBuffer = null;
	    indirectBuffer?.Release();
	    indirectBuffer = null;
    }

    #region Instances
	//[LayoutKind.Sequential]
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

	private void SetupInstances(){
		if (instanceCount <= 0) {
			// Avoid negative or 0 instances, as that will crash Unity
            instanceCount = 1;
        }
		InstanceData[] instances = new InstanceData[instanceCount];
		Vector3 boundsSize = bounds.size;
		for (int i = 0; i < instanceCount; i++) {
			// Random Position
			Vector3 position = new(
                Random.Range(0, boundsSize.x),
                0,
                Random.Range(0, boundsSize.z)
            );

			// Random Rotation around Y axis
			Quaternion rotation = Quaternion.Euler(0, Random.Range(0f, 360f), 0);

			// Random Height
			Vector3 scale = new Vector3(1, Random.Range(0.4f, 0.9f), 1); 

			// Position Offsets
			position.y += scale.y * 0.5f; // (assuming origin of mesh is in center, like Quad primitive)

			position -= boundsSize * 0.5f; // Makes position relative to bounds center
			// Or if you'd prefer to store the matrix positions in world space, instead use :
			//position += transform.position;
			/*	Though this also requires some changes on the shader side.
				e.g. an additional Transform node converting from World to Object space
			*/

			//Matrix4x4 matrix;
			instances[i] = new()
			{
				matrix = Matrix4x4.TRS(position, rotation, scale)
			};
		}
		instancesBuffer = new ComputeBuffer(instanceCount, InstanceData.Size());
		instancesBuffer.SetData(instances);
		MPB.SetBuffer("_PerInstanceData", instancesBuffer);
	}
#endregion 

    private void Update()
    {
        if (instanceCount <= 0 || instancesBuffer == null) return;
        Graphics.RenderMeshIndirect(rParams, mesh, indirectBuffer, commandCount);
    }
}
