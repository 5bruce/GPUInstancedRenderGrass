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
// 		uint index = _VisibleIDs[unity_InstanceID];
// 		InstanceData data = _PerInstanceData[index];
// 		Out = data.color;
// }
void VisibleID_float(uint InstanceID, out float Out){
		Out = _VisibleIDs[InstanceID];
}
#endif