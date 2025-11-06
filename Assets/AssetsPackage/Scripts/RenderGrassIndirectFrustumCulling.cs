using System;
using UnityEngine;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;

[ExecuteAlways] //在非runtime的时候渲染草地
public class RenderGrassIndirectFrustumCulling : MonoBehaviour
{
	public ComputeShader computeFrustumCulling;
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
	    
        Vector3 boundsSize = new Vector3(40, 1, 40);
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
        
        rParams.matProps.SetBuffer("_VisibleIDs", visibleBuffer);
        int kernel = 0;
        /*
		0 to reference first kernel in compute file
		Could alternatively use computeFrustumCulling.FindKernel("FrustumCullInstances");
		and cache that in a private variable for use in Update too
		*/
        computeFrustumCulling.SetBuffer(kernel, "_PerInstanceData", instancesBuffer);
        computeFrustumCulling.SetBuffer(kernel, "_VisibleIDsAppend", visibleBuffer);
        computeFrustumCulling.SetFloat("_MaxDrawDistance", 100);
        computeFrustumCulling.SetInt("_StartOffset", 0); // set to "Start Instance" in indirect args if used
    }

    private void OnDisable()
    {
	    instancesBuffer?.Release();
	    instancesBuffer = null;
	    indirectBuffer?.Release();
	    indirectBuffer = null;
	    
	    visibleBuffer?.Release();
	    visibleBuffer = null;
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
	private ComputeBuffer visibleBuffer;

	private void SetupInstances(){
		if (instanceCount <= 0) {
			// Avoid negative or 0 instances, as that will crash Unity
            instanceCount = 1;
        }

		uint[] ids = new uint[instanceCount];
		
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
			ids[i] = (uint)i;
		}
		instancesBuffer = new ComputeBuffer(instanceCount, InstanceData.Size());
		instancesBuffer.SetData(instances);
		MPB.SetBuffer("_PerInstanceData", instancesBuffer);
		
		visibleBuffer = new ComputeBuffer(instanceCount, sizeof(uint), ComputeBufferType.Append);
		visibleBuffer.SetData(ids);
	}
#endregion 

	private readonly int prop_Matrix = Shader.PropertyToID("_Matrix");
    private void Update()
    {
        if (instanceCount <= 0 || instancesBuffer == null) return;
        // Frustum Culling
        if (computeFrustumCulling != null) {
	        // Reset Visible Buffer
	        visibleBuffer?.SetCounterValue(0);

	        // Set Matrix & Dispatch Compute Shader
	        Camera cam = Camera.main;
// #if UNITY_EDITOR
// 	        if (Camera.current != null) {
// 		        cam = Camera.current;
// 	        }
// #endif 
	        Matrix4x4 m = Matrix4x4.Translate(bounds.center);
	        Matrix4x4 v = cam.worldToCameraMatrix;
	        Matrix4x4 p = cam.projectionMatrix;
	        // With regular shaders you'd normally use GL.GetGPUProjectionMatrix to convert this matrix to graphics API being used
	        // but in this case the compute shader expects the OpenGL convention that Unity uses by default

	        Matrix4x4 mvp = p * v * m;
	        // (If instanced matrices are stored in world space can remove the m matrix here)
	        computeFrustumCulling.SetMatrix(prop_Matrix, mvp);

	        /*
		        Note : If you have multiple objects creating grass,
	            either need to make sure the buffers are set here.
	            Or create & use an instance of the compute shader, e.g.
	            Instantiate(computeFrustumCulling) in Start() & Destroy that in OnDestroy()
	        */

	        uint numthreads = 64;
	        // keep in sync with compute shader, or set automatically with :
	        //computeFrustumCulling.GetKernelThreadGroupSizes(kernel, out numthreads, out _, out _);
	        // (probably in OnEnable & cache in private variable for usage here)

	        int kernel = 0; // or cache computeFrustumCulling.FindKernel("FrustumCullInstances");
	        computeFrustumCulling.Dispatch(kernel, Mathf.CeilToInt(instanceCount / numthreads), 1, 1);

	        // Copy Counter to Instance Count in Indirect Args
	        GraphicsBuffer.CopyCount(visibleBuffer, indirectBuffer, 1 * sizeof(uint));
        }

        Graphics.RenderMeshIndirect(rParams, mesh, indirectBuffer, commandCount);
    }
}
