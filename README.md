概况
        本篇介绍使用GPU实例化在URP管线中通过Shader Graph来对草地进行渲染，该方案可以让我们高效渲染上百万个草叶（四边形）。
GPU Instancing
        GPU Instancing的优点是可以在单个绘制调用中渲染多个对象，通过在图形API下调用这些函数，可以避免很多游戏对象的开销。使用GPU Instancing的条件是网格和材质/着色器需要保持不变，但我们可以通过将数据作为数组或缓冲区传递来拥有不同的每个实例属性（例如位置/矩阵、不同的颜色等），然后使用具有SV_InstanceID语义的变量（输入）在着色器中对其进行索引。
        通常，在创建场景时，我们倾向于使用相同的着色器渲染许多不同的模型，因此在 URP或HDRP 中，依靠 SRP Batcher 来优化场景的大部分绘制调用可能会更高效。然而，在某些情况下，GPU 实例化可能会更好，我认为渲染草叶（四边形）就是一个很好的例子。使用这些 GPU 实例化函数，你只能根据传入的边界获得整个视锥体剔除。您通常需要通过计算着色器（后面的部分提供了一个示例）或可能在 C# 中处理您自己的每个实例剔除，但可能拆分为块/单元格/四叉树（可能也可以通过Jobs进行多线程）。
ShaderGraph中的实例化
        ShaderGraph在Unity2021.2之前对实例化的支持非常有限。在Unity2021.2及之后的版本里公开了一个Instance ID节点(ShaderGraph编辑界面按下空格键即可创建需要的node节点)，但他返回unity_InstanceID始终为0，除非着色器采用以下“instancing paths”（实例化路径）之一：
1、对于常规实例化（Graphics.RenderMeshInstanced），主要需要在材质上启用GPU Instancing复选框。但这仅限于1023个实例（由于使用数组）并且每帧上传矩阵，因此与其他函数相比，该函数效率较低。
2、 Graphics.RenderMeshPrimitives和Graphics.RenderMeshIndirect函数，如果着色器支持的话，它们将尝试使用“procedural instancing path”（过程实例化路径）。默认情况下在Shader Graph中是不包括支持的，但我们可以使用几个自定义函数节点来添加。
RenderMeshPrimitives方式实例化设置
C#脚本
        首先需要在CPU端进行一些设置来告诉Unity绘制我们的草。创建一个C#脚本并将其附加到场景中的GameObject上。
using UnityEngine;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;

[ExecuteAlways] //在非runtime的时候渲染草地
public class RenderGrassPrimitives : MonoBehaviour
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

    private void OnEnable()
    {
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
    }

    private void OnDisable()
    {
        if (instancesBuffer != null)
        {
           instancesBuffer.Release();
           instancesBuffer = null;
        }
    }

    #region Instances
    // [LayoutKind.Sequential]
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
             sizeof(float) * 4 * 4  // matrix
          + sizeof(float) * 4       // color
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
          /* Though this also requires some changes on the shader side.
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
        if (instanceCount <= 0) return;
        Graphics.RenderMeshPrimitives(rParams, mesh, 0, instanceCount);
    }
}
- 第8-10行公开出实例化对象个数、需要的Mesh以及材质。
- 第14行引入unity中的RenderParams结构体，该结构体作为第一个参数传递给RenderMeshPrimitives函数。该结构的作用是将一堆与渲染相关的数据（比如worldBounds、shadowCastingMode、receiveShadows、matProps等）收集在一起。
- 第53-79行声明InstanceData结构体。该结构体包含实例的基本变换矩阵以及获取结构体数据size的静态方法。
- 第83-123行根据实例化对象个数来设置每个实例对象的数据，其中第120-122行根据实例化对象个数以及每个InstanceData的size创建一个ComputerBuffer对象，并且将InstanceData数据数组设置给该ComputerBuffer对象，最后给MaterialPropertyBlock对象设置Buffer属性。
- 第129行调用Graphics.RenderMeshPrimitives方法绘制instanceCount个数量的mesh实例。
Shader Graph
新建一个Lit Graph，如下图所示，因为希望草地受光照影响。
[图片]
- Vertex Stage（顶点阶段）
      为了支持实例化，在着色器中使用SV_InstanceID语义来访问渲染的实例。但对于Unity6之前的版本（笔者使用的是2022.3.62f1版本），没有简单方法可以做到这一点。因为它需要包含传递给顶点着色器的结构（Attributes）中，并且作为附加参数传给顶点函数。这些无法在graph中访问，甚至无法通过Custom function访问。
        但是Shader Graph可以通过添加Custom function节点的方式，来通过自定义函数来执行“instance path”。具体步骤如下：
1、在shader graph中创建一个Custom Function节点（空格快捷键）；
2、将Type改为“String”，Name命名为“Procedural”，在Body中填入如下代码，该段代码告诉着色器编译着色器的“procedural instancing”变体；
Out = In;
#pragma multi_compile _ PROCEDURAL_INSTANCING_ON
#pragma instancing_options procedural:InstancingSetup
3、添加另一个Custom Function节点，该节点Type选择“File”模式，包含一个带有InstancingSetup函数的HLSL文件，该文件还声明和索引用于传入每个实例数据的compute buffer。该节点Name设置为“Instancing”，具体代码如下：
// Instancing.hlsl
#ifndef GRASS_INSTANCED_INCLUDED
#define GRASS_INSTANCED_INCLUDED

// Declare structure & buffer for passing per-instance data
// This must match the C# side
struct InstanceData {
    float4x4 m;
    float4 color;
};
StructuredBuffer<InstanceData> _PerInstanceData;

#if UNITY_ANY_INSTANCING_ENABLED

    // Based on ParticlesInstancing
    // https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.shadergraph/Editor/Generation/Targets/BuiltIn/ShaderLibrary/ParticlesInstancing.hlsl
    // and/or
    // https://github.com/TwoTailsGames/Unity-Built-in-Shaders/blob/master/CGIncludes/UnityStandardParticleInstancing.cginc

    void InstancingMatrices(inout float4x4 objectToWorld, out float4x4 worldToObject) {
       InstanceData data = _PerInstanceData[unity_InstanceID];

        // If matrix is relative to Bounds :
       objectToWorld = mul(objectToWorld, data.m);

       // Alternatively, if instanced matrices are stored in world space we can override matrix :
       //objectToWorld = data.m;
       // This would avoid needing an additional World->Object conversion in the graph

       // ----------
        // If World->Object transforms are required :

        //worldToObject = transpose(objectToWorld);
       /*
          Assuming an orthogonal matrix (no scaling), 
          the above would be a cheap way to calculate an inverse matrix
            Otherwise, use the below :
       */
          
       // Calculate Inverse transform matrix :
       float3x3 w2oRotation;
       w2oRotation[0] = objectToWorld[1].yzx * objectToWorld[2].zxy - objectToWorld[1].zxy * objectToWorld[2].yzx;
       w2oRotation[1] = objectToWorld[0].zxy * objectToWorld[2].yzx - objectToWorld[0].yzx * objectToWorld[2].zxy;
       w2oRotation[2] = objectToWorld[0].yzx * objectToWorld[1].zxy - objectToWorld[0].zxy * objectToWorld[1].yzx;

       float det = dot(objectToWorld[0].xyz, w2oRotation[0]);
       w2oRotation = transpose(w2oRotation);
       w2oRotation *= rcp(det);
       float3 w2oPosition = mul(w2oRotation, -objectToWorld._14_24_34);

       worldToObject._11_21_31_41 = float4(w2oRotation._11_21_31, 0.0f);
       worldToObject._12_22_32_42 = float4(w2oRotation._12_22_32, 0.0f);
       worldToObject._13_23_33_43 = float4(w2oRotation._13_23_33, 0.0f);
       worldToObject._14_24_34_44 = float4(w2oPosition, 1.0f);

        /*
          This may be quite expensive and this function runs in both vertex and fragment shader
          (Though if the matrix is unused the compiler might remove? Unsure)
          Could instead calculate inverse matrices on the CPU side and pass them in too
          (Though would mean double the GPU memory is needed)
       */
    }

    void InstancingSetup() {
       /* // For HDRP may also need to remove/override these macros. Untested.
       #undef unity_ObjectToWorld
       #undef unity_WorldToObject
       */
       InstancingMatrices(unity_ObjectToWorld, unity_WorldToObject);
    }

#endif

// Shader Graph Functions

// Just passes the position through, allows us to actually attach this file to the graph.
// Should be placed somewhere in the vertex stage, e.g. right before connecting the object space position.
void Instancing_float(float3 Position, out float3 Out){
    Out = Position;
}
#endif
4、将上述两个Custom Function节点连接到Vertex阶段的Position端口。法线简单设置为(0,1,0)与Normal端口连接，使草的发现与地形的法线匹配（使其着色（光照）相同）。如下图所示：
[图片]
保存shader graph之后，运行程序可以看到渲染出来的草的实例（8000个实例），如下图所示：
[图片]
- Fragment Stage(片元阶段)
对于片元方面，我们可以使用UV0.y应用简单的颜色渐变，并输入到Base Color中，如下图所示：
[图片]
 在 Graph Settings（Graph Inspector 窗口的选项卡）下，可以启用 Alpha Clipping，将一些块添加到主堆栈中。我已将 Alpha 剪辑阈值保留为 0.5。为了控制 Alpha，我应用了草纹理。为了增加变化，这种纹理包含多种草叶形状，如下图所示：
[图片]
        为了随机选择纹理的一部分，可以使用一个FlipBook节点，将Width和Height都设为2（纹理包含2x2草地tiles）。创建一个Instance ID节点和Random Range节点（设置Min为0，Max为4），将Instance ID节点连接Random Range节点，然后再连接到FlipBook节点中的Tile端口，如下图所示：
[图片]
        如上所示，我还使用了 Y 平铺为 0.9 的 Tiling And Offset 节点，连接到 Flipbook 上的 UV 端口。这会稍微拉伸纹理，以避免以前的图块从纹理顶部泄漏。
请注意，在此处使用Instance ID认为Instance是一致的。如果缓冲区已更新，则可能需要在InstanceData 中存储随机值。
        保存shader graph之后，运行程序可以看到渲染出来的草的实例（8000个实例），如下图所示：
[图片]
RenderMeshIndirect方式实例化设置
C#脚本
与RenderMeshPrimitives方式设置基本相同，不同的地方是它还需要一个间接参数的缓冲区来存储一些索引和计数等。这里只列出不同的地方。
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
private void Update()
{
    if (instanceCount <= 0 || instancesBuffer == null) return;
    Graphics.RenderMeshIndirect(rParams, mesh, indirectBuffer, commandCount);
}
- 第6-18行使用GraphicsBuffer.IndirectDrawIndexedArgs结构来保存间接参数，具体间接参数包括：indexCountPerInstance（每个实例的顶点索引数量）、instanceCount（实例化对象的数量）、startIndex（渲染调用的索引缓冲区中使用的起始索引）、baseVertexIndex（基础顶点索引）、startInstance（第一个被渲染的实例的索引值）。
- 第48行调用Graphics.RenderMeshIndirect方法来绘制mesh实例。
GPU Frustum Culling
        在RenderMeshIndirect基础上，可以添加 视锥体剔除（Frustum Culling）以防止实例在摄像机视图之外绘制，并可选择限制渲染的距离。
Compute Shader
首先设置计算着色器，创建一个compute shader，内部代码如下：
#pragma kernel FrustumCullInstances 

#include "InstancingFrustumCull.hlsl" // ShaderGraph include file (assumes this is in the same folder)
// Alternatively, copy structure & buffers. But as we must keep them in sync, it's easier to use an #include
/*
struct InstanceData {
    float4x4 m;
    // Layout must match C# side
};
StructuredBuffer<InstanceData> _PerInstanceData;
*/

AppendStructuredBuffer<uint> _VisibleIDsAppend; // Buffer that holds the indices of visible instances

float4x4 _Matrix; // (matrix passed in should convert to Clip Space e.g. projection * view * model)
float _MaxDrawDistance;
uint _StartOffset;

[numthreads(64, 1, 1)]
void FrustumCullInstances (uint3 id : SV_DispatchThreadID) {
    InstanceData data = _PerInstanceData[_StartOffset + id.x];
    float4 absPosCS = abs(mul(_Matrix, float4(data.m._m03_m13_m23, 1.0)));

    if (   absPosCS.x <= absPosCS.w * 1.1 + 0.5 
        && absPosCS.y <= absPosCS.w * 1.1 + 0.5 // (scaling/padding hides pop-in/out at screen edges)
        && absPosCS.z <= absPosCS.w
        && absPosCS.w <= _MaxDrawDistance      // optional max draw distance
        ){
        // Is inside camera frustum
        _VisibleIDsAppend.Append(_StartOffset + id.x);
        }
}
        为了处理剔除，计算着色器将实例位置（使用_m03_m13_m23从矩阵中提取）转换为Clip space 。这与顶点着色器通常输出到的空间相同.但在这种情况下，使用的投影矩阵将从 C# 脚本传递，而不使用 GL.GetGPUProjectionMatrix，因此遵循 Unity 默认使用的 OpenGL 约定。
        为了测试这个点是否在相机视锥体内，我们只需取 abs() 并使用<=与 W 分量进行比较。当它通过时，我们将索引添加到 AppendStructuredBuffer。
        之所以采用这样的方式比较，是因为摄像机在View space剔除视锥体。在投影矩阵之后，该区域是Clip space中的立方体 （至少在 OpenGL 约定中），XYZ 分量范围为 -W 到 W（其中 W 是该位置的第四个分量）。对于 XY 轴，（0,0） 是屏幕中心。对于 Z 轴，-W 位于近平面，W 位于远平面 。（这意味着相机通常处于小于 -W 的某个 Z 距离处。我们通常不需要摄像机在clip space中的位置，但最好有这种参考点来帮助我们可视化空间）
C#脚本
        需要设置该附加缓冲区，并将其传递给实例化渲染和计算着色器（在检查器中公开并分配）。请注意，属性名称不同，以避免冲突 （在计算着色器中使用#include行） ，因为缓冲区需要在计算着色器中定义为 AppendStructuredBuffer，但在常规着色器中定义为 StructuredBuffer，才能为其编制索引。（因此_VisibleIDsAppend和_VisibleIDs）。
        在Update中我们在AppendBuffer上通过SetCounterValue(0)方法重置，该计数器会统计缓冲区中有多少个元素。然后将MVP矩阵传递给计算着色器并Dispatch以执行它。若要更新通过实例化调用而被使用的实例个数，可以使用 GraphicsBuffer.CopyCount方法将计数器中的实例数据复制到间接参数缓冲区中。其中的第三个参数需要为1 * sizeof(uint)（相当于值 4），正如实例化对象的数量是缓冲区中的第二个参数一样。copy是发生在GPU上的。
代码如下：
public ComputeShader computeFrustumCulling;
...
void SetupInstances(){
        ...
        visibleBuffer = new ComputeBuffer(instanceCount, sizeof(uint), ComputeBufferType.Append);
        // may also want to initalise to showing all instances, not sure how important that is though.        /*
        uint[] ids = new uint[instanceCount];
        ...
        ids[i] = i;
        ...
        visibleBuffer.SetData(ids);
        */}

void OnEnable() {
        ...
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
        computeFrustumCulling.SetInt("_StartOffset", 0); // set to "Start Instance" in indirect args if used}

void OnDisable() {
        ...
        visibleBuffer?.Release();
    visibleBuffer = null;
}

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
//          if (Camera.current != null) {
//             cam = Camera.current;
//          }
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
注意：在创建 ComputeBuffer 时，实际的追加缓冲区大小/长度是固定的。计数器仅允许着色器中的Append()调用覆盖现有数据。如果不重置，追加将继续每帧递增隐藏计数器。它不会越界写入缓冲区，但会导致实例化渲染在崩溃之前绘制许多重叠的实例（因为索引第一个_PerInstanceData 条目）
Shader Graph
        需要调整Custom Function节点中使用的HLSL文件来定义VisibleIDs缓冲区（_VisibleIDs），使用 InstanceID 对其进行索引，并使用结果对_PerInstanceData进行索引：
// InstancingFrustumCull.hlsl
#ifndef GRASS1_INSTANCED_INCLUDED
#define GRASS1_INSTANCED_INCLUDED

// Declare structure & buffer for passing per-instance data
// This must match the C# side
struct InstanceData {
    float4x4 m;
    float4 color;
};
StructuredBuffer<InstanceData> _PerInstanceData;
StructuredBuffer<uint> _VisibleIDs;

#if UNITY_ANY_INSTANCING_ENABLED

    // Based on ParticlesInstancing
    // https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.shadergraph/Editor/Generation/Targets/BuiltIn/ShaderLibrary/ParticlesInstancing.hlsl
    // and/or
    // https://github.com/TwoTailsGames/Unity-Built-in-Shaders/blob/master/CGIncludes/UnityStandardParticleInstancing.cginc

    void InstancingMatrices(inout float4x4 objectToWorld, out float4x4 worldToObject) {
       uint index = _VisibleIDs[unity_InstanceID];
       InstanceData data = _PerInstanceData[index];

        // If matrix is relative to Bounds :
       objectToWorld = mul(objectToWorld, data.m);

       // Alternatively, if instanced matrices are stored in world space we can override matrix :
       //objectToWorld = data.m;
       // This would avoid needing an additional World->Object conversion in the graph

       // ----------
        // If World->Object transforms are required :

        //worldToObject = transpose(objectToWorld);
       /*
          Assuming an orthogonal matrix (no scaling), 
          the above would be a cheap way to calculate an inverse matrix
            Otherwise, use the below :
       */
          
       // Calculate Inverse transform matrix :
       float3x3 w2oRotation;
       w2oRotation[0] = objectToWorld[1].yzx * objectToWorld[2].zxy - objectToWorld[1].zxy * objectToWorld[2].yzx;
       w2oRotation[1] = objectToWorld[0].zxy * objectToWorld[2].yzx - objectToWorld[0].yzx * objectToWorld[2].zxy;
       w2oRotation[2] = objectToWorld[0].yzx * objectToWorld[1].zxy - objectToWorld[0].zxy * objectToWorld[1].yzx;

       float det = dot(objectToWorld[0].xyz, w2oRotation[0]);
       w2oRotation = transpose(w2oRotation);
       w2oRotation *= rcp(det);
       float3 w2oPosition = mul(w2oRotation, -objectToWorld._14_24_34);

       worldToObject._11_21_31_41 = float4(w2oRotation._11_21_31, 0.0f);
       worldToObject._12_22_32_42 = float4(w2oRotation._12_22_32, 0.0f);
       worldToObject._13_23_33_43 = float4(w2oRotation._13_23_33, 0.0f);
       worldToObject._14_24_34_44 = float4(w2oPosition, 1.0f);

        /*
          This may be quite expensive and this function runs in both vertex and fragment shader
          (Though if the matrix is unused the compiler might remove? Unsure)
          Could instead calculate inverse matrices on the CPU side and pass them in too
          (Though would mean double the GPU memory is needed)
       */
    }

    void InstancingSetup() {
       /* // For HDRP may also need to remove/override these macros. Untested.
       #undef unity_ObjectToWorld
       #undef unity_WorldToObject
       */
       InstancingMatrices(unity_ObjectToWorld, unity_WorldToObject);
    }

#endif

// Shader Graph Functions

// Just passes the position through, allows us to actually attach this file to the graph.
// Should be placed somewhere in the vertex stage, e.g. right before connecting the object space position.
void InstancingFrustumCull_float(float3 Position, out float3 Out){
    Out = Position;
}
void InstancingFrustumCullFragment_float(float InstanceID, out float4 Out){
       InstanceData data = _PerInstanceData[InstanceID];
       Out = data.color;
}
// void InstancingFragment_float(out float4 Out){
//     uint index = _VisibleIDs[unity_InstanceID];
//     InstanceData data = _PerInstanceData[index];
//     Out = data.color;
// }
void VisibleID_float(uint InstanceID, out float Out){
       Out = _VisibleIDs[InstanceID];
}
#endif
- 第12行定义StructuredBuffer缓冲区（_VisibleIDs）；
- 第22-23行使用 InstanceID 对_VisibleIDs缓冲区进行索引，得到的结果对_PerInstanceData进行索引来拿到实例化数据；
        由于Instance ID不再是一致的，之前在Shader Graph图中将Instance ID节点连接Random Range节点会从纹理中选择一个随机图块，该图块会随着剔除而闪烁，所以我们需要将Random Range节点删除，这样就不会闪烁。如下图所示：
[图片]
修改完毕之后，运行程序，运行结果如下图所示：
[图片]
